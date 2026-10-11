using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Spectre.Console;
using Spectre.Console.Cli;
using TemporalGraph.Chat;
using TemporalGraph.Cli.Runs;
using TemporalGraph.Cli.Telemetry;
using TemporalGraph.Data;
using TemporalGraph.Llm;
using TemporalGraph.Query;

namespace TemporalGraph.Cli;

/// <summary>
/// <c>chat vet [--llm &lt;profile&gt;]</c>: the fixed checks a model has to pass before it goes on the site's allowlist, run
/// through the same chat setup as <c>chat</c> and the API (pack, tools, prompt, the profile's sampling), spoilers off throughout:
/// <list type="bullet">
/// <item><b>Spoiler gate:</b> the Hound at ch. 3, Holmes, "Who is the killer?" must not name Stapleton; Treasure Island at ch. 8,
/// Jim Hawkins, "Is Silver trustworthy?" must not mention the mutiny (any word starting "mutin").</item>
/// <item><b>Voice:</b> the same three questions to Holmes, Mortimer and Barrymore at the Hound's ch. 7 (one conversation each); each
/// answer keeps the prompt's length rule (<c>ChatPrompt</c>): a pointed question gets one short paragraph, a broad one (the whole
/// story, or details) up to three.</item>
/// <item><b>Character gate:</b> the Hound at ch. 7, where Watson is on the moor and Holmes in London. Holmes, "What do you know
/// so far?", must tell nothing of chapter 7 (Merripit House, Miss Stapleton, "Beryl", the orchid); Watson, asked who warned him to go
/// back to London, must name her (the chapter is his own, and the book says "Halloa, Beryl!" in it).</item>
/// <item><b>Tool use:</b> every turn above called a tool.</item>
/// <item><b>Voice match (blind):</b> <see cref="VoiceMatch"/>; reported, not a pass/fail condition. Skipped when the judge
/// (<c>Indexing:Llm</c>) is not configured.</item>
/// </list>
/// Prints a pass/fail table; exit 0 = all passed, 1 = a check failed or a call failed, 2 = the model isn't configured. Writes a run
/// folder (run.json with the results and answers, calls.jsonl, cost.json, chat.jsonl).
/// </summary>
public sealed class ChatVetCommand : AsyncCommand<ChatVetCommand.Settings>
{
    public const string Stage = "chat-vet";

    public sealed class Settings : GlobalSettings
    {
        [CommandOption("--llm <NAME>")][Description("A named LLM profile (Llm:Profiles:<name>); default: Chat:Llm, else the Llm section.")] public string? Llm { get; init; }

        [CommandOption("--book <ID>")][Description("Only the checks on this book; repeat for several. The blind voice match is then among the characters of those books.")]
        public string[] Book { get; init; } = [];

        [CommandOption("--max-questions <N>")][Description("Stop asking after N questions (a bounded check); the checks not reached are listed as not run, the blind voice match too when it would not fit.")]
        public int? MaxQuestions { get; init; }

        [CommandOption("--pause <SECONDS>")][Description("Seconds to wait between checks, for a provider with a low requests-per-minute limit (OpenRouter's new accounts: 20). Default 0.")]
        public int Pause { get; init; }
    }

    private const string Hound = "hound-of-the-baskervilles";
    private const string Treasure = "treasure-island";

    /// <summary>A question and the most paragraphs its answer may have.</summary>
    public sealed record Question(string Text, int MaxParagraphs = Pointed);

    /// <summary>The prompt's length rule: "one short paragraph at most … If the reader asks for the whole story or for details, you may
    /// use up to three short paragraphs."</summary>
    public const int Pointed = 1, Broad = 3;

    /// <summary>The voice check's three questions. "What do you know so far?" and "What happened on the moor?" ask
    /// for the whole story so far; "Who is Sir Henry?" is pointed.</summary>
    public static readonly Question[] VoiceQuestions = [new("What do you know so far?", Broad), new("Who is Sir Henry?", Pointed), new("What happened on the moor?", Broad)];

    /// <param name="Spoiler">For a spoiler or gate check: what the answer must not contain (a regex), and what it means.</param>
    /// <param name="Must">The other side of the character gate: what an answer must contain, and what its absence means.</param>
    public sealed record Conversation(string Name, string Book, int Chapter, string Character, Question[] Questions, (Regex Pattern, string Means)? Spoiler = null,
        (Regex Pattern, string Means)? Must = null);

    public static readonly Conversation[] Conversations =
    [
        new("spoiler: Hound ch. 3, the killer", Hound, 3, "Sherlock Holmes", [new("Who is the killer?")], (new Regex(@"Stapleton", RegexOptions.IgnoreCase), "names Stapleton")),
        new("spoiler: Treasure Island ch. 8, Silver", Treasure, 8, "Jim Hawkins", [new("Is Silver trustworthy?")], (new Regex(@"\bmutin", RegexOptions.IgnoreCase), "mentions the mutiny")),
        // Silver himself, before his secret is out.
        new("spoiler: Treasure Island ch. 8, Silver himself", Treasure, 8, "Long John Silver", [new("Tell me about yourself.")], (new Regex(@"\bmutin", RegexOptions.IgnoreCase), "mentions the mutiny")),
        new("voice: Holmes, ch. 7", Hound, 7, "Sherlock Holmes", VoiceQuestions),
        new("voice: Mortimer, ch. 7", Hound, 7, "Dr. James Mortimer", VoiceQuestions),
        new("voice: Barrymore, ch. 7", Hound, 7, "Barrymore", VoiceQuestions),
        // The character gate. In chapter 7 Watson is at Merripit House; Holmes, in London, has been
        // told none of it, and may not tell it. Watson may: it is his chapter, and Stapleton calls his sister "Beryl" before him.
        new("gate: Hound ch. 7, Holmes has not been told the chapter", Hound, 7, "Sherlock Holmes", [new("What do you know so far?", Broad)],
            (new Regex(@"Merripit|Beryl|Miss Stapleton|orchid", RegexOptions.IgnoreCase), "tells chapter 7 (Merripit House, Miss Stapleton, Beryl or the orchid)")),
        new("gate: Hound ch. 7, Watson knows his own chapter", Hound, 7, "Dr. Watson", [new("Who warned you to go back to London, and what is she called?")],
            Must: (new Regex(@"Beryl|Miss Stapleton", RegexOptions.IgnoreCase), "does not name Miss Stapleton (Beryl), whom he met in this chapter")),
    ];

    /// <param name="Info">A measurement (the voice match): shown, never a pass/fail condition.</param>
    public sealed record Result(string Check, bool Passed, string Detail, bool Info = false);

    public override Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken) =>
        RunAsync(settings, LlmSettings.LoadConfiguration(), RepoPaths.FindRoot(), Console.Out, cancellationToken);

    /// <summary>Paragraphs of an answer: blocks separated by a blank line.</summary>
    public static int Paragraphs(string answer) => Regex.Split(answer.Trim(), @"\n\s*\n").Count(p => p.Trim().Length > 0);

    /// <summary>The voice check: every answer within its question's paragraph limit.</summary>
    public static (bool Passed, string Detail) Length(IReadOnlyList<Question> questions, IReadOnlyList<string> answers)
    {
        var paragraphs = answers.Select(Paragraphs).ToList();
        var limits = questions.Take(paragraphs.Count).Select(q => q.MaxParagraphs).ToList();
        return (paragraphs.Zip(limits).All(p => p.First <= p.Second),
            $"paragraphs per answer: {string.Join(" / ", paragraphs)} (at most {string.Join(" / ", limits)})");
    }

    internal static async Task<int> RunAsync(Settings settings, IConfiguration config, string root, TextWriter output, CancellationToken ct, string? anthropicBaseUrl = null)
    {
        var llm = LlmSettings.From(config, settings.Llm ?? ChatSettings.LlmProfile(config));
        var problems = llm.Validate();
        if (problems.Count > 0) return LlmCheckCommand.NotConfigured(problems);

        var run = RunFolder.Create(RepoPaths.Runs(root));
        File.WriteAllText(run.File(ChatJsonl.FileName), "");
        var chatLog = new ChatJsonl(run);
        var startedAt = DateTimeOffset.UtcNow;
        using var span = IndexingTelemetry.Source.StartActivity(Stage);
        span?.SetTag(IndexingTelemetry.RunId, run.RunId);
        span?.SetTag(IndexingTelemetry.Stage, Stage);
        var sampling = LlmSampling.From(config, llm);
        IndexingShared.TagSampling(span, sampling);
        IndexingShared.TagLlm(span, llm);
        await output.WriteLineAsync($"Vetting {llm.Model} ({llm.Profile ?? "the Llm section"}) · run {run.RunId}");

        var results = new List<Result>();
        var transcript = new List<object>();
        var (turnsTotal, turnsWithTools, calls, inputTokens, outputTokens) = (0, 0, 0L, 0L, 0L);
        var allTurns = new List<ChatTurn>();   // every turn, for the never-say count
        string? model = null;
        Exception? failure = null;
        // A bounded run: some books' checks, and at most so many questions.
        var books = settings.Book.Length == 0 ? null : settings.Book.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var left = settings.MaxQuestions ?? int.MaxValue;
        foreach (var c in Conversations.Where(c => books is null || books.Contains(c.Book)))
        {
            if (failure is not null) break;
            if (left <= 0)
            {
                results.Add(new(c.Name, true, "not run: --max-questions was reached", Info: true));
                continue;
            }
            if (settings.Pause > 0 && c != Conversations[0]) await Task.Delay(TimeSpan.FromSeconds(settings.Pause), ct);
            var (conversation, client, examples) = Open(root, config, llm, sampling, run, chatLog, c, anthropicBaseUrl);
            using (client)
            {
                // A spoiler check also checks the example lines in the prompt: none from after the reader's chapter.
                if (c.Spoiler is not null && examples.Count > 0)
                    results.Add(new($"{c.Name}: example lines", examples.All(l => l.Chapter <= c.Chapter),
                        $"{examples.Count} lines, from ch. {string.Join(", ", examples.Select(l => l.Chapter))}"));
                var answers = new List<ChatTurn>();
                try
                {
                    for (var i = 0; i < c.Questions.Length && left > 0; i++, left--) answers.Add(await conversation.AskAsync(i + 1, c.Questions[i].Text, ct));
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    failure = ex;
                    results.Add(new(c.Name, false, $"call failed: {ex.GetType().Name}"));
                }
                var log = client.GetService<CallLogChatClient>()!;
                (calls, inputTokens, outputTokens) = (calls + log.Calls, inputTokens + log.InputTokens, outputTokens + log.OutputTokens);
                model = answers.Select(t => t.ModelId).LastOrDefault(m => m is not null) ?? model;
                turnsTotal += answers.Count;
                turnsWithTools += answers.Count(t => t.ToolCalls.Count > 0);
                allTurns.AddRange(answers);
                transcript.AddRange(answers.Select(t => new { check = c.Name, question = t.Question, answer = t.Answer, toolCalls = t.ToolCalls.Count, paragraphs = Paragraphs(t.Answer), neverSaid = t.NeverSaid }));
                if (failure is not null) break;

                if (c.Spoiler is { } s)
                {
                    var leaked = answers.FirstOrDefault(t => s.Pattern.IsMatch(t.Answer));
                    results.Add(new(c.Name, leaked is null, leaked is null ? c.Name.StartsWith("gate", StringComparison.Ordinal) ? "tells nothing of it" : "no spoiler" : s.Means));
                }
                else if (c.Must is { } must)
                {
                    var said = answers.Any(t => must.Pattern.IsMatch(t.Answer));
                    results.Add(new(c.Name, said, said ? "names her" : must.Means));
                }
                else
                {
                    var (withinLength, detail) = Length(c.Questions, answers.Select(t => t.Answer).ToList());
                    results.Add(new(c.Name, withinLength, detail));
                }
            }
        }
        results.Add(new("tool use: every turn called a tool", turnsTotal > 0 && turnsWithTools == turnsTotal, $"{turnsWithTools} of {turnsTotal} turns"));

        // The blind voice match (after the checks, so its turns don't count toward tool use). Among the characters of
        // the chosen books; left out when the questions left do not cover it, or when fewer than two characters would be in it.
        var matchCast = VoiceMatch.Cast.Where(m => books is null || books.Contains(m.Book)).ToArray();
        var matchFits = matchCast.Length >= 2 && (long)matchCast.Length * VoiceMatch.Questions.Length <= left;
        if (failure is null && !matchFits)
            results.Add(new("voice match (blind)", true, matchCast.Length < 2 ? "not run: fewer than two characters of the chosen books are in it" : "not run: --max-questions was reached", Info: true));
        if (failure is null && matchFits)
        {
            var judgeProfile = config["Indexing:Llm"]?.Trim() is { Length: > 0 } jp && !jp.StartsWith('<') ? jp : null;
            var judgeLlm = LlmSettings.From(config, judgeProfile);
            if (judgeLlm.Validate().Count > 0)
                results.Add(new("voice match (blind)", true, "skipped: the judge model (Indexing:Llm) is not configured", Info: true));
            else
            {
                var answers = VoiceMatch.Questions.Select(_ => new string[matchCast.Length]).ToList();
                for (var i = 0; i < matchCast.Length && failure is null; i++)
                {
                    var m = matchCast[i];
                    if (settings.Pause > 0) await Task.Delay(TimeSpan.FromSeconds(settings.Pause), ct);
                    var c = new Conversation($"voice match: {m.Character}", m.Book, m.Chapter, m.Character, [.. VoiceMatch.Questions.Select(q => new Question(q))]);
                    var (conversation, client, _) = Open(root, config, llm, sampling, run, chatLog, c, anthropicBaseUrl);
                    using (client)
                    {
                        try
                        {
                            for (var q = 0; q < VoiceMatch.Questions.Length; q++)
                            {
                                var turn = await conversation.AskAsync(q + 1, VoiceMatch.Questions[q], ct);
                                answers[q][i] = turn.Answer;
                                allTurns.Add(turn);
                                transcript.Add(new { check = c.Name, question = turn.Question, answer = turn.Answer, toolCalls = turn.ToolCalls.Count, paragraphs = Paragraphs(turn.Answer), neverSaid = turn.NeverSaid });
                            }
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                        {
                            failure = ex;
                            results.Add(new(c.Name, false, $"call failed: {ex.GetType().Name}"));
                        }
                        var log = client.GetService<CallLogChatClient>()!;
                        (calls, inputTokens, outputTokens) = (calls + log.Calls, inputTokens + log.InputTokens, outputTokens + log.OutputTokens);
                    }
                }
                if (failure is null)
                {
                    var manifest = BookManifest.Read(Path.Combine(RepoPaths.Books(root), BookManifest.FileName));
                    var names = matchCast.Select(m => m.Book).Distinct().ToDictionary(b => b, b =>
                    {
                        var book = manifest.First(e => e.Id == b);
                        var graph = ChatCharacters.WithRosterNames(BookGraph.Load(Path.Combine(RepoPaths.Books(root), b + ".sqlite")), book.Chat);
                        return VoiceMatch.Names(graph, book.Chat ?? []);
                    });
                    var context = new LlmCallContext { Run = run, Stage = Stage + "-judge" };
                    using var judge = ChatClientFactory.Create(judgeLlm, maxRetries: 0, new LlmPipelineOptions(CallContext: context, Sampling: LlmSampling.From(config, judgeLlm)));
                    var cast = matchCast.Select(m => m.Character).ToList();
                    try
                    {
                        for (var q = 0; q < VoiceMatch.Questions.Length; q++)
                        {
                            var masked = matchCast.Select((m, i) => VoiceMatch.Mask(answers[q][i], names[m.Book])).ToList();
                            var order = VoiceMatch.Order(matchCast.Length, seed: 48 + q);
                            var reply = await VoiceMatch.JudgeAsync(judge, VoiceMatch.JudgeText(masked, cast, order), ct);
                            var picks = VoiceMatch.ParseJudge(reply, cast);
                            var score = VoiceMatch.Grade(matchCast, order, picks);
                            results.Add(new($"voice match (blind): \"{VoiceMatch.Questions[q]}\"", true, score.Detail, Info: true));
                            transcript.Add(new
                            {
                                check = "voice match (blind)", question = VoiceMatch.Questions[q], judge = judgeLlm.Model,
                                answers = order.Select((castIndex, slot) => new { slot = slot + 1, character = cast[castIndex], masked = masked[castIndex], picked = picks.GetValueOrDefault(slot) }),
                                correct = score.Correct, sameBook = score.SameBook,
                            });
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                    {
                        results.Add(new("voice match (blind)", true, $"judge call failed: {ex.GetType().Name}", Info: true));
                    }
                    var judgeLog = judge.GetService<CallLogChatClient>()!;
                    (calls, inputTokens, outputTokens) = (calls + judgeLog.Calls, inputTokens + judgeLog.InputTokens, outputTokens + judgeLog.OutputTokens);
                }
            }
        }

        // Answers asked again for a never-say word (reported, not a pass/fail condition).
        var retried = allTurns.Where(t => t.NeverSayRetried).ToList();
        results.Add(new("never-say retries", true, $"{retried.Count} of {allTurns.Count} turns"
            + (retried.Count == 0 ? "" : $" ({string.Join(", ", retried.SelectMany(t => t.NeverSaid!).Distinct())}; still there after the retry: {retried.Count(t => t.NeverSayRemaining is { Count: > 0 })})"), Info: true));

        var table = new Table().AddColumns("Check", "Result", "Detail");
        foreach (var r in results) table.AddRow(Markup.Escape(r.Check), r.Info ? "info" : r.Passed ? "[green]pass[/]" : "[red]FAIL[/]", Markup.Escape(r.Detail));
        if (ReferenceEquals(output, Console.Out)) AnsiConsole.Write(table);
        else foreach (var r in results) await output.WriteLineAsync($"{(r.Info ? "info" : r.Passed ? "pass" : "FAIL")}  {r.Check}: {r.Detail}");
        var passed = results.Where(r => !r.Info).All(r => r.Passed);
        await output.WriteLineAsync($"{(passed ? "All checks passed" : "Not passed")} · model {model ?? "(not reported)"} · {calls} calls · {inputTokens} input / {outputTokens} output tokens");
        await output.WriteLineAsync($"Run {run.RunId}: {run.Path}");

        run.Write(RunFolder.RunJson, new
        {
            runId = run.RunId,
            command = Stage + (settings.Llm is null ? "" : $" --llm {settings.Llm}") + string.Concat(settings.Book.Select(b => $" --book {b}")) + (settings.MaxQuestions is { } max ? $" --max-questions {max}" : ""),
            startedAt,
            finishedAt = DateTimeOffset.UtcNow,
            settings = new { llm = new { profile = llm.Profile, type = llm.Type, endpoint = llm.Endpoint, model = llm.Model, policyId = llm.PolicyId }, sampling = sampling.Record(),
                knowledgePack = ChatSettings.From(config).KnowledgePack, requireToolFirst = ChatSettings.From(config).RequireToolFirst, characterGate = ChatSettings.From(config).CharacterGate },
            model,
            results = results.Select(r => new { check = r.Check, passed = r.Passed, detail = r.Detail, info = r.Info }),
            turns = transcript,
            passed,
        });
        run.Write(RunFolder.CostJson, Pricing.From(config).CostReport(calls, inputTokens, outputTokens));

        if (failure is not null)
        {
            AnsiConsole.MarkupLine($"[red]LLM call failed:[/] {Markup.Escape(failure.GetType().Name)}");
            span?.SetStatus(ActivityStatusCode.Error, failure.GetType().Name);
            return 1;
        }
        // The checks ran and one is bad.
        return passed ? Exit.Ok : CliContext.Problem(Exit.CheckFailed, "chat vet: " + string.Join("; ", results.Where(r => !r.Passed && !r.Info).Select(r => $"{r.Check}: {r.Detail}")), "vet-failed");
    }

    private static (ChatConversation, IChatClient, IReadOnlyList<VoiceLine>) Open(string root, IConfiguration config, LlmSettings llm, LlmSampling sampling, RunFolder run, ChatJsonl chatLog,
        Conversation c, string? anthropicBaseUrl)
    {
        var dbPath = Path.Combine(RepoPaths.Books(root), c.Book + ".sqlite");
        var book = BookManifest.Read(Path.Combine(RepoPaths.Books(root), BookManifest.FileName)).First(e => e.Id == c.Book);
        var graph = ChatCharacters.WithRosterNames(BookGraph.Load(dbPath), book.Chat);
        var characters = ChatCharacters.Load(dbPath, graph);
        // A canonical name or alias; else the roster spelling written onto the node, which ignores the apostrophe's shape.
        var character = ChatCharacters.Resolve(characters, c.Character)
            ?? characters.FirstOrDefault(x => x.Node?.Properties.GetValueOrDefault("rosterName") is string r && ChatTools.SameName(r, c.Character))?.Name
            ?? throw new InvalidOperationException($"No character '{c.Character}' in {c.Book}.");
        long pos = graph.Chapters[c.Chapter].CharEnd;
        var setup = ChatSetup.Create(book, graph, characters, character, ChatTools.LoadQueries(Path.Combine(root, "queries", c.Book)), ChatCharacters.LoadLocations(dbPath),
            c.Chapter, spoilers: false, config, onCall: call => chatLog.Tool(call, pos, false), voice: BookVoice.LoadFolder(book, RepoPaths.Books(root)));
        if (setup.PackText is not null) chatLog.Pack(setup.PackSections, setup.PackText.Length, pos, false);
        var context = new LlmCallContext { Run = run, Stage = Stage, BookId = c.Book, Chapter = c.Chapter };
        var client = ChatClientFactory.Create(llm, maxRetries: 0, anthropicBaseUrl, new LlmPipelineOptions(CallContext: context, Sampling: sampling));
        return (setup.Conversation(client, chatLog), client, setup.VoiceLines ?? []);
    }
}
