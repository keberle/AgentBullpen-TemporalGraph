using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Spectre.Console;
using Spectre.Console.Cli;
using TemporalGraph.Chat;
using TemporalGraph.Llm;
using TemporalGraph.Cli.Runs;
using TemporalGraph.Cli.Telemetry;
using TemporalGraph.Data;
using TemporalGraph.Query;

namespace TemporalGraph.Cli;

/// <summary>
/// <c>chat --book &lt;id&gt; | --db &lt;path&gt; --chapter N --character "&lt;name&gt;" [--spoilers] (--message "&lt;text&gt;" | --script &lt;path&gt;) [--json] [--capture-content]</c>:
/// talk to a character whose knowledge comes only from the graph as of the end of chapter N, through the committed queries
/// as typed tools (Microsoft Agent Framework agent; the model never writes Cypher). v1 gate = reader reveal time: without
/// <c>--spoilers</c> the tools return only items with <c>revealedPos &lt;= $pos</c>; there is no per-character knowledge yet.
/// Writes a run folder: run.json, calls.jsonl, cost.json, chat.jsonl, knowledge-pack.txt.
/// The knowledge pack (the character's own lookups, run by code and sent once; <c>Chat:KnowledgePack</c>) and a forced tool
/// call on each turn's first model call (<c>Chat:RequireToolFirst</c>); both on by default.
/// </summary>
public sealed class ChatCommand : AsyncCommand<ChatCommand.Settings>
{
    public const string Stage = "chat";
    public const string KnowledgePackTxt = "knowledge-pack.txt";

    /// <summary><c>Chat:MaxRows</c> when not configured (80, was 50).</summary>
    public const int DefaultMaxRows = ChatSettings.DefaultMaxRows;

    public sealed class Settings : GlobalSettings
    {
        [CommandOption("--book <ID>")][Description("Open data/books/<id>.sqlite.")] public string? Book { get; init; }
        [CommandOption("--db <PATH>")][Description("Open a run's book.sqlite (the book id comes from the run's run.json).")] public string? Db { get; init; }
        [CommandOption("--chapter <N>")][Description("The reader has read up to the end of chapter N ($pos).")] public int? Chapter { get; init; }
        [CommandOption("--character <NAME>")][Description("A canonical name or any alias.")] public string? Character { get; init; }
        [CommandOption("--spoilers")][Description("Let the tools return everything true in the story so far, not only what the reader has been shown. The character still answers only from what it knows: spoilers moves the clock, not who knows what.")] public bool Spoilers { get; init; }
        [CommandOption("--message <TEXT>")][Description("One question.")] public string? Message { get; init; }
        [CommandOption("--script <PATH>")][Description("A UTF-8 file, one question per non-blank line; # starts a comment. One conversation.")] public string? Script { get; init; }
        [CommandOption("--capture-content")]
        [Description($"Record prompts and replies on the GenAI span. Also on with {LlmCheckCommand.CaptureContentVariable}=true.")]
        public bool CaptureContent { get; init; }

        [CommandOption("--llm <NAME>")][Description("A named LLM profile (Llm:Profiles:<name>); default: Chat:Llm, else the Llm section.")] public string? Llm { get; init; }
    }

    public override Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken) =>
        RunAsync(settings, LlmSettings.LoadConfiguration(), RepoPaths.FindRoot(), Console.Out, cancellationToken);

    /// <param name="config">The CLI configuration (appsettings + user-secrets + environment); tests pass their own.</param>
    /// <param name="root">The repo root: data/books/, queries/, runs/.</param>
    /// <param name="output">Transcript or JSON output.</param>
    internal static async Task<int> RunAsync(Settings settings, IConfiguration config, string root, TextWriter output, CancellationToken ct, string? anthropicBaseUrl = null)
    {
        // --llm, else Chat:Llm, else the Llm section.
        var llm = LlmSettings.From(config, settings.Llm ?? ChatSettings.LlmProfile(config));
        var problems = llm.Validate();
        if (problems.Count > 0) return LlmCheckCommand.NotConfigured(problems);

        // Arguments.
        // No --message and no --script → the chat console at a terminal (it asks for what is missing); without one, exit 2.
        if (settings.Message is null && settings.Script is null)
        {
            if (!CliContext.Interactive)
                return CliContext.NeedsTerminal("chat without --message or --script opens the chat console",
                    $"chat --book {settings.Book ?? "<id>"} --chapter {settings.Chapter?.ToString() ?? "<N>"} --character \"{settings.Character ?? "<name>"}\" --message \"<text>\" [--spoilers] [--json]");
            if (settings.Db is not null) return IndexingShared.Fail("The chat console opens a book by id: give --book <id> (--db is for --message and --script).");
            return await Repl.ChatConsole.OpenAsync(root, config, settings.Book, settings.Chapter, settings.Character, settings.Spoilers ? true : null, settings.Llm, ct);
        }
        if ((settings.Book is null) == (settings.Db is null)) return IndexingShared.Fail("Give --book <id> or --db <path>.");
        if (settings.Chapter is null) return IndexingShared.Fail("Give --chapter <N>.");
        if (string.IsNullOrWhiteSpace(settings.Character)) return IndexingShared.Fail("Give --character \"<name>\".");
        if (settings.Message is not null && settings.Script is not null) return IndexingShared.Fail("Give --message \"<text>\" or --script <path>, not both.");

        var dbPath = settings.Book is { } b ? Path.Combine(RepoPaths.Books(root), b + ".sqlite") : Resolve(root, settings.Db!);
        if (!File.Exists(dbPath))
        {
            // An id the manifest does not have is bad input; a book that has no database yet is not ready.
            var manifestFile = Path.Combine(RepoPaths.Books(root), BookManifest.FileName);
            var listed = settings.Book is null || (File.Exists(manifestFile) && BookManifest.Read(manifestFile).Any(e => e.Id == settings.Book));
            return listed ? CliContext.Fail(Exit.NotReady, $"Book database not found: {dbPath}", "book-not-indexed")
                : IndexingShared.Fail($"Unknown book '{settings.Book}' (not in data/books/books.json).");
        }
        var bookId = settings.Book ?? QueryCommand.BookOfRun(dbPath);
        if (bookId is null) return IndexingShared.Fail($"No book id: {dbPath} has no run.json next to it with a \"book\".");
        var manifest = Path.Combine(RepoPaths.Books(root), BookManifest.FileName);
        var book = File.Exists(manifest) ? BookManifest.Read(manifest).FirstOrDefault(e => e.Id == bookId) : null;
        if (book is null) return IndexingShared.Fail($"Unknown book '{bookId}' (not in data/books/books.json).");

        var queriesDir = Path.Combine(root, "queries", bookId);
        var missing = ChatTools.MissingFiles(queriesDir);
        if (missing.Count > 0) return CliContext.Fail(Exit.NotReady, $"queries/{bookId}/ is missing: {string.Join(", ", missing)}.", "no-queries");

        IReadOnlyList<string> questions;
        if (settings.Script is { } script)
        {
            var path = Resolve(root, script);
            if (!File.Exists(path)) return IndexingShared.Fail($"Script not found: {script}");
            questions = ChatScript.Parse(File.ReadAllText(path, System.Text.Encoding.UTF8));
            if (questions.Count == 0) return IndexingShared.Fail($"No questions in {script}.");
        }
        else
        {
            if (string.IsNullOrWhiteSpace(settings.Message)) return IndexingShared.Fail("--message is empty.");
            questions = [settings.Message!.Trim()];
        }

        var graph = ChatCharacters.WithRosterNames(BookGraph.Load(dbPath), book.Chat);   // The roster's spelling once revealed
        var chapter = settings.Chapter.Value;
        if (!graph.Chapters.ContainsKey(chapter)) return IndexingShared.Fail($"No chapter {chapter} in the chapters table.");
        var characters = ChatCharacters.Load(dbPath, graph);
        if (characters.Count == 0)
            return CliContext.Fail(Exit.NotReady, $"The characters table of '{bookId}' is empty (no graph has been promoted for it yet): there is no character to talk to.", "book-not-indexed");
        var character = ChatCharacters.Resolve(characters, settings.Character!);
        if (character is null)
            return IndexingShared.Fail($"Unknown character '{settings.Character}'. Characters: {string.Join(", ", characters.Select(c => c.Name))}.");

        // Run folder and telemetry.
        var captureContent = settings.CaptureContent
            || string.Equals(Environment.GetEnvironmentVariable(LlmCheckCommand.CaptureContentVariable), "true", StringComparison.OrdinalIgnoreCase);
        var run = RunFolder.Create(RepoPaths.Runs(root));
        File.WriteAllText(run.File(ChatJsonl.FileName), "");
        var chatLog = new ChatJsonl(run);
        var startedAt = DateTimeOffset.UtcNow;
        using var span = IndexingTelemetry.Source.StartActivity(Stage);
        span?.SetTag(IndexingTelemetry.RunId, run.RunId);
        span?.SetTag(IndexingTelemetry.Stage, Stage);
        span?.SetTag(IndexingTelemetry.BookId, bookId);
        span?.SetTag(IndexingTelemetry.Chapter, chapter);
        span?.SetTag("chat.character", character);
        span?.SetTag("chat.spoilers", settings.Spoilers);

        // The shared setup: display name, prompt, tools, and the pack with $character = the canonical name
        // if it is one of the character's aliases.
        long pos = graph.Chapters[chapter].CharEnd;
        var setup = ChatSetup.Create(book, graph, characters, character, ChatTools.LoadQueries(queriesDir), ChatCharacters.LoadLocations(dbPath), chapter,
            settings.Spoilers, config, onCall: call => chatLog.Tool(call, pos, settings.Spoilers), voice: BookVoice.LoadFolder(book, RepoPaths.Books(root)));
        var (tools, displayName, systemPrompt) = (setup.Tools, setup.DisplayName, setup.SystemPrompt);
        var (usePack, requireToolFirst, maxRows) = (setup.Settings.KnowledgePack, setup.Settings.RequireToolFirst, setup.Settings.MaxRows);
        var packText = setup.PackText;
        var packSections = setup.PackSections;
        if (packText is not null)
        {
            File.WriteAllText(run.File(KnowledgePackTxt), packText + "\n");
            chatLog.Pack(packSections, packText.Length, pos, settings.Spoilers);
        }

        // SDK retries off; the pipeline retries 429/5xx, visibly. Same sampling as the stages.
        var callContext = new LlmCallContext { Run = run, Stage = Stage, BookId = bookId, Chapter = chapter };
        var sampling = LlmSampling.From(config, llm);
        IndexingShared.TagSampling(span, sampling);
        IndexingShared.TagLlm(span, llm);
        using var client = ChatClientFactory.Create(llm, maxRetries: 0, anthropicBaseUrl, new LlmPipelineOptions(captureContent, callContext, Sampling: sampling));
        var conversation = setup.Conversation(client, chatLog);
        if (!settings.Json && packText is not null)
            await output.WriteLineAsync($"Knowledge pack (sent once): {string.Join(" · ", packSections.Select(s => $"{s.Heading} {s.Rows}"))}{(setup.PackLateRowsSuppressed > 0 ? $" · {setup.PackLateRowsSuppressed} late rows hidden" : "")} · {packText.Length} chars ({run.File(KnowledgePackTxt)})\n");

        var turns = new List<ChatTurn>();
        Exception? failure = null;
        for (var i = 0; i < questions.Count && failure is null; i++)
        {
            try
            {
                var turn = await conversation.AskAsync(i + 1, questions[i], ct);
                turns.Add(turn);
                if (!settings.Json) WriteTurn(output, turn);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                failure = ex;
            }
        }

        var log = client.GetService<CallLogChatClient>()!;
        var model = turns.Select(t => t.ModelId).LastOrDefault(m => m is not null);
        var git = GitInfo.Current(root);
        run.Write(RunFolder.RunJson, new
        {
            runId = run.RunId,
            command = CommandLine(settings),
            startedAt,
            finishedAt = DateTimeOffset.UtcNow,
            git = new { commit = git.Sha, dirty = git.Dirty },
            book = bookId,
            db = settings.Db is null ? null : Path.GetRelativePath(root, dbPath).Replace('\\', '/'),
            chapter,
            pos = tools.Pos,
            character,
            displayName,
            spoilers = settings.Spoilers,
            settings = new { llm = new { profile = llm.Profile, type = llm.Type, endpoint = llm.Endpoint, model = llm.Model, policyId = llm.PolicyId }, captureContent, maxRows, sampling = sampling.Record(), knowledgePack = usePack, requireToolFirst },
            model,
            systemPrompt,
            // The first call's input tokens: system prompt + tool schemas + the pack + the first question.
            knowledgePackTokens = usePack ? log.FirstInputTokens : null,
            knowledgePack = usePack ? new { chars = packText!.Length, sections = packSections.Select(s => new { heading = s.Heading, tool = s.Tool, rows = s.Rows, lateRowsSuppressed = s.LateRowsSuppressed }) } : null,
            lateRowsSuppressed = setup.PackLateRowsSuppressed + tools.Calls.Sum(c => c.LateRowsSuppressed),
            questions = questions.Count,
            passed = failure is null,
        });
        run.Write(RunFolder.CostJson, Pricing.From(config).CostReport(log.Calls, log.InputTokens, log.OutputTokens));

        if (settings.Json)
            await output.WriteLineAsync(Json(book, chapter, character, settings.Spoilers, model, turns, log, run, failure, packSections, usePack, requireToolFirst)
                .ToJsonString(new JsonSerializerOptions(RunFolder.Json) { WriteIndented = true }));
        else
        {
            await output.WriteLineAsync($"Model {model ?? "(not reported)"} · {turns.Count} turn(s) · {tools.Calls.Count} tool call(s) · {log.InputTokens} input / {log.OutputTokens} output tokens");
            await output.WriteLineAsync($"Run {run.RunId}: {run.Path}");
        }

        if (failure is not null)
        {
            AnsiConsole.MarkupLine($"[red]LLM call failed:[/] {Markup.Escape(failure.GetType().Name)}: {Markup.Escape(failure.Message)}");
            CliContext.Problem(Exit.Failed, $"LLM call failed: {failure.GetType().Name}: {failure.Message}", "model-call-failed");
            span?.SetStatus(ActivityStatusCode.Error, failure.GetType().Name);
            return 1;
        }
        return 0;
    }

    /// <summary>Q, A, then the turn's tool calls indented: <c>tool(args) → N rows</c>.</summary>
    public static void WriteTurn(TextWriter output, ChatTurn turn)
    {
        output.WriteLine($"Q{turn.Turn}: {turn.Question}");
        output.WriteLine($"A{turn.Turn}: {turn.Answer}");
        if (turn.ToolCalls.Count == 0) output.WriteLine("    (no tool calls)");
        foreach (var c in turn.ToolCalls) output.WriteLine("    " + Describe(c));
        if (turn.LateRowsSuppressed > 0) output.WriteLine($"    late rows suppressed: {turn.LateRowsSuppressed}");
        output.WriteLine($"    tokens: {turn.InputTokens} input, {turn.OutputTokens} output");
        output.WriteLine();
    }

    /// <summary><c>facts_about(character: "Holmes") → 12 rows</c>, with "(resolved as "X")", "(unknown name)" or "(truncated)", and the late
    /// rows hidden.</summary>
    public static string Describe(ToolCallRecord c) =>
        $"{c.Tool}({string.Join(", ", c.Args.Select(a => $"{a.Key}: \"{a.Value}\""))}) → {c.Rows} row{(c.Rows == 1 ? "" : "s")}"
        + (c.ResolvedName is { } r ? $" (resolved as \"{r}\")" : "")
        + (c.UnknownName ? " (unknown name)" : c.Truncated ? " (truncated)" : "")
        + (c.LateRowsSuppressed > 0 ? $" ({c.LateRowsSuppressed} late row{(c.LateRowsSuppressed == 1 ? "" : "s")} hidden)" : "");

    /// <summary>The --json object: {book, chapter, character, spoilers, model, knowledgePack, requireToolFirst, turns, totals, runId}.</summary>
    public static JsonObject Json(BookEntry book, int chapter, string character, bool spoilers, string? model, IReadOnlyList<ChatTurn> turns,
        CallLogChatClient log, RunFolder run, Exception? failure, IReadOnlyList<ChatTools.PackSection> pack, bool usePack, bool requireToolFirst)
    {
        var json = new JsonObject
        {
            ["book"] = book.Id,
            ["chapter"] = chapter,
            ["character"] = character,
            ["spoilers"] = spoilers,
            ["model"] = model,
            ["knowledgePack"] = usePack
                ? new JsonObject(pack.Select(s => KeyValuePair.Create(s.Heading, (JsonNode?)s.Rows)))
                : null,
            ["requireToolFirst"] = requireToolFirst,
            ["turns"] = new JsonArray(turns.Select(t => (JsonNode?)new JsonObject
            {
                ["question"] = t.Question,
                ["answer"] = t.Answer,
                ["toolCalls"] = new JsonArray(t.ToolCalls.Select(c => (JsonNode?)new JsonObject
                {
                    ["tool"] = c.Tool,
                    ["args"] = new JsonObject(c.Args.Select(a => KeyValuePair.Create(a.Key, (JsonNode?)a.Value))),
                    ["rows"] = c.Rows,
                    // The ids of the rows the model saw (the graph highlight).
                    ["nodeIds"] = new JsonArray(c.NodeIdList.Select(id => (JsonNode?)id).ToArray()),
                    ["edgeIds"] = new JsonArray(c.EdgeIdList.Select(id => (JsonNode?)id).ToArray()),
                }).ToArray()),
                ["lateRowsSuppressed"] = t.LateRowsSuppressed,
                ["inputTokens"] = t.InputTokens,
                ["outputTokens"] = t.OutputTokens,
            }).ToArray()),
            ["totals"] = new JsonObject
            {
                ["turns"] = turns.Count,
                ["toolCalls"] = turns.Sum(t => t.ToolCalls.Count),
                ["llmCalls"] = log.Calls,
                ["inputTokens"] = log.InputTokens,
                ["outputTokens"] = log.OutputTokens,
            },
            ["runId"] = run.RunId,
        };
        if (failure is not null) json["error"] = $"{failure.GetType().Name}: {failure.Message}";
        return json;
    }

    private static string CommandLine(Settings s)
    {
        var parts = new List<string> { Stage };
        if (s.Book is not null) parts.Add($"--book {s.Book}");
        if (s.Db is not null) parts.Add($"--db {s.Db}");
        parts.Add($"--chapter {s.Chapter}");
        parts.Add($"--character \"{s.Character}\"");
        if (s.Spoilers) parts.Add("--spoilers");
        if (s.Message is not null) parts.Add($"--message \"{s.Message}\"");
        if (s.Script is not null) parts.Add($"--script {s.Script}");
        if (s.Json) parts.Add("--json");
        if (s.CaptureContent) parts.Add("--capture-content");
        if (s.Llm is not null) parts.Add($"--llm {s.Llm}");
        return string.Join(" ", parts);
    }

    private static string Resolve(string root, string path) =>
        Path.IsPathRooted(path) || File.Exists(path) ? path : Path.Combine(root, path);
}
