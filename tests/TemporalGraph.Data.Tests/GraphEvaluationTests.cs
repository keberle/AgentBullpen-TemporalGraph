using TemporalGraph.Cli;
using TemporalGraph.Cli.Crosscheck;
using TemporalGraph.Cli.Gold;
using TemporalGraph.Cli.Indexing;

namespace TemporalGraph.Data.Tests;

// The facts gold converted to the full schema, the graph scores, the perception candidates and the LitBank
// coreference agreement.
public class GraphEvaluationTests
{
    private static GoldFact Node(string number, string label, string text, int start, string status, params (string Type, string Entity)[] links) =>
        new(number, "node", label, null, null, null, text, links.Select(l => new GoldLink(l.Type, l.Entity)).ToList(), "q", 1, null, null, status, 0, start, start + 50);

    private static GoldFact Edge(string number, string type, string source, string target, string? kind = null) =>
        new(number, "edge", type, kind, source, target, null, [], "q", 1, null, null, "accepted", 0, 900, 950);

    [Fact]
    public void Gold_convert_makes_facts_with_subjects_claims_told_and_meetings_events_and_keeps_the_marks()
    {
        var (facts, events, relations) = GraphGold.Convert(
        [
            Node("3", "Fact", "Mortimer has a stick dated 1884.", 100, "edited", ("ABOUT", "Dr. James Mortimer")),
            Node("5", "Fact", "Mortimer is an elderly country practitioner.", 200, "accepted", ("STATES", "Dr. Watson"), ("ABOUT", "Dr. James Mortimer")),
            Node("9", "Event", "Mortimer calls at Baker Street.", 300, "added", ("PARTICIPATED_IN", "Dr. James Mortimer"), ("LOCATED_AT", "Baker Street"), ("ABOUT", "Mr. Sherlock Holmes")),
            Edge("4", "MET", "Mr. Sherlock Holmes", "Dr. Watson"),
            Edge("19", "LOCATED_AT", "Dr. James Mortimer", "Charing Cross Hospital"),
            Edge("45", "RELATED_TO", "Dr. James Mortimer", "Sir Charles Baskerville", "friend"),
        ]);

        Assert.Equal(["3:edited::", "5:accepted:told:Dr. Watson"], facts.Select(f => $"{f.Number}:{f.Status}:{f.How}:{f.Speaker}"));
        Assert.All(facts, f => Assert.Equal(["Dr. James Mortimer"], f.Subjects));
        // The Event node keeps its participants and place; the MET edge becomes an Event with the two as participants.
        Assert.Equal(["9:event:Dr. James Mortimer:Baker Street", "4:met:Mr. Sherlock Holmes,Dr. Watson:"], events.Select(e => $"{e.Number}:{e.From}:{string.Join(",", e.Participants)}:{e.Location}"));
        Assert.Equal(["19:LOCATED_AT:", "45:RELATED_TO:friend"], relations.Select(r => $"{r.Number}:{r.Type}:{r.Kind}"));
        // A rejected line stays rejected.
        Assert.Equal("rejected", GraphGold.Convert([Node("7", "Fact", "x", 1, "accepted", ("ABOUT", "Dr. Watson"))], "rejected").Facts.Single().Status);
    }

    [Fact]
    public void The_committed_graph_gold_is_the_conversion_of_the_committed_facts_gold()
    {
        var folder = Path.Combine(TemporalGraph.Cli.Runs.RepoPaths.FindRoot(), "gold", "hound-of-the-baskervilles");
        var committed = GraphGold.Load(Path.Combine(folder, "ch01-03.graph.json"));
        var temp = Path.Combine(Path.GetTempPath(), $"TemporalGraph.Data.Tests-{Guid.NewGuid():N}.json");
        try
        {
            var again = GraphGold.ConvertFile(Path.Combine(folder, "ch01-03.facts.json"), temp);
            static string J(object o) => System.Text.Json.JsonSerializer.Serialize(o);
            Assert.Equal(J(again.Facts), J(committed.Facts));
            Assert.Equal(J(again.Events), J(committed.Events));
            Assert.Equal(J(again.Relations), J(committed.Relations));
            Assert.Equal(again.RejectedFacts.Count + again.RejectedEvents.Count + again.RejectedRelations.Count, committed.RejectedFacts.Count + committed.RejectedEvents.Count + committed.RejectedRelations.Count);
            // Nothing added, nothing lost: every line of the facts gold is one item here.
            var facts = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "ch01-03.facts.json")))!;
            Assert.Equal(facts["items"]!.AsArray().Count, committed.Facts.Count + committed.Events.Count + committed.Relations.Count);
        }
        finally { File.Delete(temp); }
    }

    [Fact]
    public void Fact_scores_the_soft_match_needs_the_same_subjects_and_near_identical_text_the_evidence_match_ignores_the_wording()
    {
        GFact Run(string id, string text, int start, params int[] subjects) => new(id, text, subjects, 0, 1, "q", new EvidenceMatch("exact", 1, start, start + 50), [new GDiscovery("E1", "told", 3)], 3);
        GoldGraphFact Gold(string number, string text, int start, params string[] subjects) => new(number, text, subjects, "q", start, start + 50, 1, "accepted", null, null);
        IReadOnlySet<int> Ids(string name) => name switch { "Dr. James Mortimer" => new HashSet<int> { 1 }, "Dr. Watson" => new HashSet<int> { 2 }, _ => new HashSet<int>() };

        var (soft, evidence, matches) = GraphScorer.ScoreFacts(
            [
                Run("F1", "Mortimer has a stick dated 1884.", 100, 1),                       // the gold's wording
                Run("F2", "The visitor is an old country doctor.", 200, 1),                   // the same fact, other words
                Run("F3", "Mortimer has a stick dated 1884.", 5000, 2),                       // other subject
                Run("F4", "Something the gold does not have.", 7000, 1),
            ],
            [Gold("3", "Mortimer has a stick dated 1884", 100, "Dr. James Mortimer"), Gold("5", "Mortimer is an elderly country practitioner.", 220, "Dr. James Mortimer"), Gold("8", "Never found.", 9000, "Dr. Watson")],
            Ids);

        Assert.Equal((1, 4, 3), (soft.TruePositives, soft.Predicted, soft.Gold));
        Assert.Equal((2, 4, 3), (evidence.TruePositives, evidence.Predicted, evidence.Gold));
        Assert.Equal(["3:F1", "5:F2"], matches.Select(m => $"{m.Gold.Number}:{m.Run.Id}").Order());
    }

    [Fact]
    public void Perception_marks_count_only_marked_lines()
    {
        const string review = "## x\n\n- [x] #Q1 · Dr. Watson · doubts · F13\n- [-] #Q2 · Dr. Watson · believes · F2\n- [ ] #Q3 · Holmes · curious · F5\n- [~] #Q4 · edited\n";
        Assert.Equal(new Dictionary<string, bool> { ["Q1"] = true, ["Q2"] = false }, GraphGold.PerceptionMarks(review));
        var none = new GraphScore(new(0, 0, 0), new(0, 0, 0), 0, 0, new Dictionary<string, int>(), 3, 0, 0);
        Assert.Null(none.PerceptionPrecision);   // nothing marked: not measured, never zero
        Assert.Null(none.HowAccuracy);
        Assert.Equal(0.5, (none with { PerceptionsMarked = 2, PerceptionsRight = 1 }).PerceptionPrecision);
    }

    [Fact]
    public void LitBank_coreference_agreement_is_over_the_pairs_of_person_mentions_both_sides_have()
    {
        LitBankCorefMention L(string chain, int start, string kind = "PROP", string category = "PER") => new(chain, kind, category, "x", start, start + 5);
        List<LitBankCorefMention> litBank =
        [
            L("Holmes-0", 10), L("Holmes-0", 20, "PRON"), L("Holmes-0", 30),
            L("Watson-1", 40), L("Watson-1", 50),
            L("visitor-2", 60, "NOM"),
            L("London-9", 70, category: "GPE"),        // not a person
            L("Watson-1", 500),                         // on none of our chains
        ];
        var ours = new Dictionary<int, List<(int, int)>>
        {
            [1] = [(10, 15), (20, 25)],                 // Holmes, with a pronoun from the BookNLP chain
            [2] = [(30, 35), (40, 45), (50, 55)],       // Watson, and wrongly one Holmes mention
            [3] = [(60, 65)],
        };

        var a = LitBankCoref.Compare(litBank, ours, new Dictionary<int, string> { [1] = "Holmes", [2] = "Watson", [3] = "Mortimer" });

        Assert.Equal((7, 6), (a.PersonMentions, a.OnOurChains));
        Assert.Equal(15, a.Pairs);                                  // 6 mentions → 15 pairs
        // LitBank: Holmes×3 (3 pairs) + Watson×2 (1 pair) the same. Ours: {10,20} and {30,40,50}: 1 + 3 pairs the same. In both: (10,20) and (40,50).
        Assert.Equal((2, 2, 2), (a.SameInBoth, a.SameInLitBankOnly, a.SameInOursOnly));
        Assert.Equal(11, a.Agree);
        Assert.Equal((0.5, 0.5), (a.LinkPrecision, a.LinkRecall));
        Assert.Equal([1, 1], a.ByKind["PRON"]);
        Assert.Contains(a.Disagreements, d => d.Contains("Holmes-0 is split between our Holmes and Watson"));
    }
}
