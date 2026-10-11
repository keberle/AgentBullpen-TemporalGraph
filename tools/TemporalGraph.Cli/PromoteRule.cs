using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TemporalGraph.Cli.Books;
using TemporalGraph.Cli.Indexing;
using TemporalGraph.Cli.Runs;
using TemporalGraph.Cli.Scoring;

namespace TemporalGraph.Cli;

/// <summary>A run's scores against a book's tuning gold: what the promote rule compares and the provenance file records.</summary>
/// <param name="Gold">The gold's chapters, as its file names them ("ch01-03").</param>
/// <param name="EntitiesF1">Entity F1 on those chapters (<c>index score</c>).</param>
/// <param name="FactsF1">Fact F1 by evidence overlap on those chapters (<c>index score-graph</c>); null when the book has no graph gold.</param>
public sealed record GoldScores(string Gold, double EntitiesF1, double? FactsF1)
{
    public JsonObject ToJson() => new() { ["gold"] = Gold, ["entitiesF1"] = EntitiesF1, ["factsF1"] = FactsF1 };

    public static GoldScores? From(JsonNode? node)
    {
        try { return node is JsonObject o && o["gold"]?.GetValue<string>() is { } gold && o["entitiesF1"]?.GetValue<double>() is { } e ? new GoldScores(gold, e, o["factsF1"]?.GetValue<double>()) : null; }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException) { return null; }
    }
}

/// <summary>
/// Whether a finished run should be promoted when no one said <c>--promote</c>. The answer is the default of the
/// "Promote?" question at a terminal, and what <c>index rebuild</c> does:
/// <list type="number">
/// <item>a run of a part of the book: no, always;</item>
/// <item>the book has no promoted graph: yes;</item>
/// <item>the book has no gold: yes (a whole-book run, nothing to compare);</item>
/// <item>the run's gold scores are at or above the promoted graph's (those its provenance file records, else those of its run folder
/// scored now; neither: nothing to fall below): yes; below on either measure: no.</item>
/// </list>
/// </summary>
public static partial class PromoteRule
{
    [GeneratedRegex(@"^ch(\d{2})(?:-(\d{2}))?\.json$")]
    private static partial Regex GoldFile();

    /// <summary>The book's tuning gold: the first <c>gold/&lt;book&gt;/chNN-NN.json</c> whose chapters hold no held-out chapter; null when there is none.</summary>
    public static (string Path, (int First, int Last) Chapters)? TuningGold(string root, string book)
    {
        var folder = Path.Combine(root, "gold", book);
        if (!Directory.Exists(folder)) return null;
        foreach (var file in Directory.GetFiles(folder, "ch*.json").Order(StringComparer.Ordinal))
        {
            var m = GoldFile().Match(Path.GetFileName(file));
            if (!m.Success) continue;
            var first = int.Parse(m.Groups[1].Value);
            var last = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : first;
            if (HoldOut.In(book, first, last).Count == 0) return (file, (first, last));
        }
        return null;
    }

    /// <summary>The run scored against the book's tuning gold (as <c>index score --chapters</c> does, so the run folder gets its scores
    /// file); null when the book has no gold, the run does not reach the gold's chapters, or it cannot be scored.</summary>
    public static GoldScores? Score(string root, RunFolder run, string book)
    {
        if (TuningGold(root, book) is not { } gold) return null;
        try
        {
            if (!File.Exists(run.File(RunFolder.Stage2Json))) return null;
            var covered = IndexCrosscheckCommand.RunChapters(run);
            if (covered.First > gold.Chapters.First || covered.Last < gold.Chapters.Last) return null;
            var entities = ScoreCommand.ScoreRun(root, run, gold.Path, range: gold.Chapters).Result.Overall.F1;
            double? facts = null;
            var graphGold = GraphScoring.DefaultGold(root, book, gold.Chapters);
            if (File.Exists(graphGold) && Pipeline.IsGraphShape(run, RunFolder.Stage3Json))
                facts = GraphScoring.ScoreRun(root, run, Stage2File.Load(run), graphGold, gold.Chapters, slice: true).ByEvidence.F1;
            return new GoldScores(IndexingShared.ChapterSlug(gold.Chapters.First, gold.Chapters.Last), entities, facts);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException or InvalidOperationException or KeyNotFoundException or NullReferenceException)
        {
            return null;
        }
    }

    /// <summary>The scores a provenance file records (<c>scores</c>, written at promote time); null when it has none.</summary>
    public static GoldScores? Recorded(string provenancePath)
    {
        try { return File.Exists(provenancePath) ? GoldScores.From(JsonNode.Parse(File.ReadAllText(provenancePath))?["scores"]) : null; }
        catch (System.Text.Json.JsonException) { return null; }
    }

    /// <summary>The rule on a finished run: promote or not, and the rule that decided, in words for the terminal and run.log.</summary>
    /// <param name="chapters">The chapters the run covers.</param>
    /// <param name="bookChapters">How many chapters the book has.</param>
    public static (bool Yes, string Rule) Decide(string root, RunFolder run, string book, (int First, int Last) chapters, int bookChapters)
    {
        if (chapters.First != 1 || chapters.Last != bookChapters)
            return (false, $"no: this run covers chapters {chapters.First}–{chapters.Last} of {bookChapters}, and a part of a book is never promoted by default");
        var provenance = IndexPrepareCommand.ProvenancePath(root, book);
        var promotedRun = BookShelf.IndexedRun(root, book);
        if (promotedRun is null || !File.Exists(Path.Combine(RepoPaths.Books(root), book + ".sqlite")))
            return (true, "yes: the book has no promoted graph yet");
        if (TuningGold(root, book) is null) return (true, "yes: a whole-book run, and the book has no gold to compare it by");
        if (Score(root, run, book) is not { } mine) return (false, "no: the run could not be scored against the book's gold");
        var theirs = Recorded(provenance);
        if (theirs is null && Directory.Exists(Path.Combine(RepoPaths.Runs(root), promotedRun)))
            theirs = Score(root, RunFolder.Open(RepoPaths.Runs(root), promotedRun), book);
        return Compare(mine, theirs, promotedRun);
    }

    /// <summary>The fourth rule: a whole-book run's scores against the promoted graph's (null: none recorded and its run folder is gone).</summary>
    public static (bool Yes, string Rule) Compare(GoldScores mine, GoldScores? theirs, string promotedRun)
    {
        static string Scores(GoldScores s) => FormattableString.Invariant($"entities F1 {s.EntitiesF1:0.000}") + (s.FactsF1 is { } f ? FormattableString.Invariant($", facts F1 {f:0.000}") : "");
        if (theirs is null) return (true, $"yes: a whole-book run ({Scores(mine)} on {mine.Gold}), and the promoted graph has no recorded gold scores to fall below");
        var below = mine.EntitiesF1 < theirs.EntitiesF1 || (mine.FactsF1 is { } a && theirs.FactsF1 is { } b && a < b);
        return below
            ? (false, $"no: on the gold ({mine.Gold}) this run scores {Scores(mine)}, below the promoted graph's {Scores(theirs)} (run {promotedRun})")
            : (true, $"yes: on the gold ({mine.Gold}) this run scores {Scores(mine)}, at or above the promoted graph's {Scores(theirs)}");
    }
}
