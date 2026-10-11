using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Spectre.Console;
using Spectre.Console.Cli;
using TemporalGraph.Cli.Gold;
using TemporalGraph.Cli.Runs;
using TemporalGraph.Cli.Scoring;
using TemporalGraph.Cli.Telemetry;

namespace TemporalGraph.Cli;

/// <summary>
/// <c>index score --run &lt;runId&gt; [--run …] [--gold &lt;file&gt;] [--hide-holdout-details]</c>: scores each run's
/// stage-2.json against gold JSON (from <c>gold import</c>; default <c>gold/&lt;book&gt;/&lt;chapters&gt;.json</c> from the
/// run's chapters). Writes scores.json, appends scoring failures to failures.jsonl (replacing any earlier scoring lines),
/// and writes failures.md, the digest the improvement loop reads. Runs on held-out chapters (<see cref="HoldOut"/>) are
/// reported separately, labeled "held-out"; with <c>--hide-holdout-details</c> only their F1 is printed or written.
/// </summary>
public sealed class ScoreCommand : Command<ScoreCommand.Settings>
{
    public const string Stage = "score";
    public const string ScoresJson = "scores.json";
    public const string FailuresMd = "failures.md";

    public sealed class Settings : GlobalSettings
    {
        [CommandOption("--run <RUN>")][Description("A run id; repeat for several runs.")] public string[] Run { get; init; } = [];
        [CommandOption("--gold <FILE>")][Description("Gold JSON for every run, e.g. gold/hound-of-the-baskervilles/ch01-03.json. Default: gold/<book>/<chapters>.json per run.")] public string? Gold { get; init; }
        [CommandOption("--hide-holdout-details")][Description("For held-out runs, print and write only the F1: no failures, matches or counts.")] public bool HideHoldoutDetails { get; init; }
        [CommandOption("--facts")][Description("Score Stage 3/4 facts against facts gold (default gold/<book>/<chapters>.facts.json); writes facts-scores.json.")] public bool Facts { get; init; }
        [CommandOption("--graph")][Description("Score a run of the full schema against the graph gold (gold/<book>/<chapters>.graph.json, from gold convert): Fact F1, DISCOVERED.how accuracy, perception precision over the marked candidates; writes graph-scores.json.")] public bool Graph { get; init; }
        [CommandOption("--chapters <RANGE>")][Description("Score only the run's items in these chapters (e.g. 1-3 of a ch. 1-15 run); default gold and output names follow the range.")] public string? Chapters { get; init; }
    }

    /// <summary>
    /// <c>--chapters a-b</c>: only the part of a run inside those chapters is scored. Facts: run items and gold items whose
    /// revealed chapter (evidence start) is in the range. Entities: each run entity's mentions in the range (an entity with none
    /// is left out; its aliases and first mention come from those mentions), as if the run had covered only those chapters.
    /// <b>Held-out rule:</b> a run is held-out when the chapters actually scored (the range, else the run's chapters) include a
    /// held-out chapter, so <c>--chapters 1-3</c> on a ch. 1–15 run is scored in full, and a range with ch. 8 gets the
    /// held-out treatment (<c>--hide-holdout-details</c>). Default gold: gold/&lt;book&gt;/&lt;range&gt;(.facts).json. Output:
    /// scores-chNN-NN.json / facts-scores-chNN-NN.json (and failures-chNN-NN.md), so a slice never overwrites the run's full score.
    /// </summary>
    public static (int First, int Last)? ParseRange(string? spec) => spec is null ? null : IndexingShared.ParseChapters(spec);

    /// <summary>"scores.json" → "scores-ch01-03.json" for a range; unchanged without one.</summary>
    public static string SliceFile(string file, (int First, int Last)? range) =>
        range is { } r ? Path.GetFileNameWithoutExtension(file) + "-" + IndexingShared.ChapterSlug(r.First, r.Last) + Path.GetExtension(file) : file;

    public override int Execute(CommandContext context, Settings settings, CancellationToken ct)
    {
        if (settings.Run.Length == 0) return IndexingShared.Fail("Give at least one --run.");
        var root = RepoPaths.FindRoot();
        (int First, int Last)? range = null;
        if (settings.Chapters is { } spec && (range = ParseRange(spec)) is null) return IndexingShared.Fail("--chapters must look like 1-3.");
        if (settings.Graph) return GraphScoring.Execute(root, settings.Run, settings.Gold, range);
        if (settings.Facts) return FactsScoring.Execute(root, settings.Run, settings.Gold, settings.HideHoldoutDetails, range);
        var outcomes = new List<Outcome>();
        foreach (var id in settings.Run)
        {
            RunFolder run;
            try { run = RunFolder.Open(RepoPaths.Runs(root), id); }
            catch (DirectoryNotFoundException ex) { return IndexingShared.Fail(ex.Message); }
            if (!File.Exists(run.File(RunFolder.Stage2Json))) return IndexingShared.Fail($"{run.RunId} has no stage-2.json.");
            var goldPath = settings.Gold is { } g ? (Path.IsPathRooted(g) ? g : Path.Combine(root, g)) : DefaultGold(root, run, range);
            if (!File.Exists(goldPath)) return IndexingShared.Fail($"Gold file not found for {run.RunId}: {Path.GetRelativePath(root, goldPath)}");

            using var span = IndexingTelemetry.Source.StartActivity($"index score --run {run.RunId}");
            span?.SetTag(IndexingTelemetry.RunId, run.RunId);
            span?.SetTag(IndexingTelemetry.Stage, Stage);
            var outcome = ScoreRun(root, run, goldPath, settings.HideHoldoutDetails, range);
            span?.SetTag("score.f1", outcome.Result.Overall.F1);
            span?.SetTag("score.held_out", outcome.HeldOut);
            outcomes.Add(outcome);
        }
        Render(AnsiConsole.Console, outcomes, settings.HideHoldoutDetails);
        return 0;
    }

    /// <summary>gold/&lt;book&gt;/chNN-NN.json (chNN.json for one chapter), from the run's chapters.</summary>
    public static string DefaultGold(string root, RunFolder run, (int First, int Last)? range = null)
    {
        var (first, last) = range ?? IndexCrosscheckCommand.RunChapters(run);
        return Path.Combine(root, "gold", Stage2File.Load(run).BookId, IndexingShared.ChapterSlug(first, last) + ".json");
    }

    /// <summary>
    /// The console report: one table for the scored runs and a separate one labeled "held-out" for held-out runs. With
    /// <paramref name="hideHoldoutDetails"/>, the held-out table has only run ids and F1.
    /// </summary>
    public static void Render(IAnsiConsole console, IReadOnlyList<Outcome> outcomes, bool hideHoldoutDetails)
    {
        var scored = outcomes.Where(o => !o.HeldOut).ToList();
        var held = outcomes.Where(o => o.HeldOut).ToList();
        if (scored.Count > 0) console.Write(FullTable("scored", scored));
        if (held.Count > 0)
        {
            if (hideHoldoutDetails)
            {
                var t = new Table().Title($"{HoldOut.Label} (details hidden)").AddColumns("Run", "F1");
                foreach (var o in held) t.AddRow(o.RunId, F(o.Result.Overall.F1));
                if (held.Count > 1) t.AddRow("mean", F(held.Average(o => o.Result.Overall.F1)));
                console.Write(t);
            }
            else console.Write(FullTable(HoldOut.Label, held));
        }
        foreach (var o in outcomes)
            console.MarkupLine($"Run [bold]{o.RunId}[/]{(o.HeldOut ? $" ({HoldOut.Label})" : "")}: {Markup.Escape(ScoresJson)}, {Markup.Escape(FailuresMd)}{(o.DetailsHidden ? " (details hidden)" : "")}");
    }

    private static string F(double x) => $"{x:0.000}";

    private static int[] Counts(ScoreResult r) =>
    [
        r.Count(FailureTags.WrongMerge), r.Count(FailureTags.MissedMerge),
        r.Failures.Count(f => f.Tag == FailureTags.WrongAlias && f.RunEntity is not null),
        r.Failures.Count(f => f.Tag == FailureTags.WrongAlias && f.GoldEntity is not null),
        r.Count(FailureTags.WrongType), r.Count(FailureTags.OutOfScope), r.Count(FailureTags.SpuriousEntity), r.Count(FailureTags.MissedEntity),
    ];

    private static Table FullTable(string title, IReadOnlyList<Outcome> outcomes)
    {
        var table = new Table().Title(title).AddColumns("Run", "P", "R", "F1", "Matched", "Run entities", "Gold", "Wrong merges", "Missed merges", "Wrong aliases", "Missing aliases", "Wrong type", "Out of scope", "Spurious", "Missed");
        foreach (var o in outcomes)
        {
            var r = o.Result;
            table.AddRow([o.RunId, F(r.Overall.Precision), F(r.Overall.Recall), F(r.Overall.F1), r.Matches.Count.ToString(), r.Overall.Predicted.ToString(), r.Overall.Gold.ToString(),
                .. Counts(r).Select(c => c.ToString())]);
        }
        if (outcomes.Count > 1)
        {
            var counts = outcomes.Select(o => Counts(o.Result)).ToList();
            table.AddRow(["mean", F(outcomes.Average(o => o.Result.Overall.Precision)), F(outcomes.Average(o => o.Result.Overall.Recall)), F(outcomes.Average(o => o.Result.Overall.F1)),
                $"{outcomes.Average(o => o.Result.Matches.Count):0.##}", $"{outcomes.Average(o => o.Result.Overall.Predicted):0.##}", $"{outcomes.Average(o => o.Result.Overall.Gold):0.##}",
                .. Enumerable.Range(0, counts[0].Length).Select(i => $"{counts.Average(c => c[i]):0.##}")]);
        }
        return table;
    }

    /// <param name="HeldOut">The run's chapters include a held-out chapter.</param>
    /// <param name="DetailsHidden">Held-out and --hide-holdout-details: only the F1 was printed and written.</param>
    public sealed record Outcome(string RunId, ScoreResult Result, object Scores, bool HeldOut, bool DetailsHidden);

    public static Outcome ScoreRun(string root, RunFolder run, string goldPath, bool hideHoldoutDetails = false, (int First, int Last)? range = null)
    {
        var goldBytes = File.ReadAllBytes(goldPath);
        var goldJson = JsonNode.Parse(Encoding.UTF8.GetString(goldBytes))!;
        var gold = goldJson["entities"]!.AsArray().Select((e, i) => new GoldItem(i + 1, e!["type"]!.GetValue<string>(), e["name"]!.GetValue<string>(),
            e["aliases"]!.AsArray().Select(a => a!.GetValue<string>()).ToList(), e["first"]!["chapter"]!.GetValue<int>(), e["first"]!["quote"]!.GetValue<string>())).ToList();
        var rejected = goldJson["rejected"]!.AsArray().Select(e => (e!["type"]!.GetValue<string>(), e["name"]!.GetValue<string>())).ToList();

        var stage2 = Stage2File.Load(run);
        var (first, last) = range ?? IndexCrosscheckCommand.RunChapters(run);
        var heldChapters = HoldOut.In(stage2.BookId, first, last);
        var hide = heldChapters.Count > 0 && hideHoldoutDetails;
        var bookText = IndexingShared.BookText(stage2.BookId);
        var items = stage2.Entities
            .Select(e => (e, mentions: e.Mentions.Where(m => range is null || (m.Chapter >= first && m.Chapter <= last)).OrderBy(m => m.CharStart).ToList()))
            .Where(x => x.mentions.Count > 0)
            .Select(x => new RunItem(x.e.Id, x.e.Type.ToString(), x.e.CanonicalName,
                (range is null ? x.e.Aliases : x.mentions.Select(m => m.Text).Distinct().OrderBy(a => a, StringComparer.Ordinal).ToList()).Where(a => a != x.e.CanonicalName).ToList(),
                x.mentions[0].Chapter, ReviewFile.Quote(bookText, range is null ? x.e.FirstMention : x.mentions[0].CharStart))).ToList();
        var rangeArg = range is { } rg ? $" --chapters {rg.First}-{rg.Last}" : "";
        var scoresFile = SliceFile(ScoresJson, range);
        var failuresFile = SliceFile(FailuresMd, range);

        var result = Scorer.Score(items, gold, rejected);
        var goldRef = new { file = Path.GetRelativePath(root, goldPath).Replace('\\', '/'), sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(goldBytes)) };
        run.RecordCommand(new { command = $"index score --run {run.RunId} --gold {goldRef.file}{rangeArg}{(hideHoldoutDetails ? " --hide-holdout-details" : "")}", startedAt = DateTimeOffset.UtcNow }, new Dictionary<string, object?>());

        // failures.jsonl: replace earlier scoring lines, keep Stage 1/2 lines.
        run.RemoveFailures(line => line["stage"]?.GetValue<string>() == Stage);
        if (hide)
        {
            // Held-out with details hidden: no scoring lines, no matches or counts; only the F1.
            var hidden = new { runId = run.RunId, book = stage2.BookId, gold = goldRef, scoredAt = DateTimeOffset.UtcNow, heldOut = heldChapters, detailsHidden = true, overall = new { f1 = result.Overall.F1 } };
            run.Write(scoresFile, hidden);
            File.WriteAllText(run.File(failuresFile), $"# Failures · run `{run.RunId}`\n\n{HoldOut.Label} (chapter {string.Join(", ", heldChapters)}): details hidden. F1 {result.Overall.F1}.\n");
            return new Outcome(run.RunId, result, hidden, true, true);
        }
        foreach (var f in result.Failures)
            run.AppendFailure(new { type = f.Tag, stage = Stage, runId = run.RunId, name = f.Name, entityType = f.Type, chapter = f.Chapter, quote = f.Quote, detail = f.Detail, runEntity = f.RunEntity, goldEntity = f.GoldEntity });

        var scores = new
        {
            runId = run.RunId,
            book = stage2.BookId,
            gold = goldRef,
            scoredAt = DateTimeOffset.UtcNow,
            heldOut = heldChapters,
            overall = Metrics(result.Overall),
            byType = result.ByType.ToDictionary(kv => kv.Key, kv => Metrics(kv.Value)),
            counts = new
            {
                wrongMerges = result.Count(FailureTags.WrongMerge),
                missedMerges = result.Count(FailureTags.MissedMerge),
                wrongAliases = result.Failures.Count(f => f.Tag == FailureTags.WrongAlias && f.RunEntity is not null),
                missingAliases = result.Failures.Count(f => f.Tag == FailureTags.WrongAlias && f.GoldEntity is not null),
                wrongType = result.Count(FailureTags.WrongType),
                outOfScope = result.Count(FailureTags.OutOfScope),
                spurious = result.Count(FailureTags.SpuriousEntity),
                missed = result.Count(FailureTags.MissedEntity),
            },
            definitions = "See Scorer.cs: one-to-one same-type matching on any shared normalized name; precision = matches / run entities; recall = matches / gold entities.",
            matches = result.Matches,
        };
        run.Write(scoresFile, scores);
        File.WriteAllText(run.File(failuresFile), Digest(run, scores));
        return new Outcome(run.RunId, result, scores, heldChapters.Count > 0, false);
    }

    private static object Metrics(Prf p) => new { precision = p.Precision, recall = p.Recall, f1 = p.F1, matched = p.TruePositives, runEntities = p.Predicted, goldEntities = p.Gold };

    /// <summary>failures.md: every failure in failures.jsonl grouped by tag, with counts and up to 5 examples (name + quote).</summary>
    public static string Digest(RunFolder run, object scores)
    {
        var lines = File.Exists(run.File(RunFolder.FailuresJsonl))
            ? File.ReadAllLines(run.File(RunFolder.FailuresJsonl)).Where(l => l.Length > 0).Select(l => JsonNode.Parse(l)!).ToList()
            : [];
        var s = JsonSerializer.SerializeToNode(scores, RunFolder.Json)!;
        var sb = new StringBuilder();
        sb.AppendLine($"# Failures · run `{run.RunId}`");
        sb.AppendLine();
        sb.AppendLine($"Scored against `{s["gold"]!["file"]}`: precision {s["overall"]!["precision"]}, recall {s["overall"]!["recall"]}, F1 {s["overall"]!["f1"]} " +
                      $"({s["overall"]!["matched"]} matched of {s["overall"]!["runEntities"]} run / {s["overall"]!["goldEntities"]} gold entities).");
        sb.AppendLine();
        sb.AppendLine("| Tag | Count |");
        sb.AppendLine("|---|---|");
        var groups = FailureTags.All.Select(t => (Tag: t, Items: lines.Where(l => l["type"]?.GetValue<string>() == t).ToList())).Where(g => g.Items.Count > 0).ToList();
        foreach (var g in groups) sb.AppendLine($"| {g.Tag} | {g.Items.Count} |");
        foreach (var g in groups)
        {
            sb.AppendLine();
            sb.AppendLine($"## {g.Tag} ({g.Items.Count})");
            sb.AppendLine();
            foreach (var f in g.Items.Take(5)) sb.AppendLine($"- {Example(f)}");
            if (g.Items.Count > 5) sb.AppendLine($"- … {g.Items.Count - 5} more in failures.jsonl");
        }
        return sb.ToString();
    }

    private static string Example(JsonNode f)
    {
        static string Clean(string? x) => (x ?? "").ReplaceLineEndings(" ").Replace("|", "/");
        if (f["mention"] is { } m) // Stage 1 quote mismatch
            return $"**{Clean(m["text"]?.GetValue<string>())}** ({Clean(m["type"]?.GetValue<string>())}, ch{f["chapter"]}, {f["pass"]}) — \"{Clean(m["evidence"]?.GetValue<string>())}\" — {Clean(f["reason"]?.GetValue<string>())}";
        if (f["name"] is null) // Stage 2 merge conflict
            return Clean(f["detail"]?.GetValue<string>());
        return $"**{Clean(f["name"]!.GetValue<string>())}** ({Clean(f["entityType"]?.GetValue<string>())}, ch{f["chapter"]}) — \"{Clean(f["quote"]?.GetValue<string>())}\" — {Clean(f["detail"]?.GetValue<string>())}";
    }
}

/// <summary><c>index score --facts</c>: a run's Stage 3/4 items against facts gold (<see cref="FactsScorer"/>).</summary>
public static class FactsScoring
{
    public const string Stage = "score-facts";
    public const string ScoresJson = "facts-scores.json";

    public static string DefaultGold(string root, RunFolder run, (int First, int Last)? range = null)
    {
        var (first, last) = range ?? IndexCrosscheckCommand.RunChapters(run);
        return Path.Combine(root, "gold", Stage2File.Load(run).BookId, IndexingShared.ChapterSlug(first, last) + ".facts.json");
    }

    public static int Execute(string root, IReadOnlyList<string> runIds, string? goldOption, bool hideHoldoutDetails, (int First, int Last)? range = null)
    {
        var table = new Table().AddColumns("Run", "P", "R", "F1", "Matched", "Run items", "Gold", "Revealed ok", "Story exact / compatible / wrong",
            "Missed", "Spurious", "Wrong endpoint", "Wrong type", "Stated as fact", "Wrong date", "Invented date", "Unstable date");
        foreach (var id in runIds)
        {
            RunFolder run;
            try { run = RunFolder.Open(RepoPaths.Runs(root), id); }
            catch (DirectoryNotFoundException ex) { return IndexingShared.Fail(ex.Message); }
            if (!File.Exists(run.File(RunFolder.Stage3Json))) return IndexingShared.Fail($"{run.RunId} has no stage-3.json.");
            var goldPath = goldOption is { } g ? (Path.IsPathRooted(g) ? g : Path.Combine(root, g)) : DefaultGold(root, run, range);
            if (!File.Exists(goldPath)) return IndexingShared.Fail($"Facts gold not found for {run.RunId}: {Path.GetRelativePath(root, goldPath)}");
            var (scores, r, hidden) = ScoreRun(root, run, goldPath, hideHoldoutDetails, range);
            if (hidden) table.AddRow(run.RunId + $" ({HoldOut.Label}, details hidden)", "", "", $"{r.Overall.F1:0.000}", "", "", "", "", "", "", "", "", "", "", "", "", "");
            else table.AddRow([run.RunId, $"{r.Overall.Precision:0.000}", $"{r.Overall.Recall:0.000}", $"{r.Overall.F1:0.000}", r.Matches.Count.ToString(), r.Overall.Predicted.ToString(),
                r.Overall.Gold.ToString(), $"{r.RevealedAccuracy:0.000}", $"{r.Story("exact")} / {r.Story("compatible")} / {r.Story("wrong")}",
                .. FailureTags.FactScoring.Select(t => r.Count(t).ToString())]);
        }
        AnsiConsole.Write(table);
        return 0;
    }

    public static (object Scores, FactsScoreResult Result, bool Hidden) ScoreRun(string root, RunFolder run, string goldPath, bool hideHoldoutDetails = false,
        (int First, int Last)? range = null)
    {
        var goldBytes = File.ReadAllBytes(goldPath);
        var gold = FactsGold.Load(goldPath);
        if (range is { } gr) gold = gold.Where(g => g.RevealedChapter >= gr.First && g.RevealedChapter <= gr.Last).ToList();
        var stage2 = Stage2File.Load(run);
        var stage3 = IndexingShared.Read<Indexing.Stage3Result>(run, RunFolder.Stage3Json);
        var stage4 = File.Exists(run.File(RunFolder.Stage4Json)) ? IndexingShared.Read<Indexing.Stage4Result>(run, RunFolder.Stage4Json) : null;
        var chapterOf = IndexingShared.ChapterOf(IndexingShared.Stage0For(stage2.BookId));
        var predictions = FactsScorer.Predictions(stage3, stage4, chapterOf);
        if (range is { } pr) predictions = predictions.Where(p => p.RevealedChapter >= pr.First && p.RevealedChapter <= pr.Last).ToList();
        var result = FactsScorer.Score(predictions, gold, FactsScorer.EntityIds(stage2));
        var (first, last) = range ?? IndexCrosscheckCommand.RunChapters(run);
        var scoresFile = ScoreCommand.SliceFile(ScoresJson, range);
        var heldChapters = HoldOut.In(stage2.BookId, first, last);
        var goldRef = new { file = Path.GetRelativePath(root, goldPath).Replace('\\', '/'), sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(goldBytes)) };
        run.RecordCommand(new { command = $"index score --facts --run {run.RunId} --gold {goldRef.file}{(range is { } cr ? $" --chapters {cr.First}-{cr.Last}" : "")}{(hideHoldoutDetails ? " --hide-holdout-details" : "")}", startedAt = DateTimeOffset.UtcNow }, new Dictionary<string, object?>());
        run.RemoveFailures(line => line["stage"]?.GetValue<string>() == Stage);
        if (heldChapters.Count > 0 && hideHoldoutDetails)
        {
            var hidden = new { runId = run.RunId, book = stage2.BookId, gold = goldRef, scoredAt = DateTimeOffset.UtcNow, heldOut = heldChapters, detailsHidden = true, overall = new { f1 = result.Overall.F1 } };
            run.Write(scoresFile, hidden);
            return (hidden, result, true);
        }
        foreach (var f in result.Failures)
            run.AppendFailure(new { type = f.Tag, stage = Stage, runId = run.RunId, gold = f.GoldNumber, item = f.RunId, detail = f.Detail });
        var scores = new
        {
            runId = run.RunId,
            book = stage2.BookId,
            gold = goldRef,
            scoredAt = DateTimeOffset.UtcNow,
            heldOut = heldChapters,
            overall = new { precision = result.Overall.Precision, recall = result.Overall.Recall, f1 = result.Overall.F1, matched = result.Overall.TruePositives, runItems = result.Overall.Predicted, goldItems = result.Overall.Gold },
            revealed = new { matched = result.Matches.Count, correct = result.Matches.Count(m => m.RevealedCorrect), accuracy = result.RevealedAccuracy },
            story = new { exact = result.Story("exact"), compatible = result.Story("compatible"), wrong = result.Story("wrong") },
            counts = FailureTags.FactScoring.ToDictionary(t => t, t => result.Count(t)),
            definitions = "See FactsScorer.cs: one-to-one matching by type, endpoints (resolved entity) and overlapping evidence; wording ignored.",
            matches = result.Matches,
        };
        run.Write(scoresFile, scores);
        return (scores, result, false);
    }
}
