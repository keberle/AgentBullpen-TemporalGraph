using System.ComponentModel;
using System.Text.Json.Nodes;
using Spectre.Console;
using Spectre.Console.Cli;
using TemporalGraph.Cli.Indexing;
using TemporalGraph.Llm;
using TemporalGraph.Cli.Runs;

namespace TemporalGraph.Cli;

/// <summary>
/// <c>index dedupe --run &lt;runId&gt; [--threshold t]</c>: the Stage 3 run-wide de-duplication (<see cref="RunDedupe"/>)
/// re-applied to an existing run's Stage 3 output, into a new run (frozen copy of Stages 1–3, no LLM calls). The source's
/// Stage 4 output, if any, is carried over with item ids renumbered and the removed items dropped, so the deduped run can be
/// scored and built like the source. A source written before the run-wide dedupe is deduped as Stage 3 now would; on a later run the
/// step is idempotent (it finds nothing more).
/// </summary>
public sealed class IndexDedupeCommand : Command<IndexDedupeCommand.Settings>
{
    public sealed class Settings : GlobalSettings
    {
        [CommandOption("--run <RUN>")] public string Run { get; init; } = "";
        [CommandOption("--threshold <J>")][Description("Token-Jaccard threshold for Fact/Event texts (default Indexing:Stage3:DedupeThreshold, 0.6).")] public double? Threshold { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings, CancellationToken ct)
    {
        var root = RepoPaths.FindRoot();
        var threshold = settings.Threshold ?? IndexingShared.IndexingSettings.From(LlmSettings.LoadConfiguration()).Stage3DedupeThreshold;
        if (threshold is <= 0 or > 1) return IndexingShared.Fail("--threshold must be in (0, 1].");
        RunFolder source;
        try { source = RunFolder.Open(RepoPaths.Runs(root), settings.Run); }
        catch (DirectoryNotFoundException ex) { return IndexingShared.Fail(ex.Message); }
        if (!File.Exists(source.File(RunFolder.Stage3Json))) return IndexingShared.Fail($"{source.RunId} has no stage-3.json.");

        var (run, result, merges) = Execute(source, RepoPaths.Runs(root), threshold);
        var d = result.Dedupe!;
        var table = new Table().AddColumns("Source", "New run", "MET/IDENTICAL_TO removed", "RELATED_TO removed", "LOCATED_AT removed", "Nodes merged", "Threshold", "Edges", "Nodes");
        table.AddRow(source.RunId, run.RunId, d.MetRemoved.ToString(), d.RelatedRemoved.ToString(), d.LocatedRemoved.ToString(), d.NodesMerged.ToString(),
            d.Threshold.ToString(System.Globalization.CultureInfo.InvariantCulture), result.Edges.Count.ToString(), result.Nodes.Count.ToString());
        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"Run [bold]{run.RunId}[/]: {Markup.Escape(run.Path)} ({merges.Count} duplicate lines in failures.jsonl)");
        return 0;
    }

    /// <summary>The frozen copy, the dedupe, and the remapped Stage 4. Returns the new run and its Stage 3 result.</summary>
    public static (RunFolder Run, Stage3Result Result, List<RunDedupeMerge> Merges) Execute(RunFolder source, string runsRoot, double threshold)
    {
        var run = FrozenStages.Copy(source, runsRoot, 3);
        var before = IndexingShared.Read<Stage3Result>(run, RunFolder.Stage3Json);
        var (result, merges, newIds) = RunDedupe.Apply(before, threshold);
        var old = JsonNode.Parse(File.ReadAllText(run.File(RunFolder.Stage3Json)))!;
        IndexingShared.WriteWith(run, RunFolder.Stage3Json, result, new Dictionary<string, object?>
        {
            ["stage"] = Stage3.StageName, ["skill"] = old["skill"]?.DeepClone(), ["counts"] = Stage3.Counts(result),
            ["dedupeFrom"] = new { runId = source.RunId, threshold },
        });
        var bookId = before.BookId;
        foreach (var m in merges) run.AppendFailure(RunDedupe.FailureLine(m, run.RunId, bookId));

        // Stage 4: keep the kept items' times under their new ids; drop removed items and orderings that refer to them.
        if (File.Exists(source.File(RunFolder.Stage4Json)))
        {
            var s4 = IndexingShared.Read<Stage4Result>(source, RunFolder.Stage4Json);
            var items = s4.Items.Where(i => newIds.ContainsKey(i.Id)).Select(i => i with { Id = newIds[i.Id] }).ToList();
            var order = s4.Order.Where(o => newIds.ContainsKey(o.A) && newIds.ContainsKey(o.B)).Select(o => o with { A = newIds[o.A], B = newIds[o.B] }).ToList();
            var s4old = JsonNode.Parse(File.ReadAllText(source.File(RunFolder.Stage4Json)))!;
            var remapped = s4 with { Items = items, Order = order };
            IndexingShared.WriteWith(run, RunFolder.Stage4Json, remapped, new Dictionary<string, object?>
            {
                ["stage"] = Stage4.StageName, ["skill"] = s4old["skill"]?.DeepClone(), ["counts"] = s4old["counts"]?.DeepClone(),
                ["stage4From"] = new { runId = source.RunId, remappedBy = "index dedupe", itemsDropped = s4.Items.Count - items.Count, ordersDropped = s4.Order.Count - order.Count },
            });
        }
        run.RecordCommand(new { command = $"index dedupe --run {source.RunId} --threshold {threshold.ToString(System.Globalization.CultureInfo.InvariantCulture)}", startedAt = DateTimeOffset.UtcNow },
            new Dictionary<string, object?> { ["dedupeFrom"] = new { runId = source.RunId, threshold } });
        return (run, result, merges);
    }
}
