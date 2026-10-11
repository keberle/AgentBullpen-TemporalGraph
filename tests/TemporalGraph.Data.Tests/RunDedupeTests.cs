using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using TemporalGraph.Cli;
using TemporalGraph.Cli.Indexing;
using TemporalGraph.Cli.Runs;
using TemporalGraph.Cli.Scoring;

namespace TemporalGraph.Data.Tests;

// The Stage 3 run-wide de-duplication (RunDedupe), index dedupe, and the Indexing:Llm default profile.
public class RunDedupeTests
{
    private static EvidenceMatch Ev(int start) => new("exact", 1, start, start + 10);

    private static GraphEdge Edge(string id, string type, int s, int t, int start, string? kind = null) =>
        new(id, type, s, t, kind, 0, 1, "q", Ev(start));

    private static GraphNode Node(string id, string label, string text, int start, params int[] entities) =>
        new(id, label, text, 0, 1, "q", Ev(start), entities.Select(e => new ExtractedLink("ABOUT", e)).ToList());

    [Fact]
    public void Met_and_identical_to_are_kept_once_per_unordered_pair_the_earliest()
    {
        var (edges, _, counts, merges, _) = RunDedupe.Apply(
        [
            Edge("E1", "MET", 1, 2, 500), Edge("E2", "MET", 2, 1, 100), Edge("E3", "MET", 1, 3, 300),
            Edge("E4", "IDENTICAL_TO", 4, 5, 50), Edge("E5", "IDENTICAL_TO", 5, 4, 60),
        ], []);

        Assert.Equal(2, counts.MetRemoved); // E1 into E2 (same pair), E5 into E4
        Assert.Equal(["IDENTICAL_TO 4-5 @50", "MET 2-1 @100", "MET 1-3 @300"], edges.Select(e => $"{e.Type} {e.Source}-{e.Target} @{e.EvidenceMatch.CharStart}"));
        Assert.Equal(["E1", "E2", "E3"], edges.Select(e => e.Id)); // renumbered in book order
        Assert.All(merges, m => Assert.Equal("edge", m.Item));
        Assert.Equal("E2", merges.Single(m => m.DroppedId == "E1").KeptId); // the kept MET 2-1 is E2 after renumbering
    }

    [Fact]
    public void Related_to_is_kept_once_per_pair_and_kind_with_contained_kinds_merged_into_the_shorter()
    {
        var (edges, _, counts, _, _) = RunDedupe.Apply(
        [
            Edge("E1", "RELATED_TO", 1, 2, 100, "personal friend and medical attendant"),
            Edge("E2", "RELATED_TO", 2, 1, 200, "Friend"),             // contained in E1's kind: merged, E1 keeps the shorter kind
            Edge("E3", "RELATED_TO", 1, 2, 300, "  FRIEND "),          // same normalized kind: merged
            Edge("E4", "RELATED_TO", 1, 2, 400, "nephew"),             // another kind: kept
            Edge("E5", "RELATED_TO", 1, 3, 500, "friend"),             // another pair: kept
        ], []);

        Assert.Equal(2, counts.RelatedRemoved);
        Assert.Equal(["1-2 Friend @100", "1-2 nephew @400", "1-3 friend @500"], edges.Select(e => $"{e.Source}-{e.Target} {e.Kind} @{e.EvidenceMatch.CharStart}"));
    }

    [Fact]
    public void Located_at_is_kept_once_per_source_and_target_unless_story_dates_differ()
    {
        IReadOnlyList<GraphEdge> input = [Edge("E1", "LOCATED_AT", 1, 9, 100), Edge("E2", "LOCATED_AT", 1, 9, 200), Edge("E3", "LOCATED_AT", 9, 1, 300), Edge("E4", "LOCATED_AT", 1, 9, 400)];
        var (edges, _, counts, _, _) = RunDedupe.Apply(input, []);
        Assert.Equal(2, counts.LocatedRemoved); // E2 and E4 into E1; E3 is another direction
        Assert.Equal(2, edges.Count);

        // With known, different story dates both stay; null vs a date is still a duplicate.
        var dated = RunDedupe.Apply(input, [], storyOf: id => id switch { "E1" => "1884", "E2" => "1889", _ => null });
        Assert.Equal(1, dated.Counts.LocatedRemoved);
        Assert.Equal([100, 200, 300], dated.Edges.Select(e => e.EvidenceMatch.CharStart)); // E1 1884, E2 1889, E3; E4 (undated) merged into E1
    }

    [Theory]
    [InlineData("Sir Charles was a widower.", "Sir Charles Baskerville was a widower.", 0.6, true)]   // Jaccard 5/6 ≈ 0.83
    [InlineData("Holmes is late in the mornings.", "Holmes is usually very late in the mornings.", 0.6, true)] // 6/8 = 0.75
    [InlineData("Holmes is late in the mornings.", "Holmes is usually very late in the mornings.", 0.8, false)]
    [InlineData("Mortimer walks a great deal.", "Mortimer owns a curly-haired spaniel.", 0.6, false)]
    [InlineData("The stick is dated 1884.", "The stick, dated 1884!", 0.9, true)]                       // same without stopwords/punctuation
    public void Node_texts_merge_at_or_above_the_threshold_or_when_equal_without_stopwords(string a, string b, double threshold, bool merged)
    {
        var (_, nodes, counts, _, _) = RunDedupe.Apply([], [Node("N1", "Fact", a, 100, 1), Node("N2", "Fact", b, 900, 1)], threshold);
        Assert.Equal(merged ? 1 : 0, counts.NodesMerged);
        Assert.Equal(merged ? 1 : 2, nodes.Count);
    }

    [Fact]
    public void Jaccard_exactly_at_the_threshold_merges()
    {
        // {a b c d e} vs {a b c f g}: 3/7 ≈ 0.4286. Threshold 3/7 merges, just above does not.
        Assert.True(RunDedupe.NearIdentical("a b c d e", "a b c f g", 3.0 / 7, out var j));
        Assert.Equal(3.0 / 7, j, 10);
        Assert.False(RunDedupe.NearIdentical("a b c d e", "a b c f g", 3.0 / 7 + 0.001, out _));
    }

    [Fact]
    public void Node_merge_needs_the_same_label_and_entity_set_and_carries_the_later_evidence()
    {
        var (_, nodes, counts, merges, _) = RunDedupe.Apply([],
        [
            Node("N1", "Fact", "Sir Charles was a widower.", 100, 1),
            Node("N2", "Fact", "Sir Charles was a widower.", 900, 1, 2),   // another entity set: kept
            Node("N3", "Event", "Sir Charles was a widower.", 950, 1),     // another label: kept
            Node("N4", "Fact", "Sir Charles was a widower.", 1500, 1),     // merged into N1
            Node("N5", "Fact", "Sir Charles Baskerville was a widower.", 2000, 1), // merged into N1 too
        ]);

        Assert.Equal(2, counts.NodesMerged);
        var kept = nodes.Single(n => n.Label == "Fact" && n.Links.Count == 1);
        Assert.Equal(100, kept.EvidenceMatch.CharStart);                   // revealed_pos stays the earliest
        Assert.Equal([[1500, 1510], [2000, 2010]], kept.AlsoEvidence!.Select(x => x).ToList());
        Assert.Equal(3, nodes.Count);
        Assert.All(merges, m => Assert.Equal(kept.Id, m.KeptId));
        Assert.Equal(0.833, merges.Single(m => m.DroppedId == "N5").Jaccard); // 5/6
    }

    [Fact]
    public void Index_dedupe_gives_the_same_counts_as_the_stage_post_step_and_remaps_stage_4()
    {
        var root = Path.Combine(Path.GetTempPath(), "TemporalGraph.Data.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var stage3 = new Stage3Result("hound-of-the-baskervilles", 1, 1, 0.9, [],
                [Edge("E1", "MET", 1, 2, 100), Edge("E2", "MET", 2, 1, 300), Edge("E3", "LOCATED_AT", 1, 9, 400)],
                [Node("N1", "Fact", "Holmes is late.", 200, 1), Node("N2", "Fact", "Holmes is late!", 600, 1)], [], 0);
            var source = RunFolder.Create(root);
            File.WriteAllText(source.File(RunFolder.Stage1Json), "{}");
            File.WriteAllText(source.File(RunFolder.Stage2Json), "{}");
            source.Write(RunFolder.Stage3Json, stage3);
            source.Write(RunFolder.Stage4Json, new Stage4Result("hound-of-the-baskervilles", 0.9, [],
                [new TimedItem("E3", "edge", 1, 400, null, null, null, null, 2.0, true), new TimedItem("N2", "node", 1, 600, null, null, null, null, 3.0, true)],
                [new OrderRelation("E3", "N1", "BEFORE", 1, "q", Ev(400))], []));
            source.RecordCommand(new { command = "test" }, new Dictionary<string, object?> { ["book"] = "hound-of-the-baskervilles" });

            var (run, result, merges) = IndexDedupeCommand.Execute(source, root, 0.6);

            var expected = RunDedupe.Apply(stage3, 0.6).Result.Dedupe;
            Assert.Equal(expected, result.Dedupe);
            Assert.Equal(new RunDedupeCounts(1, 0, 0, 1, 0.6), result.Dedupe);
            var written = JsonNode.Parse(File.ReadAllText(run.File(RunFolder.Stage3Json)))!;
            Assert.Equal(1, written["dedupe"]!["metRemoved"]!.GetValue<int>());
            Assert.Equal(1, written["dedupe"]!["nodesMerged"]!.GetValue<int>());
            Assert.Equal(source.RunId, written["dedupeFrom"]!["runId"]!.GetValue<string>());
            var failures = File.ReadAllLines(run.File(RunFolder.FailuresJsonl)).Select(l => JsonNode.Parse(l)!).ToList();
            Assert.Equal(2, failures.Count(f => f["type"]!.GetValue<string>() == FailureTags.Duplicate));
            Assert.Equal(2, merges.Count);

            // Stage 4: E3 → E2 (MET E2 removed, LOCATED_AT renumbered), N2 merged away (dropped), the ordering E3 → N1 remapped.
            var s4 = IndexingShared.Read<Stage4Result>(run, RunFolder.Stage4Json);
            Assert.Equal(["E2"], s4.Items.Select(i => i.Id));
            Assert.Equal(("E2", "N1"), (s4.Order.Single().A, s4.Order.Single().B));

            // Idempotent: deduping the deduped run finds nothing.
            var (_, again, _) = IndexDedupeCommand.Execute(run, root, 0.6);
            Assert.Equal(new RunDedupeCounts(0, 0, 0, 0, 0.6), again.Dedupe);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Indexing_uses_the_indexing_llm_profile_while_chat_keeps_the_llm_section()
    {
        var template = new ConfigurationBuilder().AddJsonFile(Path.Combine(AppContext.BaseDirectory, "templates", "cli.appsettings.json")).Build();
        Assert.Equal("haiku55", template["Indexing:Llm"]);
        Assert.Equal("haiku55", IndexingShared.IndexingSettings.From(template).DefaultLlmProfile);
        Assert.Equal(0.6, IndexingShared.IndexingSettings.From(template).Stage3DedupeThreshold);

        // Without a configured haiku55 the stages name its missing keys (exit 2); the Llm section is not used.
        var root = Path.Combine(Path.GetTempPath(), "TemporalGraph.Data.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var config = new ConfigurationBuilder().AddJsonFile(Path.Combine(AppContext.BaseDirectory, "templates", "cli.appsettings.json"))
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Llm:Type"] = "OpenAI", ["Llm:Model"] = "m", ["Llm:ApiKey"] = "k-123" }).Build();
            Assert.Null(IndexingShared.Llm(config, RunFolder.Create(root), "stage-1"));
            Assert.Contains("Llm:Profiles:haiku55:Type is not set.", TemporalGraph.Llm.LlmSettings.From(config, IndexingShared.IndexingSettings.From(config).DefaultLlmProfile).Validate());
            // An explicit --llm still wins, and chat/llm-check read the Llm section (profile null).
            Assert.NotNull(IndexingShared.Llm(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                { ["Indexing:Llm"] = "haiku55", ["Llm:Profiles:x:Type"] = "OpenAI", ["Llm:Profiles:x:Model"] = "m", ["Llm:Profiles:x:ApiKey"] = "k" }).Build(), RunFolder.Create(root), "stage-1", "x"));
            Assert.Empty(TemporalGraph.Llm.LlmSettings.From(config).Validate());
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
