using System.Text.Json.Nodes;
using TemporalGraph.Cli;
using TemporalGraph.Cli.Indexing;
using TemporalGraph.Cli.Runs;

namespace TemporalGraph.Data.Tests;

// The planned-call count that never passes its total, the promote rule, and the estimate's basis.
public sealed class RunPolishTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tg64-" + Guid.NewGuid().ToString("N")[..8]);

    public RunPolishTests()
    {
        Directory.CreateDirectory(RepoPaths.Runs(_root));
        Directory.CreateDirectory(Path.Combine(RepoPaths.Books(_root), "provenance"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public void The_planned_count_grows_with_the_calls_scheduled_and_is_never_passed()
    {
        // 16 chunks with glean: 32 calls planned for Stage 1; Stage 2's blocks are not known ahead.
        var plan = new RunPlanInfo(0, 5, 1, 15, 16, 0, new Dictionary<string, long> { ["stage-1"] = 32, ["stage-4"] = 16, ["stage-5"] = 15 },
            new Dictionary<string, long> { ["stage-1"] = 1000, ["stage-2"] = 1000 });
        Assert.Equal(32, RunProgress.Planned(plan, 1, new StageProgress("stage-1") { Calls = 20 }));
        Assert.Equal("stage 1: 20/32 calls · 0/0 tokens", RunProgress.Line(1, plan, new StageProgress("stage-1") { Calls = 20 }));
        // Two judge calls and a chunk asked again in halves: 36 calls. The total follows; "34/16" cannot happen.
        Assert.Equal(36, RunProgress.Planned(plan, 1, new StageProgress("stage-1") { Calls = 36 }));
        Assert.Equal("stage 1: 36/36 calls · 0/0 tokens", RunProgress.Line(1, plan, new StageProgress("stage-1") { Calls = 36 }));
        // In the run's own process the gate counts a call when it is scheduled, before it has landed.
        var gate = new StageRunner(8, firstAlone: false);
        for (var i = 0; i < 34; i++) gate.CallAsync(() => Task.FromResult(0), TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        Assert.Equal(34, RunProgress.Planned(plan, 1, new StageProgress("stage-1") { Calls = 33 }, gate));
        Assert.Equal(0, RunProgress.Planned(plan, 2, new StageProgress("stage-2") { Calls = 50 }));   // not known ahead: no total, no bar share
    }

    [Fact]
    public void The_estimate_is_the_per_chunk_average_of_three_books()
    {
        Assert.Equal((3, 123), (IndexPlan.EstimateBooks, IndexPlan.EstimateChunks));
        Assert.Equal((86_301L, 40_099L), (IndexPlan.TokensPerChunk.Values.Sum(t => t.In), IndexPlan.TokensPerChunk.Values.Sum(t => t.Out)));
        // The three runs' cost.json totals, per stage, over 16 + 34 + 73 chunks.
        static long Average(params long[] books) => (long)Math.Round(books.Sum() / 123.0);
        Assert.Equal((Average(565_046, 837_071, 2_335_246), Average(554_653, 768_722, 2_087_940)), IndexPlan.TokensPerChunk[1]);
        Assert.Equal((Average(544_464, 652_893, 2_473_158), Average(78_205, 124_953, 371_502)), IndexPlan.TokensPerChunk[2]);
        Assert.Equal((Average(195_279, 316_793, 849_125), Average(107_206, 151_426, 450_887)), IndexPlan.TokensPerChunk[4]);
        Assert.Equal((Average(253_568, 476_498, 1_115_827), Average(31_376, 60_644, 144_727)), IndexPlan.TokensPerChunk[5]);
        Assert.Contains("3 books", IndexPlan.EstimateBasis);
        Assert.Contains("123 chunks; 86,301 in and 40,099 out each", IndexPlan.EstimateBasis);
        Assert.Contains("3 books", IndexPlan.EstimateBasisShort);
    }

    private RunFolder Run() => RunFolder.Create(RepoPaths.Runs(_root));

    private void Promoted(string book, string runId, JsonObject? scores = null)
    {
        File.WriteAllText(Path.Combine(RepoPaths.Books(_root), book + ".sqlite"), "");
        File.WriteAllText(IndexPrepareCommand.ProvenancePath(_root, book), new JsonObject { ["book"] = book, ["runId"] = runId, ["scores"] = scores }.ToJsonString());
    }

    [Fact]
    public void Promote_defaults_to_yes_for_a_book_with_no_graph_and_never_for_a_part_of_a_book()
    {
        var run = Run();
        // A part of a book: no, whatever else holds.
        var (part, why) = PromoteRule.Decide(_root, run, "tiny-tale", (1, 3), 15);
        Assert.False(part);
        Assert.StartsWith("no: this run covers chapters 1–3 of 15", why);
        // A whole book with no promoted graph: yes.
        Assert.Equal((true, "yes: the book has no promoted graph yet"), PromoteRule.Decide(_root, run, "tiny-tale", (1, 15), 15));
        // Promoted before, and the book has no gold: yes, nothing to compare.
        Promoted("tiny-tale", "20260101T000000Z-aaaaaa");
        Assert.Equal((true, "yes: a whole-book run, and the book has no gold to compare it by"), PromoteRule.Decide(_root, run, "tiny-tale", (1, 15), 15));
        Assert.False(PromoteRule.Decide(_root, run, "tiny-tale", (2, 15), 15).Yes);
        Assert.Null(PromoteRule.TuningGold(_root, "tiny-tale"));
    }

    [Fact]
    public void With_gold_a_whole_book_run_is_promoted_by_default_only_at_or_above_the_recorded_scores()
    {
        var recorded = new GoldScores("ch01-03", 0.7010, 0.2348);
        Assert.Equal((true, "yes: on the gold (ch01-03) this run scores entities F1 0.701, facts F1 0.240, at or above the promoted graph's entities F1 0.701, facts F1 0.235"),
            PromoteRule.Compare(new GoldScores("ch01-03", 0.7010, 0.2400), recorded, "r1"));
        var (lower, why) = PromoteRule.Compare(new GoldScores("ch01-03", 0.6329, 0.2500), recorded, "r1");
        Assert.False(lower);
        Assert.Equal("no: on the gold (ch01-03) this run scores entities F1 0.633, facts F1 0.250, below the promoted graph's entities F1 0.701, facts F1 0.235 (run r1)", why);
        Assert.False(PromoteRule.Compare(new GoldScores("ch01-03", 0.80, 0.20), recorded, "r1").Yes);            // below on facts alone
        Assert.True(PromoteRule.Compare(new GoldScores("ch01-03", 0.80, null), recorded, "r1").Yes);            // no graph gold on one side: entities decide
        Assert.StartsWith("yes: a whole-book run (entities F1 0.633", PromoteRule.Compare(new GoldScores("ch01-03", 0.6329, null), null, "r1").Rule);

        // The scores are recorded in the provenance file at promote time and read back from it.
        Promoted("tiny-tale", "r1", recorded.ToJson());
        Assert.Equal(recorded, PromoteRule.Recorded(IndexPrepareCommand.ProvenancePath(_root, "tiny-tale")));
        Promoted("other", "r2");
        Assert.Null(PromoteRule.Recorded(IndexPrepareCommand.ProvenancePath(_root, "other")));
        Assert.Null(PromoteRule.Recorded(Path.Combine(_root, "nowhere.json")));

        // The Hound's tuning gold is chapters 1–3; chapter 8 is held out and never the yardstick.
        var gold = PromoteRule.TuningGold(RepoPaths.FindRoot(), "hound-of-the-baskervilles");
        Assert.Equal((1, 3), gold!.Value.Chapters);
        Assert.EndsWith("ch01-03.json", gold.Value.Path);
    }
}
