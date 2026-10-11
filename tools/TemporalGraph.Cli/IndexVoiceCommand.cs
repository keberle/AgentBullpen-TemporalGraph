using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Spectre.Console;
using Spectre.Console.Cli;
using TemporalGraph.Chat;
using TemporalGraph.Cli.Indexing;
using TemporalGraph.Cli.Runs;
using TemporalGraph.Data;
using TemporalGraph.Llm;
using TemporalGraph.Query;

namespace TemporalGraph.Cli;

/// <summary>
/// <c>index voice --book &lt;id&gt;</c>: the roster characters' own lines from the text (<see cref="VoiceLines"/>), written to
/// <c>data/books/&lt;id&gt;/voice/lines.json</c> (the pre-selected lines) and <c>lines.review.md</c> (the candidates, to mark).
/// <c>--apply-review</c> writes the lines marked <c>[x]</c> to lines.json instead (no model). Refuses to overwrite a review file without
/// <c>--force</c>. Writes a run folder (run.json, attribution.jsonl, calls.jsonl, cost.json).
/// </summary>
public sealed class IndexVoiceCommand : AsyncCommand<IndexVoiceCommand.Settings>
{
    public const string Stage = "index-voice";
    public const string AttributionFile = "attribution.jsonl";

    /// <summary>Marked paragraphs per call.</summary>
    public const int BatchSize = 30;

    public sealed class Settings : GlobalSettings
    {
        [CommandOption("--book <ID>")][Description("The book id (books.json).")] public string Book { get; init; } = "";
        [CommandOption("--llm <NAME>")][Description("A named LLM profile for the speaker attribution; default: Indexing:Llm.")] public string? Llm { get; init; }
        [CommandOption("--apply-review")][Description("Write the lines marked [[x]] in lines.review.md to lines.json (no model).")] public bool ApplyReview { get; init; }
        [CommandOption("--force")][Description("Overwrite an existing lines.review.md (and its marks).")] public bool Force { get; init; }
        [CommandOption("--from-run <RUN_ID>")][Description("Select again from an earlier index voice run's speakers (its attribution.jsonl): no model calls.")] public string? FromRun { get; init; }
    }

    /// <summary>Who "said I" is: the narrator of each first-person book, by chapter (<i>Treasure Island</i> ch. 16–18 are the doctor's).</summary>
    public static string? Narrator(string bookId, int chapter) => bookId switch
    {
        "hound-of-the-baskervilles" => "Dr. Watson",
        "treasure-island" => chapter is >= 16 and <= 18 ? "Dr. Livesey" : "Jim Hawkins",
        _ => null,
    };

    public override Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken) =>
        RunAsync(settings, LlmSettings.LoadConfiguration(), RepoPaths.FindRoot(), cancellationToken);

    internal static async Task<int> RunAsync(Settings settings, IConfiguration config, string root, CancellationToken ct)
    {
        var books = RepoPaths.Books(root);
        var book = BookManifest.Read(Path.Combine(books, BookManifest.FileName)).FirstOrDefault(b => b.Id == settings.Book);
        if (book is null) return IndexingShared.Fail($"Unknown book '{settings.Book}'.");
        if (book.Chat is not { Count: > 0 } roster) return IndexingShared.Fail($"'{book.Id}' has no chat roster (books.json chat).");
        var folder = BookVoice.FolderFor(books, book.Id);
        var reviewPath = Path.Combine(folder, BookVoice.ReviewFile);
        var linesPath = Path.Combine(folder, BookVoice.LinesFile);

        if (settings.ApplyReview)
        {
            if (!File.Exists(reviewPath)) return IndexingShared.Fail($"No review file: {reviewPath}. Run `index voice --book {book.Id}` first.");
            List<VoiceLine> kept;
            try { kept = VoiceLines.ParseReview(File.ReadAllText(reviewPath)); }
            catch (InvalidDataException e) { return IndexingShared.Fail(e.Message); }
            var unknown = kept.Select(l => l.Character).Distinct().Where(c => !roster.Contains(c)).ToList();
            if (unknown.Count > 0) return IndexingShared.Fail($"Not on the roster: {string.Join(", ", unknown)}.");
            File.WriteAllText(linesPath, JsonSerializer.Serialize(Sort(kept, roster), BookVoice.Json) + "\n");
            foreach (var g in kept.GroupBy(l => l.Character)) AnsiConsole.MarkupLine($"  {Markup.Escape(g.Key)}: {g.Count()} lines");
            AnsiConsole.MarkupLine($"Wrote {kept.Count} reviewed lines to {Markup.Escape(linesPath)}.");
            return 0;
        }

        if (File.Exists(reviewPath) && !settings.Force)
            return IndexingShared.Fail($"{reviewPath} exists and may carry your marks. Use --apply-review to use them, or --force to start over.");
        if (!BookProfiles.Has(root, book.Id)) return IndexingShared.Fail($"Book '{book.Id}' has no Stage 0 profile.");

        var stage0 = IndexingShared.Stage0For(book.Id);
        var text = IndexingShared.BookText(book.Id);
        var graph = ChatCharacters.WithRosterNames(BookGraph.Load(Path.Combine(books, book.DatabaseFileName)), roster);
        var cast = new VoiceLines.Cast(graph, roster);
        var nameWords = VoiceLines.NameWords(graph);

        if (settings.FromRun is { } fromRun)
        {
            var file = Path.Combine(RepoPaths.Runs(root), fromRun, AttributionFile);
            if (!File.Exists(file)) return IndexingShared.Fail($"No {AttributionFile} in run {fromRun}.");
            var saved = File.ReadAllLines(file).Where(l => l.Length > 0).Select(l => JsonSerializer.Deserialize<SavedAttribution>(l, BookVoice.Json)!)
                .Select(a => new VoiceLines.Attributed(new VoiceLines.Utterance(a.Chapter, 0, 0, (int)a.Pos, a.Text, "", false), a.Speaker, a.Method, a.Evidence)).ToList();
            return Write(book, roster, saved, nameWords, folder, linesPath, reviewPath, fromRun);
        }

        var utterances = VoiceLines.Utterances(text, stage0);
        var byRule = VoiceLines.ByRule(utterances, cast);

        var run = RunFolder.Create(RepoPaths.Runs(root));
        var startedAt = DateTimeOffset.UtcNow;
        if (IndexingShared.Llm(config, run, Stage, settings.Llm) is not { } llm) return Exit.NotReady;
        using var client = llm.Client;
        var threshold = IndexingShared.IndexingSettings.From(config).EvidenceThreshold;
        AnsiConsole.MarkupLine($"{book.Title}: {utterances.Count} quoted paragraphs; {byRule.Count(a => a is not null)} by speech tag; the rest to {Markup.Escape(llm.Llm.Model ?? "the model")}.");

        // The model, for the paragraphs the rule left open: per chapter, in batches of at most BatchSize marked paragraphs (the whole chapter
        // is the context of each call; a long list overran the output cap), one retry for an unreadable reply.
        var answers = new ConcurrentDictionary<int, VoiceLines.Attributed>();
        var failed = new ConcurrentBag<int>();
        using var gate = new SemaphoreSlim(4);
        var batches = stage0.Chapters.SelectMany(chapter =>
            utterances.Select((u, i) => (u, i)).Where(x => x.u.Chapter == chapter.Number && byRule[x.i] is null).Chunk(BatchSize).Select(b => (chapter, open: b)));
        await Task.WhenAll(batches.Select(async batch =>
        {
            var (chapter, open) = batch;
            await gate.WaitAsync(ct);
            try
            {
                var prompt = VoiceLines.Prompt.Replace("\r\n", "\n").Replace("{chapter}", chapter.Number.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    .Replace("{title}", book.Title).Replace("{author}", book.Author)
                    .Replace("{text}", VoiceLines.MarkedChapter(text, chapter, open.Select(x => x.u).ToList()));
                VoiceLines.SpeakerAnswers? result = null;
                for (var attempt = 1; result is null; attempt++)
                {
                    try
                    {
                        result = (await client.GetResponseAsync<VoiceLines.SpeakerAnswers>([new ChatMessage(ChatRole.User, prompt)], Stage1.Json, Stage1.StrictOptions(),
                            useJsonSchemaResponseFormat: true, ct)).Result;
                    }
                    catch (JsonException) when (attempt < 2) { }
                }
                var byQ = result.Speakers.GroupBy(a => a.Q).ToDictionary(g => g.Key, g => g.First());
                for (var k = 0; k < open.Length; k++)
                    answers[open[k].i] = VoiceLines.Check(open[k].u, byQ.GetValueOrDefault(k + 1), text, cast, threshold, Narrator(book.Id, chapter.Number));
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                failed.Add(chapter.Number);
                AnsiConsole.MarkupLine($"[red]ch. {chapter.Number}: {Markup.Escape(e.GetType().Name)}[/]");
            }
            finally
            {
                gate.Release();
            }
        }));
        IndexingShared.RecordCost(run, config, Stage, client);
        if (!failed.IsEmpty) return IndexingShared.Fail($"Attribution failed for ch. {string.Join(", ", failed.Order())}; nothing written (run {run.RunId}).", 1);

        // Continuations of a paragraph the model attributed take its speaker.
        var attributed = new List<VoiceLines.Attributed>();
        for (var i = 0; i < utterances.Count; i++)
        {
            var a = byRule[i] ?? answers.GetValueOrDefault(i) ?? new VoiceLines.Attributed(utterances[i], null, "unknown");
            if (utterances[i].Continues && attributed.Count > 0 && a.Speaker is null && attributed[^1].Speaker is { } previous)
                a = new VoiceLines.Attributed(utterances[i], previous, "continued");
            attributed.Add(a);
        }
        File.WriteAllLines(run.File(AttributionFile), attributed.Select(a => JsonSerializer.Serialize(new
        {
            chapter = a.Utterance.Chapter, pos = a.Utterance.Pos, speaker = a.Speaker, method = a.Method, evidence = a.Evidence, text = a.Utterance.Text,
        }, BookVoice.Json).ReplaceLineEndings(" ")));

        var (candidates, counts) = Selected(attributed, roster, nameWords);
        Write(book, roster, attributed, nameWords, folder, linesPath, reviewPath, run.RunId);

        var methods = attributed.GroupBy(a => a.Method).ToDictionary(g => g.Key, g => g.Count());
        run.Write(RunFolder.RunJson, new
        {
            runId = run.RunId,
            command = $"{Stage} --book {book.Id}" + (settings.Llm is null ? "" : $" --llm {settings.Llm}") + (settings.Force ? " --force" : ""),
            startedAt,
            finishedAt = DateTimeOffset.UtcNow,
            git = IndexingShared.Git(root),
            settings = new { llm = IndexingShared.LlmRecord(llm.Llm), sampling = llm.Sampling.Record(), evidenceThreshold = threshold },
            utterances = utterances.Count,
            methods,
            characters = roster.Select(r => new { name = r, attributed = counts[r], candidates = candidates[r].Count, preselected = candidates[r].Take(VoiceLines.PreSelected).Count() }),
        });

        AnsiConsole.MarkupLine($"Speakers: {string.Join(" · ", methods.OrderByDescending(m => m.Value).Select(m => $"{m.Key} {m.Value}"))}. Run {run.RunId}.");
        return 0;
    }

    private sealed record SavedAttribution(int Chapter, long Pos, string? Speaker, string Method, string? Evidence, string Text);

    private static (Dictionary<string, List<VoiceLines.Candidate>> Candidates, Dictionary<string, int> Counts) Selected(
        IReadOnlyList<VoiceLines.Attributed> attributed, IReadOnlyList<string> roster, ISet<string> nameWords) =>
        (VoiceLines.Select(attributed, roster, nameWords), roster.ToDictionary(r => r, r => attributed.Count(a => a.Speaker == r)));

    /// <summary>lines.json (the pre-selected lines) and lines.review.md (the candidates) from the attributed utterances.</summary>
    private static int Write(BookEntry book, IReadOnlyList<string> roster, IReadOnlyList<VoiceLines.Attributed> attributed, ISet<string> nameWords,
        string folder, string linesPath, string reviewPath, string runId)
    {
        var (candidates, counts) = Selected(attributed, roster, nameWords);
        var preselected = candidates.SelectMany(c => c.Value.Take(VoiceLines.PreSelected).Select(x => x.Line)).ToList();
        Directory.CreateDirectory(folder);
        File.WriteAllText(linesPath, JsonSerializer.Serialize(Sort(preselected, roster), BookVoice.Json) + "\n");
        File.WriteAllText(reviewPath, VoiceLines.Review(book.Title, book.Id, runId, roster, candidates, counts));
        var table = new Table().AddColumns("Character", "Utterances", "Candidates", "Pre-selected");
        foreach (var r in roster) table.AddRow(Markup.Escape(r), counts[r].ToString(), candidates[r].Count.ToString(), candidates[r].Take(VoiceLines.PreSelected).Count().ToString());
        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"Wrote {Markup.Escape(linesPath)} and {Markup.Escape(reviewPath)}.");
        return 0;
    }

    private static List<VoiceLine> Sort(IEnumerable<VoiceLine> lines, IReadOnlyList<string> roster) =>
        lines.OrderBy(l => roster.ToList().IndexOf(l.Character)).ThenBy(l => l.Pos).ToList();
}
