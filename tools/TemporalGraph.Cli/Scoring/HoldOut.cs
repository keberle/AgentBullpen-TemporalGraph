namespace TemporalGraph.Cli.Scoring;

/// <summary>
/// Held-out chapters: gold the improvement loop never sees the details of while it edits skills.
/// A run whose chapters include any of these is a held-out run: <c>index score</c> reports it separately, labeled
/// "held-out", and with <c>--hide-holdout-details</c> it reports only the F1 and writes no failure details.
/// Changing this list is a code change on purpose: the loop must not be able to edit it.
/// </summary>
public static class HoldOut
{
    public const string Label = "held-out";

    public static readonly IReadOnlyDictionary<string, IReadOnlyList<int>> Chapters = new Dictionary<string, IReadOnlyList<int>>
    {
        ["hound-of-the-baskervilles"] = [8], // ch. 1–3 tune, skill examples come from ch. 4–7
    };

    /// <summary>The held-out chapters inside [first, last] for the book (empty when none).</summary>
    public static IReadOnlyList<int> In(string bookId, int first, int last) =>
        Chapters.TryGetValue(bookId, out var held) ? held.Where(c => c >= first && c <= last).ToList() : [];

    public static bool Touches(string bookId, int first, int last) => In(bookId, first, last).Count > 0;

    public sealed record SkippedRun(string RunId, string Reason);

    /// <summary>
    /// The run folders the improvement loop (<c>index improve</c>) may read. A held-out run folder is skipped
    /// entirely: none of its files (failures, scores, stage output) reach the loop. A folder whose run.json is missing
    /// or has no book and chapters is skipped too, since it can't be shown not to be held out.
    /// </summary>
    public static (IReadOnlyList<Runs.RunFolder> Runs, IReadOnlyList<SkippedRun> Skipped) LoopRunFolders(string runsRoot)
    {
        var runs = new List<Runs.RunFolder>();
        var skipped = new List<SkippedRun>();
        if (!Directory.Exists(runsRoot)) return (runs, skipped);
        foreach (var dir in Directory.GetDirectories(runsRoot).Order(StringComparer.Ordinal))
        {
            var run = Runs.RunFolder.Open(runsRoot, dir);
            string? book = null;
            int first = 0, last = 0;
            try
            {
                var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(run.File(Runs.RunFolder.RunJson)))!;
                book = json["book"]!.GetValue<string>();
                (first, last) = (json["chapters"]!["first"]!.GetValue<int>(), json["chapters"]!["last"]!.GetValue<int>());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or NullReferenceException or InvalidOperationException or FormatException)
            {
                skipped.Add(new SkippedRun(run.RunId, "run.json missing or without book and chapters"));
                continue;
            }
            if (Touches(book, first, last)) skipped.Add(new SkippedRun(run.RunId, Label));
            else runs.Add(run);
        }
        return (runs, skipped);
    }
}
