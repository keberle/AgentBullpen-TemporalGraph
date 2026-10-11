using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using TemporalGraph.Cli.Gold;
using TemporalGraph.Cli.Indexing;
using TemporalGraph.Cli.Runs;
using TemporalGraph.Cli.Scoring;
using TemporalGraph.Data;

namespace TemporalGraph.Cli;

/// <summary>What <c>index score --graph</c> measures, for one run.</summary>
/// <param name="Soft">Fact P/R/F1 by the soft match: the same subject set and text similarity at or above 0.9.</param>
/// <param name="ByEvidence">Fact P/R/F1 by the rule the facts gold was scored with before the full schema (the same subject set and overlapping
/// evidence; the wording ignored), for comparison with earlier runs.</param>
/// <param name="HowChecked">Gold facts with an obvious mode (a claim: <c>told</c>) that matched a run fact by evidence.</param>
/// <param name="HowCorrect">Of those, the run facts that have a DISCOVERED edge with that mode.</param>
/// <param name="PerceptionsListed">The PERCEIVES edges of the chapters (the candidate list).</param>
/// <param name="PerceptionsMarked">How many of them the review file marks right or wrong.</param>
/// <param name="PerceptionsRight">Of the marked ones, those marked right. Precision is over the marked ones only.</param>
public sealed record GraphScore(Prf Soft, Prf ByEvidence, int HowChecked, int HowCorrect, IReadOnlyDictionary<string, int> HowGiven, int PerceptionsListed,
    int PerceptionsMarked, int PerceptionsRight)
{
    public double? HowAccuracy => HowChecked == 0 ? null : Math.Round(HowCorrect / (double)HowChecked, 4);
    public double? PerceptionPrecision => PerceptionsMarked == 0 ? null : Math.Round(PerceptionsRight / (double)PerceptionsMarked, 4);
}

public static class GraphScorer
{
    public const double SoftThreshold = 0.9;

    /// <summary>
    /// Facts of the run against the gold Facts (rejected lines are not gold). A gold name stands for every run entity with that
    /// canonical name or alias; the subject sets are the same when each gold subject can be paired with a different run subject and
    /// none is left over. Matching is one-to-one, the best pair first.
    /// </summary>
    public static (Prf Soft, Prf ByEvidence, List<(GoldGraphFact Gold, GFact Run)> EvidenceMatches) ScoreFacts(IReadOnlyList<GFact> run, IReadOnlyList<GoldGraphFact> gold,
        Func<string, IReadOnlySet<int>> idsOf)
    {
        bool SameSubjects(GFact p, GoldGraphFact g)
        {
            if (g.Subjects.Count != p.Subjects.Count) return false;
            var used = new HashSet<int>();
            foreach (var name in g.Subjects)
            {
                var id = idsOf(name).Where(p.Subjects.Contains).Where(i => !used.Contains(i)).Cast<int?>().FirstOrDefault();
                if (id is null) return false;
                used.Add(id.Value);
            }
            return true;
        }
        List<(GoldGraphFact, GFact)> Pair(Func<GFact, GoldGraphFact, double> score)
        {
            var (freeRun, freeGold, pairs) = (run.ToHashSet(), gold.ToHashSet(), new List<(GoldGraphFact, GFact)>());
            foreach (var (p, g, _) in (from g in gold from p in run where SameSubjects(p, g) let s = score(p, g) where s > 0 select (p, g, s))
                     .OrderByDescending(x => x.s).ThenBy(x => x.p.EvidenceMatch.CharStart))
            {
                if (!freeRun.Contains(p) || !freeGold.Contains(g)) continue;
                freeRun.Remove(p);
                freeGold.Remove(g);
                pairs.Add((g, p));
            }
            return pairs;
        }
        var soft = Pair((p, g) => TextSimilarity.Score(p.Text, g.Text) is var s && s >= SoftThreshold ? s : 0);
        var evidence = Pair((p, g) => Math.Max(0, Math.Min(p.EvidenceMatch.CharEnd, g.EvidenceEnd) - Math.Max(p.EvidenceMatch.CharStart, g.EvidenceStart)));
        return (new Prf(soft.Count, run.Count, gold.Count), new Prf(evidence.Count, run.Count, gold.Count), evidence);
    }
}

/// <summary><c>index score --graph --run &lt;runId&gt; [--chapters a-b] [--gold &lt;file&gt;]</c>: a run of the full schema against the graph gold.</summary>
public static class GraphScoring
{
    public const string ScoresJson = "graph-scores.json";

    public static string DefaultGold(string root, string bookId, (int First, int Last) chapters) =>
        Path.Combine(root, "gold", bookId, IndexingShared.ChapterSlug(chapters.First, chapters.Last) + GraphGold.Suffix);

    public static string PerceptionsReviewPath(string root, string bookId, (int First, int Last) chapters) =>
        Path.Combine(root, "gold", bookId, IndexingShared.ChapterSlug(chapters.First, chapters.Last) + ".perceptions.review.md");

    public static int Execute(string root, IReadOnlyList<string> runIds, string? goldOption, (int First, int Last)? range)
    {
        var table = new Table().AddColumns("Run", "Fact P / R / F1 (same subjects, text ≥ 0.9)", "Fact P / R / F1 (same subjects, evidence overlaps)", "Run facts", "Gold facts",
            "DISCOVERED.how accuracy", "Perception precision");
        foreach (var id in runIds)
        {
            RunFolder run;
            try { run = RunFolder.Open(RepoPaths.Runs(root), id); }
            catch (DirectoryNotFoundException ex) { return IndexingShared.Fail(ex.Message); }
            if (!Pipeline.IsGraphShape(run, RunFolder.Stage3Json)) return IndexingShared.Fail($"{run.RunId} has no stage-3.json of the full-schema shape.");
            var stage2 = Stage2File.Load(run);
            var chapters = range ?? IndexCrosscheckCommand.RunChapters(run);
            if (HoldOut.In(stage2.BookId, chapters.First, chapters.Last).Count > 0) return IndexingShared.Fail($"Chapters {chapters.First}–{chapters.Last} include a held-out chapter; the graph gold covers tuning chapters only.");
            var goldPath = goldOption is { } g ? (Path.IsPathRooted(g) ? g : Path.Combine(root, g)) : DefaultGold(root, stage2.BookId, chapters);
            if (!File.Exists(goldPath)) return IndexingShared.Fail($"Graph gold not found: {Path.GetRelativePath(root, goldPath)} (make it with gold convert).");
            var score = ScoreRun(root, run, stage2, goldPath, chapters, range is not null);
            static string P(Prf p) => $"{p.Precision:0.000} / {p.Recall:0.000} / {p.F1:0.000} ({p.TruePositives})";
            table.AddRow(run.RunId, P(score.Soft), P(score.ByEvidence), score.Soft.Predicted.ToString(), score.Soft.Gold.ToString(),
                score.HowAccuracy is { } a ? $"{a:0.000} ({score.HowCorrect} of {score.HowChecked})" : "not measured",
                score.PerceptionPrecision is { } pr ? $"{pr:0.000} ({score.PerceptionsRight} of {score.PerceptionsMarked} marked; {score.PerceptionsListed} listed)" : $"not measured (0 of {score.PerceptionsListed} marked)");
        }
        AnsiConsole.Write(table);
        return 0;
    }

    public static GraphScore ScoreRun(string root, RunFolder run, Stage2Result stage2, string goldPath, (int First, int Last) chapters, bool slice)
    {
        var gold = GraphGold.Load(goldPath);
        var stage3 = IndexingShared.Read<GraphStage3Result>(run, RunFolder.Stage3Json);
        var runFacts = stage3.Facts.Where(f => f.Chapter >= chapters.First && f.Chapter <= chapters.Last).ToList();
        var goldFacts = gold.Facts.Where(f => f.RevealedChapter >= chapters.First && f.RevealedChapter <= chapters.Last).ToList();
        var (soft, byEvidence, matches) = GraphScorer.ScoreFacts(runFacts, goldFacts, FactsScorer.EntityIds(stage2));
        var withMode = matches.Where(m => m.Gold.How is not null).ToList();
        var howCorrect = withMode.Count(m => m.Run.Discoveries.Any(d => d.How == m.Gold.How));
        var howGiven = withMode.SelectMany(m => m.Run.Discoveries.Select(d => d.How).Distinct()).GroupBy(h => h).OrderBy(x => x.Key, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.Count());

        int listed = 0, marked = 0, right = 0;
        if (File.Exists(run.File(Pipeline.Stage5Json)))
        {
            var edges = IndexingShared.Read<PerceptionStage5Result>(run, Pipeline.Stage5Json).Edges.Where(e => e.Chapter >= chapters.First && e.Chapter <= chapters.Last).ToList();
            listed = edges.Count;
            var review = PerceptionsReviewPath(root, stage2.BookId, chapters);
            if (File.Exists(review) && File.ReadAllText(review).Contains($"`{run.RunId}`", StringComparison.Ordinal))
            {
                var marks = GraphGold.PerceptionMarks(File.ReadAllText(review));
                marked = edges.Count(e => marks.ContainsKey(e.Id));
                right = edges.Count(e => marks.TryGetValue(e.Id, out var ok) && ok);
            }
        }
        var score = new GraphScore(soft, byEvidence, withMode.Count, howCorrect, howGiven, listed, marked, right);
        static object Metrics(Prf p) => new { precision = p.Precision, recall = p.Recall, f1 = p.F1, matched = p.TruePositives, runFacts = p.Predicted, goldFacts = p.Gold };
        run.Write(ScoreCommand.SliceFile(ScoresJson, slice ? chapters : null), new
        {
            runId = run.RunId,
            book = stage2.BookId,
            chapters = new { first = chapters.First, last = chapters.Last },
            gold = new { file = Path.GetRelativePath(root, goldPath).Replace('\\', '/'), sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(goldPath))) },
            scoredAt = DateTimeOffset.UtcNow,
            factsSoftMatch = Metrics(soft),
            factsEvidenceMatch = Metrics(byEvidence),
            discoveredHow = new { goldFactsWithAnObviousMode = goldFacts.Count(f => f.How is not null), matchedByEvidence = score.HowChecked, correct = score.HowCorrect, accuracy = score.HowAccuracy, runModesOnThose = howGiven },
            perceptions = new { listed, marked, right, precision = score.PerceptionPrecision, rule = "precision is over the marked lines only; an unmarked candidate is never counted wrong" },
            definitions = "Soft match: the same subject set and TextSimilarity >= 0.9 (Smith-Waterman over the longer text). Evidence match: the same subject set and overlapping evidence spans. One-to-one, best pair first. Gold facts are the accepted, edited and added lines of the review files, converted by gold convert.",
        });
        run.RecordCommand(new { command = $"index score --graph --run {run.RunId}{(slice ? $" --chapters {chapters.First}-{chapters.Last}" : "")}", startedAt = DateTimeOffset.UtcNow }, new Dictionary<string, object?>());
        return score;
    }
}

/// <summary><c>gold convert --book &lt;id&gt; --file gold/&lt;id&gt;/ch01-03.facts.json</c>: the edge-shaped facts gold into the shape of the full
/// schema (Fact nodes with subjects and, for claims, the mode <c>told</c>; meetings as Events), keeping every mark.</summary>
public sealed class GoldConvertCommand : Command<GoldConvertCommand.Settings>
{
    public sealed class Settings : GlobalSettings
    {
        [CommandOption("--book <ID>")] public string Book { get; init; } = "";
        [CommandOption("--file <FILE>")][Description("The facts gold JSON from gold import, e.g. gold/hound-of-the-baskervilles/ch01-03.facts.json.")] public string File { get; init; } = "";
        [CommandOption("--out <FILE>")][Description("Default: next to it, '.facts.json' → '.graph.json'.")] public string? Out { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings, CancellationToken ct)
    {
        if (!File.Exists(settings.File)) return IndexingShared.Fail($"File not found: {settings.File}");
        var outPath = settings.Out ?? GraphGold.PathFor(settings.File);
        var gold = GraphGold.ConvertFile(settings.File, outPath);
        if (gold.Book != settings.Book) return IndexingShared.Fail($"{settings.File} is the gold of '{gold.Book}', not '{settings.Book}'.");
        AnsiConsole.MarkupLine($"Wrote [bold]{Markup.Escape(outPath)}[/]: {gold.Facts.Count} facts ({gold.Facts.Count(f => f.How is not null)} with an obvious discovery mode), " +
            $"{gold.Events.Count} events ({gold.Events.Count(e => e.From == "met")} from MET edges), {gold.Relations.Count} relations; {gold.RejectedFacts.Count + gold.RejectedEvents.Count + gold.RejectedRelations.Count} rejected lines kept as rejected.");
        return 0;
    }
}

/// <summary><c>gold perceptions-review --run &lt;runId&gt; [--chapters a-b]</c>: a run's PERCEIVES edges as a checklist to mark later
/// (gold/&lt;book&gt;/chNN-NN.perceptions.review.md). Candidates, not gold.</summary>
public sealed class GoldPerceptionsReviewCommand : Command<GoldPerceptionsReviewCommand.Settings>
{
    public sealed class Settings : GlobalSettings
    {
        [CommandOption("--run <RUN>")] public string Run { get; init; } = "";
        [CommandOption("--chapters <RANGE>")][Description("Only these chapters of the run, e.g. 1-3.")] public string? Chapters { get; init; }
        [CommandOption("--out <FILE>")] public string? Out { get; init; }
        [CommandOption("--force")][Description("Overwrite a file that already has marks.")] public bool Force { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings, CancellationToken ct)
    {
        var root = RepoPaths.FindRoot();
        RunFolder run;
        try { run = RunFolder.Open(RepoPaths.Runs(root), settings.Run); }
        catch (DirectoryNotFoundException ex) { return IndexingShared.Fail(ex.Message); }
        if (!File.Exists(run.File(Pipeline.Stage5Json))) return IndexingShared.Fail($"{run.RunId} has no {Pipeline.Stage5Json}; run index perceive first.");
        (int First, int Last) chapters;
        if (settings.Chapters is { } spec)
        {
            if (IndexingShared.ParseChapters(spec) is not { } parsed) return IndexingShared.Fail("--chapters must look like 1-3.");
            chapters = parsed;
        }
        else chapters = IndexCrosscheckCommand.RunChapters(run);
        var stage2 = Stage2File.Load(run);
        var path = settings.Out ?? GraphScoring.PerceptionsReviewPath(root, stage2.BookId, chapters);
        if (File.Exists(path) && !settings.Force && GraphGold.PerceptionMarks(File.ReadAllText(path)).Count > 0)
            return IndexingShared.Fail($"{Path.GetRelativePath(root, path)} already has marks; not overwriting it. Use --out <other file>, or --force.");
        var title = BookManifest.Read(Path.Combine(RepoPaths.Books(root), BookManifest.FileName)).First(b => b.Id == stage2.BookId).Title;
        var content = GraphGold.PerceptionsReview(title, chapters.First == chapters.Last ? $"{chapters.First}" : $"{chapters.First}–{chapters.Last}", run.RunId,
            IndexingShared.Read<PerceptionStage5Result>(run, Pipeline.Stage5Json), IndexingShared.Read<GraphStage3Result>(run, RunFolder.Stage3Json),
            stage2.Entities.ToDictionary(e => e.Id, e => e.CanonicalName), chapters.First, chapters.Last);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, content);
        AnsiConsole.MarkupLine($"Wrote [bold]{Markup.Escape(Path.GetRelativePath(root, path))}[/]: {content.Split('\n').Count(l => l.StartsWith("- [ ] "))} candidate lines.");
        return 0;
    }
}
