using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using TemporalGraph.Cli;
using TemporalGraph.Cli.Gold;
using TemporalGraph.Cli.Indexing;
using TemporalGraph.Cli.Runs;
using TemporalGraph.Cli.Scoring;

namespace TemporalGraph.Data.Tests;

// Stage 3 (facts and edges) and Stage 4 (time) checks, the scratch book database and the facts review draft.
// No network: the LLM is a stub.
public class FactsAndTimeTests
{
    private const string Hound = "hound-of-the-baskervilles";
    private static readonly string Text = IndexingShared.BookText(Hound);
    // These stages are the ones of before the one-pass pipeline: 1,000-token windows with an overlap.
    private static readonly Stage0Result Stage0 = TemporalGraph.Cli.Indexing.Stage0.Prepare(BookProfiles.For(Hound), SourceTexts.Read(Hound), SourceTexts.Sha256(Hound), ChunkSettings.Windows);
    private static ChunkInfo Chunk1 => Stage0.Chunks[0];
    private static string Chunk1Text => Text[Chunk1.CharStart..Chunk1.CharEnd];

    // Both sentences are in chunk 1 (chapter 1).
    private const string Breakfast = "Mr. Sherlock Holmes, who was usually very late in the mornings";
    private const string Stick = "I stood upon the hearth-rug and picked up the stick which our visitor had left behind him the night before.";

    private static readonly IReadOnlyList<EntityRef> Entities =
    [
        new(1, "Sherlock Holmes", MentionType.Character, ["Holmes"], []),
        new(2, "Dr. Watson", MentionType.Character, [], []),
        new(3, "Baker Street", MentionType.Location, [], []),
    ];

    private static Stage3Chunk_ Check(params object[] items)
    {
        var edges = items.OfType<ExtractedEdge>().ToList();
        var nodes = items.OfType<ExtractedNode>().ToList();
        var r = Stage3.Check(new FactExtraction(edges, nodes), Chunk1, Chunk1Text, Entities, 0.9);
        return new Stage3Chunk_(r.Edges, r.Nodes, r.Rejected);
    }

    private sealed record Stage3Chunk_(List<GraphEdge> Edges, List<GraphNode> Nodes, List<Stage3Reject> Rejected);

    // ---- Stage 3 ------------------------------------------------------------------------------------

    [Fact]
    public void Stage3_accepts_ontology_edges_with_evidence_and_stores_symmetric_ends_lower_id_first()
    {
        var r = Check(
            new ExtractedEdge("MET", 2, 1, null, Stick),
            new ExtractedEdge("RELATED_TO", 2, 1, " friend ", Stick),
            new ExtractedEdge("LOCATED_AT", 1, 3, null, Breakfast));

        Assert.Empty(r.Rejected);
        Assert.Equal(3, r.Edges.Count);
        Assert.Equal((1, 2), (r.Edges[0].Source, r.Edges[0].Target));       // MET is symmetric
        Assert.Equal((2, 1, "friend"), (r.Edges[1].Source, r.Edges[1].Target, r.Edges[1].Kind)); // RELATED_TO keeps its direction
        Assert.Null(r.Edges[0].Kind);
        Assert.Equal("exact", r.Edges[2].EvidenceMatch.Method);
        Assert.StartsWith("Mr. Sherlock Holmes", Text[r.Edges[2].EvidenceMatch.CharStart..]);
    }

    [Fact]
    public void Stage3_rejects_types_outside_the_ontology_bad_endpoints_and_quote_mismatches()
    {
        var r = Check(
            new ExtractedEdge("FRIEND_OF", 1, 2, null, Stick),
            new ExtractedEdge("KNOWS", 1, 2, null, Stick),
            new ExtractedEdge("STATES", 1, 2, null, Stick),
            new ExtractedEdge("RELATED_TO", 1, 2, null, Stick),
            new ExtractedEdge("MET", 1, 3, null, Stick),
            new ExtractedEdge("MET", 1, 99, null, Stick),
            new ExtractedEdge("MET", 1, 1, null, Stick),
            new ExtractedEdge("LOCATED_AT", 1, 2, null, Stick),
            new ExtractedEdge("IDENTICAL_TO", 1, 3, null, Stick),
            new ExtractedEdge("MET", 1, 2, null, "Holmes and Watson walked down to the river together that evening."),
            new ExtractedNode("Theme", "Detection.", Stick, [new("ABOUT", 1)]),
            new ExtractedNode("Character", "Holmes.", Stick, [new("ABOUT", 1)]));

        Assert.Empty(r.Edges);
        Assert.Empty(r.Nodes);
        string[] expected =
        [
            FailureTags.TypeOutsideOntology, FailureTags.TypeOutsideOntology, FailureTags.TypeOutsideOntology, FailureTags.TypeOutsideOntology,
            FailureTags.BadEndpoint, FailureTags.BadEndpoint, FailureTags.BadEndpoint, FailureTags.BadEndpoint, FailureTags.BadEndpoint,
            FailureTags.QuoteMismatch, FailureTags.TypeOutsideOntology, FailureTags.TypeOutsideOntology,
        ];
        Assert.Equal(expected, r.Rejected.Select(x => x.Tag));
        Assert.Contains("Stage 5", r.Rejected[1].Reason);
        Assert.Contains("needs a kind", r.Rejected[3].Reason);
        Assert.NotNull(r.Rejected[9].BestMatch);
    }

    [Fact]
    public void Stage3_claims_link_the_speaker_with_STATES_and_a_node_keeps_only_its_valid_links()
    {
        var r = Check(
            new ExtractedNode("Fact", " The visitor left his stick. ", Stick, [new("STATES", 2), new("ABOUT", 1), new("ABOUT", 1)]),
            new ExtractedNode("Event", "Watson picks up the stick.", Stick, [new("PARTICIPATED_IN", 2), new("STATES", 2), new("LOCATED_AT", 1)]),
            new ExtractedNode("Fact", "Baker Street says so.", Stick, [new("STATES", 3), new("PARTICIPATED_IN", 1)]),
            new ExtractedNode("Fact", "No links.", Stick, []));

        Assert.Equal(2, r.Nodes.Count);
        Assert.Equal("The visitor left his stick.", r.Nodes[0].Text);
        Assert.Equal(["STATES:2", "ABOUT:1"], r.Nodes[0].Links.Select(l => $"{l.Type}:{l.Entity}"));  // duplicates dropped
        Assert.Equal(["PARTICIPATED_IN:2"], r.Nodes[1].Links.Select(l => $"{l.Type}:{l.Entity}"));
        // Event: STATES and LOCATED_AT (to a character) rejected; Fact 3: both links and the node; Fact 4: the node.
        Assert.Equal(["link", "link", "link", "link", "node", "node"], r.Rejected.Select(x => x.Item));
        Assert.All(r.Rejected, x => Assert.Equal(FailureTags.BadEndpoint, x.Tag));
    }

    [Fact]
    public void Stage3_dedupes_repeats_from_the_next_chunk_only_and_numbers_items_in_book_order()
    {
        var evidence = EvidenceChecker.Find(Stick, Chunk1Text, Chunk1.CharStart)!;
        var later = evidence with { CharStart = evidence.CharStart + 5000, CharEnd = evidence.CharEnd + 5000 };
        GraphEdge E(int chunk, EvidenceMatch m, string type = "MET") => new("", type, 1, 2, null, chunk, 1, Stick, m);
        GraphNode N(int chunk, EvidenceMatch m) => new("", "Fact", "x", chunk, 1, Stick, m, [new("ABOUT", 1)]);

        var (edges, nodes, dropped) = Stage3.Dedupe(
            [E(1, later), E(1, evidence), E(2, evidence), E(1, evidence, "LOCATED_AT"), E(1, evidence)],
            [N(1, evidence), N(2, evidence), N(2, later)]);

        // Dropped: chunk 2's MET and chunk 2's first node (same items in the overlap). Same-chunk repeats are kept.
        Assert.Equal(2, dropped);
        Assert.Equal(["E1", "E2", "E3", "E4"], edges.Select(e => e.Id));
        Assert.Equal(evidence.CharStart, edges[0].EvidenceMatch.CharStart);
        Assert.Equal(later.CharStart, edges[^1].EvidenceMatch.CharStart);
        Assert.Equal(["N1", "N2"], nodes.Select(n => n.Id));
    }

    [Fact]
    public async Task Stage3_run_writes_rejects_to_failures_and_counts_edges_by_type()
    {
        using var temp = new TempFolder();
        var run = RunFolder.Create(temp.Path);
        var skill = OldSkills.Load(SkillFile.ExtractFacts);
        var prompts = new List<string>();
        var client = new StubChatClient(prompt =>
        {
            prompts.Add(prompt);
            return JsonSerializer.Serialize(new FactExtraction(
                [new ExtractedEdge("MET", 1, 2, null, Stick), new ExtractedEdge("KNOWS", 1, 2, null, Stick)],
                [new ExtractedNode("Fact", "Watson picks up the stick.", Stick, [new("ABOUT", 2)])]), Stage1.Json);
        });
        var entities = new List<EntityRef>(Entities) { [1] = new(2, "Dr. Watson", MentionType.Character, [], [(Chunk1.CharStart + 10, Chunk1.CharStart + 16, "Watson")]) };

        var r = await Stage3.RunAsync(client, null, skill, Stage0, Text, entities, 1, 1, 0.9, run, CancellationToken.None);

        var chunks = Stage0.Chunks.Count(c => c.Chapter == 1);
        Assert.Equal(chunks, prompts.Count);
        Assert.Contains("2 · Character · Dr. Watson · named here: Watson", prompts[0]);
        Assert.DoesNotContain("named here: ", prompts[^1]);
        // The quote is only in chunk 1 (and maybe its overlap with chunk 2, which the dedupe drops).
        Assert.Single(r.Edges);
        Assert.Single(r.Nodes);
        var counts = Stage3.Counts(r);
        Assert.Equal(1, ((Dictionary<string, int>)counts["edgesByType"])["MET"]);
        Assert.Equal(chunks, ((Dictionary<string, int>)counts["rejectedByReason"])[FailureTags.TypeOutsideOntology]);
        var failures = File.ReadAllLines(run.File(RunFolder.FailuresJsonl)).Select(l => JsonNode.Parse(l)!).ToList();
        Assert.All(failures, f => Assert.Equal("stage-3", f["stage"]!.GetValue<string>()));
        Assert.Equal(r.Rejected.Count, failures.Count);
        Assert.Contains(failures, f => f["type"]!.GetValue<string>() == FailureTags.QuoteMismatch);
    }

    // ---- Stage 4 ------------------------------------------------------------------------------------

    private static readonly ChapterInfo Ch1 = Stage0.Chapters.Single(c => c.Number == 1);
    private static string Ch1Text => Text[Ch1.CharStart..Ch1.CharEnd];
    private const string Dated = "“To James Mortimer, M.R.C.S., from his friends of the C.C.H.,” was engraved upon it, with the date “1884.”";

    [Fact]
    public void Stage4_validates_EDTF_reasoning_quotes_fabula_and_orderings()
    {
        (string, string, int)[] targets = [("E1", "edge", 100), ("N1", "node", 200), ("N2", "node", 300), ("N3", "node", 400), ("N4", "node", 500), ("N5", "node", 600), ("N6", "node", 700), ("N7", "node", 800)];
        var output = new TimeAssignment(
        [
            new("E1", "1884", null, Dated, 5),                       // dated: fabula dropped
            new("N1", "1884-13", null, Dated, 3),                    // invalid EDTF → null, fabula kept
            new("N2", "1884", null, "A quote that is not in the text at all, really.", 2), // reasoning mismatch → dates dropped
            new("N3", null, null, null, 1),
            new("N4", "1884", "1880", Dated, null),                  // until before from → until dropped
            new("N6", "1884-05-04", null, Stick, null),              // the quote doesn't give the year → dropped
            new("N7", "1879~/1884", null, Dated, null),              // worked out (~) at one end, quoted at the other: kept
            new("X9", "1884", null, Dated, null),                    // unknown id
            new("N1", "1885", null, Dated, null),                    // second entry for N1: ignored
        ],
        [
            new("E1", "N1", "BEFORE", Stick),
            new("E1", "N1", "BEFORE", Stick),                        // the same relation twice: kept once
            new("E1", "N1", "PRECEDES", Stick),
            new("E1", "E1", "BEFORE", Stick),
            new("N1", "Z", "AFTER", Stick),
            new("N2", "N3", "AFTER", "Holmes and Watson walked down to the river together that evening."),
            new("N3", "N4", "SIMULTANEOUS", Stick),
            new("N3", "N4", "VAGUE", Stick),                         // Not an ordering about time
            new("N2", "N3", "IDENTITY", Stick),                      // A repeat, not an ordering
        ]);

        var r = Stage4.Check(output, 1, targets, Ch1Text, Ch1.CharStart, 0.9);
        var items = r.Items.ToDictionary(i => i.Id);

        Assert.Equal(8, r.Items.Count);
        Assert.Equal(("1884", "1884-01-01", "1884-12-31"), (items["E1"].StoryFrom!.Edtf, items["E1"].StoryFrom!.Lo, items["E1"].StoryFrom!.Hi));
        Assert.Null(items["E1"].FabulaSeq);
        Assert.NotNull(items["E1"].ReasoningMatch);
        Assert.Equal(100, items["E1"].RevealedPos);
        Assert.Null(items["N1"].StoryFrom);
        Assert.Equal(3, items["N1"].FabulaSeq);
        Assert.Null(items["N2"].StoryFrom);
        Assert.Null(items["N2"].Reasoning);
        Assert.Equal(2, items["N2"].FabulaSeq);
        Assert.Equal(1, items["N3"].FabulaSeq);
        Assert.Equal("1884", items["N4"].StoryFrom!.Edtf);
        Assert.Null(items["N4"].StoryUntil);
        Assert.Null(items["N6"].StoryFrom);
        Assert.Equal(("1879~/1884", "1879-01-01", "1884-12-31"), (items["N7"].StoryFrom!.Edtf, items["N7"].StoryFrom!.Lo, items["N7"].StoryFrom!.Hi));
        Assert.False(items["N5"].Returned);
        Assert.Null(items["N5"].StoryFrom);

        Assert.Equal(["E1 BEFORE N1", "N3 SIMULTANEOUS N4"], r.Order.Select(o => $"{o.A} {o.Relation} {o.B}"));
        var tags = r.Rejected.GroupBy(x => x.Tag).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(2, tags[FailureTags.InvalidEdtf]);      // 1884-13, until before from
        Assert.Equal(3, tags[FailureTags.QuoteMismatch]);    // N2's reasoning, N6's unquoted year, N2/N3's ordering evidence
        Assert.Contains(r.Rejected, x => x.Id == "N6" && x.Reason.Contains("year 1884"));
        Assert.Equal(3, tags[FailureTags.BadEndpoint]);      // X9, E1/E1, N1/Z
        Assert.Equal(3, tags[FailureTags.TypeOutsideOntology]); // PRECEDES, VAGUE, IDENTITY
        Assert.Contains(r.Rejected, x => x.Order?.Relation == "IDENTITY" && x.Reason.Contains("same event"));
    }

    [Fact]
    public async Task Stage4_sends_one_call_per_chapter_with_the_text_so_far_and_sets_revealed_pos_by_code()
    {
        var evidence1 = EvidenceChecker.Find(Stick, Chunk1Text, Chunk1.CharStart)!;
        var ch2 = Stage0.Chapters.Single(c => c.Number == 2);
        var ch2Chunk = Stage0.Chunks.First(c => c.Chapter == 2);
        var ch2Quote = string.Join(' ', Text[(ch2Chunk.CharStart + 200)..(ch2Chunk.CharStart + 260)].Split((char[])[' ', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
        var evidence2 = EvidenceChecker.Find(ch2Quote, Text[ch2Chunk.CharStart..ch2Chunk.CharEnd], ch2Chunk.CharStart)!;
        var stage3 = new Stage3Result(Hound, 1, 3, 0.9, [],
            [new GraphEdge("E1", "MET", 1, 2, null, Chunk1.Index, 1, Stick, evidence1)],
            [new GraphNode("N1", "Fact", "x", ch2Chunk.Index, 2, ch2Quote, evidence2, [new("ABOUT", 1)])], [], 0);
        var prompts = new List<string>();
        var client = new StubChatClient(prompt =>
        {
            prompts.Add(prompt);
            var id = prompt.Contains("- E1 ") ? "E1" : "N1";
            return JsonSerializer.Serialize(new TimeAssignment([new(id, id == "E1" ? "1884~" : null, null, id == "E1" ? Dated : null, id == "E1" ? null : 1)], []), Stage1.Json);
        });
        var skill = SkillFile.Load(RepoPaths.FindRoot(), SkillFile.AssignStoryTime);

        var r = await Stage4.RunAsync(client, null, skill, Stage0, Text, stage3, Entities, 0.9, null, CancellationToken.None);

        Assert.Equal(2, prompts.Count);                          // chapter 3 has no items: no call
        Assert.Contains("chapters 1–1", prompts[0]);
        Assert.Contains("chapters 1–2", prompts[1]);
        Assert.Contains("- E1 · MET · Sherlock Holmes → Dr. Watson", prompts[0]);
        Assert.Contains("- N1 · Fact · \"x\" · ABOUT → Sherlock Holmes", prompts[1]);
        Assert.Contains(string.Join(' ', Text[ch2.CharStart..(ch2.CharStart + 40)].Split((char[])[' ', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)), string.Join(' ', prompts[1].Split((char[])[' ', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)));
        Assert.Equal([evidence1.CharStart, evidence2.CharStart], r.Items.Select(i => i.RevealedPos));
        Assert.Equal("1884~", r.Items[0].StoryFrom!.Edtf);
        Assert.Equal(1, r.Items[1].FabulaSeq);
        Assert.Equal(1, (int)Stage4.Counts(r)["dated"]);
        Assert.Equal(1, (int)Stage4.Counts(r)["null"]);
    }

    [Theory]
    [InlineData("1884", "with the date “1884.”", null)]
    [InlineData("1884-05-04", "On the fourth of May", "1884")]
    [InlineData("1884-05-04~", "On the fourth of May", null)]
    [InlineData("1879%", "five years ago", null)]
    [InlineData("1879?", "five years ago", "1879")]
    [InlineData("1882/1884", "from 1882 to 1884", null)]
    [InlineData("1882/1885", "from 1882 to 1884", "1885")]
    [InlineData("XXXX-05-04", "On the fourth of May", null)]
    [InlineData("188X", "some years", null)]
    [InlineData("../1884", "until 1884", null)]
    public void Stage4_a_full_year_must_be_in_the_quote_unless_marked_approximate(string edtf, string quote, string? unquoted) =>
        Assert.Equal(unquoted, Stage4.UnquotedYear(edtf, quote));

    [Fact]
    public async Task Stage4_drops_an_unreadable_sample_and_the_others_vote()
    {
        using var temp = new TempFolder();
        var run = RunFolder.Create(temp.Path);
        var evidence1 = EvidenceChecker.Find(Stick, Chunk1Text, Chunk1.CharStart)!;
        var stage3 = new Stage3Result(Hound, 1, 1, 0.9, [], [new GraphEdge("E1", "MET", 1, 2, null, Chunk1.Index, 1, Stick, evidence1)], [], [], 0);
        var calls = 0;
        var client = new StubChatClient(_ => Interlocked.Increment(ref calls) == 1
            ? "{\"items\": [{\"id\": \"E1\", \"storyFrom\": \"18"   // cut off, as by a content filter
            : JsonSerializer.Serialize(new TimeAssignment([new("E1", "1884", null, Dated, null)], []), Stage1.Json));
        var skill = SkillFile.Load(RepoPaths.FindRoot(), SkillFile.AssignStoryTime);

        var r = await Stage4.RunAsync(client, null, skill, Stage0, Text, stage3, Entities, 0.9, run, CancellationToken.None, new VotingSettings(3, 2));

        Assert.Equal(3, calls);
        Assert.Equal(("1884", 2), (r.Items.Single().StoryFrom!.Edtf, r.Items.Single().DateVotes));
        Assert.Equal(1, r.UnreadableReplies);
        Assert.Contains(File.ReadAllLines(run.File(RunFolder.FailuresJsonl)), l => l.Contains("\"unreadable reply\""));
    }

    // ---- Scratch database and facts review -------------------------------------------------------------

    private static Entity Ent(int id, string name, MentionType type, int start, params string[] aliases) =>
        new(id, name, type, [name, .. aliases], [new Mention(0, name, type, 0, 1, "", start, start + name.Length, "", new EvidenceMatch("", 0, 0, 0))], start, [id]);

    private static Stage2Result Stage2(params Entity[] entities) => new(Hound, [], [], [], entities, [], new ResolveSettings());

    private static (Stage2Result, Stage3Result, Stage4Result) Graph()
    {
        var m1 = EvidenceChecker.Find(Stick, Chunk1Text, Chunk1.CharStart)!;
        var m2 = EvidenceChecker.Find(Breakfast, Chunk1Text, Chunk1.CharStart)!;
        var stage2 = Stage2(Ent(1, "Sherlock Holmes", MentionType.Character, m2.CharStart + 4, "Holmes"), Ent(2, "Dr. Watson", MentionType.Character, m1.CharStart),
            Ent(3, "Baker Street", MentionType.Location, m1.CharStart + 5));
        var stage3 = new Stage3Result(Hound, 1, 1, 0.9, [],
            [new GraphEdge("E1", "MET", 1, 2, null, 0, 1, Stick, m1), new GraphEdge("E2", "RELATED_TO", 2, 1, "friend", 0, 1, Breakfast, m2)],
            [
                new GraphNode("N1", "Fact", "Watson says the visitor left a stick.", 0, 1, Stick, m1, [new("STATES", 2), new("ABOUT", 1)]),
                new GraphNode("N2", "Event", "Watson picks up the stick.", 0, 1, Stick, m1, [new("PARTICIPATED_IN", 2), new("LOCATED_AT", 3)]),
            ], [], 0);
        var stage4 = new Stage4Result(Hound, 0.9, [],
        [
            new TimedItem("E1", "edge", 1, m1.CharStart, new StoryDate("1884~", "1884-01-01", "1884-12-31"), null, Dated, m2, null, true),
            new TimedItem("E2", "edge", 1, m2.CharStart, null, null, null, null, 2, true),
            new TimedItem("N1", "node", 1, m1.CharStart, new StoryDate("1884/1886", "1884-01-01", "1886-12-31"), new StoryDate("1887", "1887-01-01", "1887-12-31"), Dated, m2, null, true),
            new TimedItem("N2", "node", 1, m1.CharStart, null, null, null, null, 1, true),
        ],
        [new OrderRelation("N2", "E2", "AFTER", 1, Stick, m1), new OrderRelation("N1", "E1", "BEFORE", 1, Stick, m1)], []);
        return (stage2, stage3, stage4);
    }

    [Fact]
    public void Build_db_writes_entities_edges_nodes_time_and_event_order()
    {
        using var temp = new TempFolder();
        var path = Path.Combine(temp.Path, "book.sqlite");
        var (stage2, stage3, stage4) = Graph();

        var counts = ScratchDb.Build(path, Stage0, stage2, stage3, stage4);

        Assert.Equal(Stage0.Chapters.Count, counts["chapters"]);
        Assert.Equal(Stage0.Chapters.Sum(c => c.Paragraphs.Count), counts["positions"]);
        Assert.Equal(5, counts["nodes"]);              // 3 entities + 2 Fact/Event
        Assert.Equal(5, counts["node_labels"]);
        Assert.Equal(6, counts["edges"]);              // 2 entity edges + 4 links
        Assert.Equal(2, counts["event_order"]);
        Assert.Equal((2, 1), (counts["characters"], counts["locations"]));

        using var db = BookDatabase.OpenReadWrite(path);
        var edges = db.Edges.Include(e => e.Source).ThenInclude(n => n.Labels).Include(e => e.Target).ThenInclude(n => n.Labels).ToList();
        var met = edges.Single(e => e.Type == "MET");
        Assert.Equal(("1884~", "1884-01-01", "1884-12-31"), (met.StoryFromEdtf, met.StoryFromLo, met.StoryFromHi));
        Assert.Equal(met.EvidenceStart, met.RevealedPos);
        Assert.Null(met.RetractedPos);
        Assert.Equal(2.0, edges.Single(e => e.Type == "RELATED_TO").FabulaSeq);
        Assert.Contains("\"kind\":\"friend\"", edges.Single(e => e.Type == "RELATED_TO").Properties);

        var states = edges.Single(e => e.Type == "STATES");
        Assert.Equal("Character", states.Source.Labels.Single().Label);
        Assert.Equal("Fact", states.Target.Labels.Single().Label);
        Assert.Null(states.StoryFromEdtf);             // STATES doesn't carry the claim's time
        var about = edges.Single(e => e.Type == "ABOUT");
        Assert.Equal("Fact", about.Source.Labels.Single().Label);
        Assert.Equal(("1884/1886", "1886-12-31", "1887", "1887-12-31"), (about.StoryFromEdtf, about.StoryFromHi, about.StoryUntilEdtf, about.StoryUntilHi));
        Assert.Equal("Event", edges.Single(e => e.Type == "PARTICIPATED_IN").Target.Labels.Single().Label);
        Assert.Equal(1.0, edges.Single(e => e.Type == "LOCATED_AT").FabulaSeq);

        var order = db.EventOrder.ToList();
        var byId = edges.ToDictionary(e => e.Id);
        // N2 stands for its first non-STATES link (PARTICIPATED_IN); N1 for ABOUT.
        Assert.Contains(order, o => o.Relation == "AFTER" && byId[o.AEdgeId].Type == "PARTICIPATED_IN" && byId[o.BEdgeId].Type == "RELATED_TO");
        Assert.Contains(order, o => o.Relation == "BEFORE" && byId[o.AEdgeId].Type == "ABOUT" && byId[o.BEdgeId].Type == "MET");
        var fact = db.Nodes.Include(n => n.Labels).AsEnumerable().Single(n => n.Labels.Any(l => l.Label == "Fact"));
        Assert.Contains("\"storyFromEdtf\":\"1884/1886\"", fact.Properties);
        Assert.Equal("Sherlock Holmes", db.Characters.Include(c => c.Node).OrderBy(c => c.Id).First().Name);
        Assert.NotNull(db.Characters.First().NodeId);
    }

    [Fact]
    public void Build_db_writes_each_aliases_first_mention_offset()
    {
        using var temp = new TempFolder();
        var path = Path.Combine(temp.Path, "book.sqlite");
        var (_, stage3, stage4) = Graph();
        Mention M(string text, int start) => new(0, text, MentionType.Character, 0, 1, "", start, start + text.Length, "", new EvidenceMatch("", 0, 0, 0));
        var holmes = new Entity(1, "Sherlock Holmes", MentionType.Character, ["Holmes", "Sherlock Holmes"],
            [M("Holmes", 40), M("Sherlock Holmes", 300), M("Holmes", 25)], 25, [1]);
        var stage2 = Stage2(holmes, Ent(2, "Dr. Watson", MentionType.Character, 10), Ent(3, "Baker Street", MentionType.Location, 50));

        ScratchDb.Build(path, Stage0, stage2, stage3, stage4);

        using var db = BookDatabase.OpenReadWrite(path);
        var node = db.Characters.Include(c => c.Node).AsEnumerable().Single(c => c.Name == "Sherlock Holmes").Node!;
        var props = System.Text.Json.JsonDocument.Parse(node.Properties).RootElement;
        Assert.Equal("Sherlock Holmes", props.GetProperty("name").GetString());
        Assert.Equal(["Holmes", "Sherlock Holmes"], props.GetProperty("aliases").EnumerateArray().Select(a => a.GetString()));
        Assert.Equal([("Holmes", 25), ("Sherlock Holmes", 300)],
            props.GetProperty("aliasFirstPos").EnumerateObject().Select(p => (p.Name, p.Value.GetInt32())));
        // The short name (the name used most) and its initials, on the node and in the characters table; locations have none.
        Assert.Equal("Holmes", props.GetProperty("shortName").GetString());
        var row = db.Characters.Single(c => c.Name == "Sherlock Holmes");
        Assert.Equal(("Holmes", "H"), (row.ShortName, row.Initials));
        var place = db.Locations.Include(l => l.Node).Single().Node!;
        Assert.False(System.Text.Json.JsonDocument.Parse(place.Properties).RootElement.TryGetProperty("shortName", out _));
    }

    [Fact]
    public void Short_name_is_the_name_used_most_that_can_stand_alone()
    {
        Mention M(string text, int start) => new(0, text, MentionType.Character, 0, 1, "", start, start + text.Length, "", new EvidenceMatch("", 0, 0, 0));
        Entity E(int id, string name, params (string Text, int Times)[] uses)
        {
            var at = 0;
            var mentions = uses.SelectMany(u => Enumerable.Range(0, u.Times).Select(_ => M(u.Text, at += 10))).ToList();
            return new Entity(id, name, MentionType.Character, uses.Select(u => u.Text).Distinct().ToList(), mentions, 0, [id]);
        }
        string Pick(Entity e, params Entity[] others) => ShortNames.Pick(e, ShortNames.Owners([e, .. others]));

        // Possessives, lower-case forms, all capitals and titles alone don't count, however often they occur.
        Assert.Equal("Richelieu", Pick(E(1, "Armand Jean Duplessis, Cardinal de Richelieu",
            ("the cardinal", 30), ("cardinal", 20), ("Monseigneur", 15), ("Richelieu", 10), ("Richelieu’s", 12), ("RICHELIEU", 11))));
        Assert.Equal("Athos", Pick(E(2, "Monsieur Athos", ("Monsieur Athos", 3), ("Athos", 40))));
        // An alias another character also has is skipped ("Bonacieux" for husband and wife).
        Assert.Equal("Mme. Bonacieux", Pick(E(3, "Constance Bonacieux", ("Bonacieux", 9), ("Mme. Bonacieux", 5)), E(4, "Jacques Michel Bonacieux", ("Bonacieux", 20))));
        // Ties: the first mentioned. Nothing qualifies: the canonical name.
        Assert.Equal("Dr. Mortimer", Pick(E(5, "Dr. James Mortimer", ("Dr. Mortimer", 2), ("Mortimer", 2))));
        Assert.Equal("the queen", Pick(E(6, "the queen", ("the queen", 5), ("queen", 3))));
    }

    [Fact]
    public void Facts_review_is_the_union_of_runs_one_line_per_item()
    {
        var (stage2, stage3, stage4) = Graph();
        // Run B: the same graph with other ids and entity numbering, one edge fewer and another story time for E1.
        var stage2b = Stage2(stage2.Entities.Select(e => e with { Id = e.Id + 10 }).ToArray());
        var stage3b = stage3 with
        {
            Edges = [stage3.Edges[0] with { Id = "E7", Source = 12, Target = 11 }],
            Nodes = stage3.Nodes.Select(n => n with { Id = n.Id.Replace("N", "N9"), Links = n.Links.Select(l => l with { Entity = l.Entity + 10 }).ToList() }).ToList(),
        };
        var stage4b = stage4 with
        {
            Items = [stage4.Items[0] with { Id = "E7", StoryFrom = null, Reasoning = null, ReasoningMatch = null }],
            Order = [new OrderRelation("N92", "E7", "AFTER", 1, Stick, stage4.Order[0].EvidenceMatch)],
        };
        var sources = new List<FactsReviewSource> { new("runA", stage2, stage3, stage4), new("runB", stage2b, stage3b, stage4b) };

        var content = FactsReviewFile.Generate("The Hound of the Baskervilles", Hound, "1", sources, Text, IndexingShared.ChapterOf(Stage0));
        var lines = content.ReplaceLineEndings("\n").Split('\n');

        var met = Assert.Single(lines, l => l.Contains(" · MET · "));
        Assert.Matches(@"^- \[ \] #\d+ · MET · Sherlock Holmes → Dr. Watson · ""I stood upon the hearth-rug.*"" · revealed: ch1 · story: 1884~ · reasoning: "".*"" <!-- 2/2 runs · story: 1884~ 1/2, — 1/2 -->$", met);
        Assert.Contains(lines, l => l.Contains(" · RELATED_TO (friend) · Dr. Watson → Sherlock Holmes · ") && l.EndsWith("<!-- 1/2 runs -->"));
        Assert.Contains(lines, l => l.Contains(" · Fact · \"Watson says the visitor left a stick.\" · links: STATES ← Dr. Watson, ABOUT → Sherlock Holmes · ")
            && l.Contains("story: 1884/1886 until 1887") && l.Contains("<!-- 2/2 runs · story: 1884/1886 until 1887 1/2, — 1/2 -->"));
        Assert.Contains(lines, l => l.Contains(" · Event · ") && l.Contains("PARTICIPATED_IN ← Dr. Watson, LOCATED_AT → Baker Street"));
        Assert.Contains(lines, l => l.StartsWith("- [ ] AFTER · #") && l.EndsWith("<!-- 1/2 runs -->"));
        Assert.Contains(lines, l => l.StartsWith("- [ ] BEFORE · #"));

        var sizes = FactsReviewFile.LineCounts(content);
        Assert.Equal(1, sizes["edge MET"]);
        Assert.Equal(1, sizes["edge RELATED_TO"]);
        Assert.Equal((1, 1), (sizes["Fact"], sizes["Event"]));
        Assert.Equal((2, 1), (sizes["order AFTER"], sizes["order BEFORE"])); // run B orders N2 against MET, not RELATED_TO
        Assert.Contains(FactsReviewFile.MissingHeading, content);
    }

    private sealed class StubChatClient(Func<string, string> reply) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply(messages.Last(m => m.Role == ChatRole.User).Text))));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class TempFolder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "TemporalGraph.Data.Tests", Guid.NewGuid().ToString("N"));
        public TempFolder() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
