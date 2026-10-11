using System.ComponentModel;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Spectre.Console;
using Spectre.Console.Cli;
using TemporalGraph.Cli.Runs;
using TemporalGraph.Llm;

namespace TemporalGraph.Cli.Setup;

/// <param name="Profile">The profile name (<c>Llm:Profiles:&lt;name&gt;</c>).</param>
/// <param name="KeyEnv">The environment variable holding the key, set by the user.</param>
/// <param name="KeyStdin">Read the key from standard input.</param>
public sealed record AddOptions(string? Profile, string? Provider, string? Model, string? Endpoint = null, string? KeyEnv = null, bool KeyStdin = false,
    bool Default = false, bool Pickable = false, bool Indexing = false);

/// <summary>One profile's check in <c>setup check --json</c>.</summary>
/// <param name="Reason">Why the provider account can't serve (<c>credits</c>, <c>key-limit</c>, <c>quota</c>, <c>auth</c>), the API's classification; null otherwise.</param>
/// <param name="FixIt">What to do about <see cref="Reason"/>.</param>
public sealed record CheckEntry(string Profile, bool Configured, bool Passed, string? Model, long? InputTokens, long? OutputTokens, string? Error,
    string? RunId, IReadOnlyList<string> Problems, VetEntry? Vet, string? Reason = null, string? FixIt = null);

public sealed record VetEntry(bool Passed, int ExitCode, string? RunId);

/// <param name="Skipped">Incomplete profiles not checked (without <c>--profile</c>), with what each is missing.</param>
public sealed record CheckReport(bool Passed, IReadOnlyList<CheckEntry> Profiles, IReadOnlyList<SkippedProfile> Skipped);

public sealed record SkippedProfile(string Profile, IReadOnlyList<string> Problems);

/// <summary>
/// The <c>setup</c> commands, apart from the console they run in so the tests can drive them against a temporary store.
/// Exit codes: 0 ok (for <c>status</c>: chat ready), 1 a model call or the store failed, 2 bad input (or a prompt is needed and there is
/// no terminal), 3 not ready. Keys are only ever read from a hidden prompt, an environment variable (<c>--key-env</c>) or standard input
/// (<c>--key-stdin</c>), and never printed: every message that could carry provider text goes through <see cref="SetupService.Redact"/>.
/// </summary>
public sealed partial class SetupRunner(ISecretsStore store, Func<IConfiguration> loadConfig, Func<IReadOnlyList<Prerequisite>> prerequisites,
    IAnsiConsole console, TextWriter output, TextReader input, bool interactive, Func<string> repoRoot)
{
    public const int Ok = 0, Failed = 1, BadInput = 2, NotReady = 3;
    public const string Cli = "dotnet run --project tools/TemporalGraph.Cli --";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly SetupService _service = new(store);

    public static SetupRunner ForConsole() =>
        new(new UserSecretsStore(LlmSettings.UserSecretsId), () => LlmSettings.LoadConfiguration(), Prerequisites.Probe, AnsiConsole.Console,
            Console.Out, Console.In, IsInteractive() && !CliContext.NoInput, RepoPaths.FindRoot);

    /// <summary>A person at a terminal: neither stream redirected (an agent's shell redirects them) and a console that can prompt.</summary>
    public static bool IsInteractive() =>
        !Console.IsInputRedirected && !Console.IsOutputRedirected && AnsiConsole.Profile.Capabilities.Interactive;

    // ── status ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    public int Status(bool json)
    {
        try
        {
            var report = _service.Status(loadConfig(), prerequisites());
            if (json) WriteJson(report);
            else RenderStatus(report);
            return report.Ready ? Ok : NotReady;
        }
        catch (SecretsStoreException e)
        {
            return Error(e.Message, json, Failed);
        }
    }

    private void RenderStatus(StatusReport r)
    {
        var tools = new Table().AddColumns("Prerequisite", "Needed", "Found", "");
        foreach (var p in r.Prerequisites) tools.AddRow(p.Name, p.Needed + " or later", p.Found ?? "not found", p.Ok ? "[green]ok[/]" : "[red]missing[/]");
        console.Write(tools);

        if (r.Profiles.Count > 0)
        {
            var profiles = new Table().AddColumns("Profile", "Provider", "Model", "Endpoint", "Key", "Complete", "Added by setup");
            foreach (var p in r.Profiles)
                profiles.AddRow(Markup.Escape(p.Name), p.Provider ?? "-", Markup.Escape(p.Model ?? "-"), p.Endpoint, p.Key == "set" ? "set" : "[red]missing[/]",
                    p.Complete ? "yes" : "[red]no[/]", p.WrittenBySetup ? "yes" : "no");
            console.Write(profiles);
        }
        else console.MarkupLine("No model profiles yet.");

        var chatName = r.Chat.Profile ?? "the Llm section";
        console.MarkupLine($"Chat: {Markup.Escape(chatName)}{(r.Chat.Model is { } m ? $" ({Markup.Escape(m)})" : "")} — {(r.Chat.Ready ? "[green]ready[/]" : "[red]not ready[/]")}");
        if (r.Chat.Models.Count > 0) console.MarkupLine($"Model picker (Chat:Models): {Markup.Escape(string.Join(", ", r.Chat.Models))}");
        console.MarkupLine($"Indexing: {Markup.Escape(r.Indexing.Profile ?? "the Llm section")} — {(r.Indexing.Ready ? "ready" : "not set up (only needed to index a new book)")}");
        console.MarkupLine($"Gateway mode: {(r.Gateway.Enabled ? "on" : "off")}");
        foreach (var missing in r.Missing) console.MarkupLine($"[yellow]Missing:[/] {Markup.Escape(missing)}");
        console.MarkupLine(r.Ready ? "[green]Ready.[/] Next: [bold]aspire run[/]." : "[red]Not ready.[/]");
    }

    // ── add ────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    public int Add(AddOptions o, bool json) => AddAsync(o, json, CancellationToken.None).GetAwaiter().GetResult();

    public async Task<int> AddAsync(AddOptions o, bool json, CancellationToken ct)
    {
        string? key = null;
        try
        {
            if (string.IsNullOrWhiteSpace(o.Profile)) throw new SetupInputException($"Name the profile: setup add <profile> --provider {Providers.Names} --model <id> --key-env <VAR>.");
            var provider = Providers.Find(o.Provider) ?? throw new SetupInputException($"--provider is required: {Providers.Names}.");
            if (o.KeyEnv is not null && o.KeyStdin) throw new SetupInputException("Use --key-env or --key-stdin, not both.");
            var request = new AddRequest(o.Profile.Trim(), provider.Name, o.Model ?? "", o.Endpoint, "(checked below)", o.Default, o.Pickable, o.Indexing);
            _service.Validate(request);   // everything but the key, before asking for one

            if (o.KeyEnv is { } variable)
            {
                key = Environment.GetEnvironmentVariable(variable.Trim());
                if (string.IsNullOrWhiteSpace(key))
                    throw new SetupInputException($"The environment variable {variable} is not set or is empty. The user sets it in their own shell (never paste a key into a chat or a command line), then rerun.");
            }
            else if (o.KeyStdin)
            {
                key = input.ReadToEnd().Trim();
                if (key.Length == 0) throw new SetupInputException("--key-stdin: nothing was read from standard input.");
            }
            else if (interactive)
            {
                key = await console.AskAsync(new TextPrompt<string>($"API key for [bold]{Markup.Escape(request.Profile)}[/] (hidden; empty: back):").Secret(null).AllowEmpty(), ct);
                if (string.IsNullOrWhiteSpace(key)) throw new SetupInputException("No key was entered; nothing was saved.");
            }
            else
                throw new SetupInputException("No key given, and there is no terminal to ask for one. Have the user put the key in an environment variable themselves, then run:\n  "
                    + AddCommandLine(o, provider));

            var result = _service.Add(request with { Key = key });
            if (json) WriteJson(result);
            else
            {
                console.MarkupLine($"Saved profile [bold]{Markup.Escape(result.Profile)}[/] ({result.Provider}, {Markup.Escape(result.Model)}) to user-secrets store {store.Id}.");
                if (result.Default) console.MarkupLine("It is the chat's default model (Chat:Llm).");
                if (result.Pickable) console.MarkupLine("It is offered in the chat's model picker (Chat:Models).");
                if (result.Indexing) console.MarkupLine("The indexing stages use it (Indexing:Llm).");
                console.MarkupLine($"Check it with [bold]setup check --profile {Markup.Escape(result.Profile)}[/]; the web app starts with [bold]aspire run[/].");
                CliContext.Next(NextAfterSetup);
            }
            return Ok;
        }
        catch (SetupInputException e)
        {
            return Error(e.Message, json, BadInput, key);
        }
        catch (SecretsStoreException e)
        {
            return Error(e.Message, json, Failed, key);
        }
    }

    /// <summary>The non-interactive command for this add, with <c>--key-env</c> and the provider's usual variable name.</summary>
    public static string AddCommandLine(AddOptions o, ProviderInfo provider) =>
        $"{Cli} setup add {o.Profile ?? provider.Name} --provider {provider.Name} --model {o.Model ?? provider.DefaultModel ?? "<deployment-name>"}"
        + (o.Endpoint is { } e ? $" --endpoint {e}" : provider.NeedsEndpoint ? " --endpoint https://<resource>.openai.azure.com/openai/v1/" : "")
        + $" --key-env {o.KeyEnv ?? provider.KeyVariable}"
        + (o.Default ? " --default" : "") + (o.Pickable ? " --pickable" : "") + (o.Indexing ? " --indexing" : "");

    // ── check ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    public async Task<int> CheckAsync(string? profile, bool vet, bool json, CancellationToken ct)
    {
        IConfiguration config;
        List<string?> names;
        var skipped = new List<SkippedProfile>();
        try
        {
            config = loadConfig();
            var report = _service.Status(config, []);
            if (profile is not null)
            {
                if (!report.Profiles.Any(p => string.Equals(p.Name, profile, StringComparison.OrdinalIgnoreCase)))
                    throw new SetupInputException($"No profile named '{profile}'. `setup status` lists them.");
                names = [profile];
            }
            else
            {
                // Every complete profile; an incomplete one (a template, or one without its key) is listed, not called.
                names = report.Profiles.Where(p => p.Complete).Select(p => (string?)p.Name).ToList();
                skipped.AddRange(report.Profiles.Where(p => !p.Complete).Select(p => new SkippedProfile(p.Name, LlmSettings.From(config, p.Name).Validate())));
                if (report.Chat.Profile is null && report.Chat.Ready) names.Insert(0, null);   // the chat uses the Llm section
                if (names.Count == 0)
                    return Error("No complete model profile to check. Run `setup` or `setup add` first (`setup status` shows what is missing).", json, NotReady);
            }
        }
        catch (SetupInputException e)
        {
            return Error(e.Message, json, BadInput);
        }
        catch (SecretsStoreException e)
        {
            return Error(e.Message, json, Failed);
        }

        var values = store.Read();
        var entries = new List<CheckEntry>();
        foreach (var name in names)
        {
            var label = name ?? "Llm";
            if (!json) console.MarkupLine($"Checking [bold]{Markup.Escape(label)}[/]…");
            var r = await LlmCheckCommand.CheckAsync(config, name, captureContent: false, repoRoot(), showStatus: false,
                command: "setup check" + (name is null ? "" : $" --profile {name}"), ct);
            VetEntry? vetEntry = null;
            if (vet && r.Passed)
            {
                var text = new StringWriter();
                var exit = await ChatVetCommand.RunAsync(new ChatVetCommand.Settings { Llm = name }, config, repoRoot(), text, ct);
                var runId = Regex.Match(text.ToString(), @"· run (\S+)") is { Success: true } m ? m.Groups[1].Value : null;
                vetEntry = new VetEntry(exit == 0, exit, runId);
                if (!json) console.Write(new Text(SetupService.Redact(text.ToString(), values)));
            }
            var entry = new CheckEntry(label, r.Configured, r.Passed, r.Model, r.InputTokens, r.OutputTokens,
                r.Error is null ? null : SetupService.Redact(r.Error, values), r.RunId, r.Problems, vetEntry, r.Reason, r.FixIt);
            entries.Add(entry);
            if (!json)
            {
                if (!entry.Configured) console.MarkupLine($"  [red]not configured:[/] {Markup.Escape(string.Join("; ", entry.Problems))}");
                else if (!entry.Passed)
                {
                    console.MarkupLine($"  [red]failed:[/] {Markup.Escape(entry.Error ?? "")} (run {entry.RunId})");
                    if (entry.FixIt is { } fix) console.MarkupLine($"  [yellow]{Markup.Escape(entry.Reason!)}:[/] {Markup.Escape(fix)}");
                }
                else console.MarkupLine($"  [green]ok[/]: {Markup.Escape(entry.Model ?? "model not reported")}, {entry.InputTokens} in / {entry.OutputTokens} out (run {entry.RunId})");
            }
        }

        var passed = entries.All(e => e.Configured && e.Passed && e.Vet is not { Passed: false });
        if (json) WriteJson(new CheckReport(passed, entries, skipped));
        else foreach (var s in skipped) console.MarkupLine($"Skipped [bold]{Markup.Escape(s.Profile)}[/] (incomplete): {Markup.Escape(string.Join("; ", s.Problems))}");
        return entries.Any(e => e.Configured && (!e.Passed || e.Vet is { Passed: false })) ? Failed
            : entries.Any(e => !e.Configured) ? NotReady : Ok;
    }

    // ── reset ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    public int Reset(string? profile, bool yes, bool json) => ResetAsync(profile, yes, json, CancellationToken.None).GetAwaiter().GetResult();

    public async Task<int> ResetAsync(string? profile, bool yes, bool json, CancellationToken ct)
    {
        try
        {
            var plan = _service.PlanReset(profile);
            if (plan.Removed.Count == 0 && plan.Restored.Count == 0)
            {
                if (json) WriteJson(plan);
                else console.MarkupLine("Nothing to reset: setup has written nothing to this store.");
                return Ok;
            }
            if (!yes)
            {
                if (!interactive)
                    throw new SetupInputException($"Reset asks for confirmation, and there is no terminal. Rerun with --yes:\n  {Cli} setup reset{(profile is null ? "" : $" --profile {profile}")} --yes");
                foreach (var k in plan.Removed) console.MarkupLine($"  remove  {Markup.Escape(k)}");
                foreach (var k in plan.Restored) console.MarkupLine($"  restore {Markup.Escape(k)} (to its value before setup)");
                if (!await console.ConfirmAsync("Go ahead?", false, ct))
                {
                    console.MarkupLine("Nothing changed.");
                    return Ok;
                }
            }
            var result = _service.Reset(profile);
            if (json) WriteJson(result);
            else
            {
                console.MarkupLine($"Removed {result.Removed.Count} setting(s){(result.Restored.Count > 0 ? $" and restored {result.Restored.Count}" : "")}; settings setup didn't write are untouched.");
                if (profile is null) console.MarkupLine("The chat now uses whatever was configured before setup (`setup status` shows it).");
            }
            return Ok;
        }
        catch (SetupInputException e)
        {
            return Error(e.Message, json, BadInput);
        }
        catch (SecretsStoreException e)
        {
            return Error(e.Message, json, Failed);
        }
    }

    // ── output ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private void WriteJson(object value) => output.WriteLine(JsonSerializer.Serialize(value, value.GetType(), Json));

    private int Error(string message, bool json, int exit, params string?[] secrets)
    {
        var safe = SetupService.Redact(message, SafeRead(), secrets);
        if (json) WriteJson(new { error = safe, exitCode = exit });
        else console.MarkupLine($"[red]{Markup.Escape(safe)}[/]");
        return exit;
    }

    private IReadOnlyDictionary<string, string> SafeRead()
    {
        try { return store.Read(); }
        catch (Exception) { return new Dictionary<string, string>(); }
    }
}

// ── Spectre commands ───────────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Spectre's parser accepts an option it doesn't know and leaves it in <see cref="CommandContext.Remaining"/>, so a
/// misspelt option (<c>--jsn</c>) would run the command without it. A setup command refuses instead: exit 2 (bad input), with the option
/// named, before it reads or writes anything. (A missing value is the parser's own error, which <see cref="CliApp.ExitCode"/> makes 2.)
/// </summary>
public static class SetupArguments
{
    public static int? Reject(CommandContext context, bool json)
    {
        var unknown = context.Remaining.Parsed.Select(g => (g.Key.StartsWith('-') ? g.Key : (g.Key.Length == 1 ? "-" : "--") + g.Key))
            .Concat(context.Remaining.Raw).Distinct().ToList();
        if (unknown.Count == 0) return null;
        var message = $"Unknown option{(unknown.Count == 1 ? "" : "s")}: {string.Join(" ", unknown)}. `setup {context.Name} --help` lists the options.";
        if (json) Console.Out.WriteLine(JsonSerializer.Serialize(new { error = message, exitCode = SetupRunner.BadInput }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        else AnsiConsole.MarkupLine($"[red]{Markup.Escape(message)}[/]");
        return SetupRunner.BadInput;
    }

    /// <summary>Whether the arguments ask for JSON (<c>--json</c>), read before the settings are trusted.</summary>
    public static bool WantsJson(CommandContext context) => context.Arguments.Contains("--json");
}

public sealed class SetupCommand : AsyncCommand<SetupCommand.Settings>
{
    public sealed class Settings : GlobalSettings;

    public override Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken) =>
        SetupArguments.Reject(context, SetupArguments.WantsJson(context)) is { } bad ? Task.FromResult(bad) : SetupRunner.ForConsole().WalkthroughAsync(cancellationToken);
}

public sealed class SetupStatusCommand : Command<SetupStatusCommand.Settings>
{
    public sealed class Settings : GlobalSettings
    {
    }

    public override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken) =>
        SetupArguments.Reject(context, settings.Json) ?? SetupRunner.ForConsole().Status(settings.Json);
}

public sealed class SetupAddCommand : AsyncCommand<SetupAddCommand.Settings>
{
    public sealed class Settings : GlobalSettings
    {
        [CommandArgument(0, "[profile]")][Description("The profile name (Llm:Profiles:<name>): letters, digits, '-' and '_'.")] public string? Profile { get; init; }
        [CommandOption("--provider <PROVIDER>")][Description("openai, anthropic, azure or openrouter.")] public string? Provider { get; init; }
        [CommandOption("--model <ID>")][Description("The model id; for Azure, the deployment name.")] public string? Model { get; init; }
        [CommandOption("--endpoint <URL>")][Description("Azure: https://<resource>.openai.azure.com/openai/v1/ (required). OpenAI: an OpenAI-compatible endpoint (optional).")] public string? Endpoint { get; init; }
        [CommandOption("--key-env <VAR>")][Description("Read the API key from this environment variable (which the user sets). A key is never a command-line argument.")] public string? KeyEnv { get; init; }
        [CommandOption("--key-stdin")][Description("Read the API key from standard input.")] public bool KeyStdin { get; init; }
        [CommandOption("--default")][Description("Make it the chat's default model (Chat:Llm).")] public bool Default { get; init; }
        [CommandOption("--pickable")][Description("Offer it in the chat's model picker (Chat:Models).")] public bool Pickable { get; init; }
        [CommandOption("--indexing")][Description("Use it for the indexing stages (Indexing:Llm).")] public bool Indexing { get; init; }
    }

    public override Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken cancellationToken) =>
        SetupArguments.Reject(context, s.Json) is { } bad ? Task.FromResult(bad)
            : SetupRunner.ForConsole().AddAsync(new AddOptions(s.Profile, s.Provider, s.Model, s.Endpoint, s.KeyEnv, s.KeyStdin, s.Default, s.Pickable, s.Indexing), s.Json, cancellationToken);
}

public sealed class SetupCheckCommand : AsyncCommand<SetupCheckCommand.Settings>
{
    public sealed class Settings : GlobalSettings
    {
        [CommandOption("--profile <NAME>")][Description("Check one profile; default: every configured profile.")] public string? Profile { get; init; }
        [CommandOption("--vet")][Description("Also run `chat vet` (about 22 calls per profile).")] public bool Vet { get; init; }
    }

    public override Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken cancellationToken) =>
        SetupArguments.Reject(context, s.Json) is { } bad ? Task.FromResult(bad) : SetupRunner.ForConsole().CheckAsync(s.Profile, s.Vet, s.Json, cancellationToken);
}

public sealed class SetupResetCommand : AsyncCommand<SetupResetCommand.Settings>
{
    public sealed class Settings : GlobalSettings
    {
        [CommandOption("--profile <NAME>")][Description("Remove one profile setup created; default: everything setup wrote.")] public string? Profile { get; init; }
        [CommandOption("--yes")][Description("Don't ask for confirmation.")] public bool Yes { get; init; }
    }

    public override Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken cancellationToken) =>
        SetupArguments.Reject(context, s.Json) is { } bad ? Task.FromResult(bad) : SetupRunner.ForConsole().ResetAsync(s.Profile, s.Yes, s.Json, cancellationToken);
}
