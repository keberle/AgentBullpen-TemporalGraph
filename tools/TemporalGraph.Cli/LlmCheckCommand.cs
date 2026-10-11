using System.ComponentModel;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.AI;
using Spectre.Console;
using Spectre.Console.Cli;
using TemporalGraph.Llm;
using TemporalGraph.Cli.Runs;
using TemporalGraph.Cli.Telemetry;

namespace TemporalGraph.Cli;

/// <summary>
/// <c>llm-check</c>: one short fixed prompt to the configured LLM, run through a Microsoft Agent Framework
/// agent, to prove the dev provider works. Prints the reply, the model id and token counts, and writes a run
/// folder (run.json, calls.jsonl, cost.json). Never prints or records the API key.
/// </summary>
public sealed class LlmCheckCommand : AsyncCommand<LlmCheckCommand.Settings>
{
    public const string Prompt = "Reply with the single word: ready";
    public const string Stage = "llm-check";
    public const string CaptureContentVariable = "OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT";

    public sealed class Settings : GlobalSettings
    {
        [CommandOption("--capture-content")]
        [Description($"Record prompts and replies on the GenAI span (dashboard GenAI visualizer). Also on with {CaptureContentVariable}=true.")]
        public bool CaptureContent { get; init; }

        [CommandOption("--llm <NAME>")][Description("A named LLM profile (Llm:Profiles:<name>); default: the Llm section.")] public string? Llm { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var captureContent = settings.CaptureContent
            || string.Equals(Environment.GetEnvironmentVariable(CaptureContentVariable), "true", StringComparison.OrdinalIgnoreCase);
        var result = await CheckAsync(LlmSettings.LoadConfiguration(), settings.Llm, captureContent, RepoPaths.FindRoot(), showStatus: true,
            command: Stage + (settings.CaptureContent ? " --capture-content" : "") + (settings.Llm is { } p ? $" --llm {p}" : ""), cancellationToken);
        if (!result.Configured) return NotConfigured(result.Problems);

        if (!result.Passed)
        {
            // The message carries the service's error (status, code), never the request headers.
            AnsiConsole.MarkupLine($"[red]LLM call failed:[/] {Markup.Escape(result.Error ?? "")}");
            if (result.FixIt is { } fix) AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(result.Reason!)}:[/] {Markup.Escape(fix)}");
            AnsiConsole.MarkupLine($"Run [bold]{result.RunId}[/]: {Markup.Escape(result.RunPath ?? "")}");
            return 1;
        }

        var table = new Table().AddColumns("Prompt", "Reply", "Model", "Input tokens", "Output tokens");
        table.AddRow(
            Markup.Escape(Prompt),
            Markup.Escape(result.Reply ?? ""),
            Markup.Escape(result.Model ?? "(not reported)"),
            result.InputTokens?.ToString() ?? "-",
            result.OutputTokens?.ToString() ?? "-");
        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"Run [bold]{result.RunId}[/]: {Markup.Escape(result.RunPath ?? "")}");
        return 0;
    }

    /// <summary>The outcome of one check. <see cref="Error"/> is the exception's type and message (the service's error, never a header).
    /// <see cref="Reason"/> is the provider-account failure (<c>credits</c>, <c>key-limit</c>, <c>quota</c>, <c>auth</c>), as the
    /// API's standalone chat classifies it, and <see cref="FixIt"/> what to do about it.</summary>
    public sealed record Result(string? Profile, bool Configured, IReadOnlyList<string> Problems, bool Passed, string? Reply = null, string? Model = null,
        long? InputTokens = null, long? OutputTokens = null, string? Error = null, string? RunId = null, string? RunPath = null, string? Reason = null,
        string? FixIt = null);

    /// <summary>
    /// The check itself, shared with <c>setup check</c>: validates the profile, sends <see cref="Prompt"/>, writes the run folder
    /// (run.json, calls.jsonl, cost.json). Prints nothing except the "Calling the LLM…" status when <paramref name="showStatus"/>.
    /// </summary>
    public static async Task<Result> CheckAsync(IConfiguration config, string? profile, bool captureContent, string root, bool showStatus, string command,
        CancellationToken cancellationToken)
    {
        var llm = LlmSettings.From(config, profile);
        var problems = llm.Validate();
        if (problems.Count > 0) return new Result(llm.Profile, false, problems, false);

        var run = RunFolder.Create(RepoPaths.Runs(root));
        var startedAt = DateTimeOffset.UtcNow;

        using var span = IndexingTelemetry.Source.StartActivity(Stage);
        span?.SetTag(IndexingTelemetry.RunId, run.RunId);
        span?.SetTag(IndexingTelemetry.Stage, Stage);

        // One call: SDK retries off; our pipeline retries on 429/5xx, visibly. Same sampling settings as the stages.
        var callContext = new LlmCallContext { Run = run, Stage = Stage };
        var sampling = LlmSampling.From(config, llm);
        IndexingShared.TagSampling(span, sampling);
        IndexingShared.TagLlm(span, llm);
        using var client = ChatClientFactory.Create(llm, maxRetries: 0, new LlmPipelineOptions(captureContent, callContext, Sampling: sampling));
        var agent = client.AsAIAgent(name: "llm-check");

        AgentResponse? response = null;
        Exception? failure = null;
        try
        {
            response = showStatus
                ? await AnsiConsole.Status().StartAsync("Calling the LLM…", _ => agent.RunAsync(Prompt, cancellationToken: cancellationToken))
                : await agent.RunAsync(Prompt, cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            failure = ex;
        }

        var log = client.GetService<CallLogChatClient>()!;
        run.Write(RunFolder.RunJson, new
        {
            runId = run.RunId,
            command,
            startedAt,
            finishedAt = DateTimeOffset.UtcNow,
            git = GitInfo.Current(root) is var git ? new { commit = git.Sha, dirty = git.Dirty } : null,
            settings = new { llm = new { profile = llm.Profile, type = llm.Type, endpoint = llm.Endpoint, model = llm.Model, policyId = llm.PolicyId }, captureContent, sampling = sampling.Record() },
            model = response is null ? null : ModelId(response),
            passed = failure is null,
        });
        run.Write(RunFolder.CostJson, Pricing.From(config).CostReport(log.Calls, log.InputTokens, log.OutputTokens));

        if (failure is not null)
        {
            span?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, failure.GetType().Name);
            var classified = ProviderFailures.Classify(failure);
            return new Result(llm.Profile, true, [], false, Error: $"{failure.GetType().Name}: {failure.Message}", RunId: run.RunId, RunPath: run.Path,
                Reason: classified?.Reason,
                FixIt: classified is null ? null : ProviderFailures.FixIt(ProviderFailures.Label(Setup.Providers.Classify(llm.Type, llm.Endpoint)), classified.Reason));
        }
        return new Result(llm.Profile, true, [], true, response!.Text, ModelId(response), response.Usage?.InputTokenCount, response.Usage?.OutputTokenCount,
            RunId: run.RunId, RunPath: run.Path);
    }

    /// <summary>Prints "The LLM provider is not configured." with the problems (no values) and returns exit code 2 (also used by chat).</summary>
    public static int NotConfigured(IReadOnlyList<string> problems)
    {
        AnsiConsole.MarkupLine("[red]The LLM provider is not configured.[/]");
        foreach (var p in problems) AnsiConsole.MarkupLine($"  - {Markup.Escape(p)}");
        AnsiConsole.MarkupLine($"Run `{Markup.Escape(CliName.Wrapper)} setup` (or see \"Dev LLM provider\" in README.md).");
        // No model configured is "not ready".
        return CliContext.Problem(Exit.NotReady, "The LLM provider is not configured: " + string.Join("; ", problems) + $". Run `{CliName.Wrapper} setup`.", "model-not-configured");
    }

    /// <summary>AgentResponse has no model id; a ChatClientAgent's raw representation is the ChatResponse, which does.</summary>
    public static string? ModelId(AgentResponse response) => (response.RawRepresentation as ChatResponse)?.ModelId;
}
