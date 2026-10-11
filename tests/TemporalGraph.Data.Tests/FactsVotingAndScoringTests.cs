using System.Text.Json;
using System.Text.Json.Nodes;
using TemporalGraph.Cli;
using TemporalGraph.Cli.Gold;
using TemporalGraph.Cli.Indexing;
using TemporalGraph.Cli.Runs;
using TemporalGraph.Cli.Scoring;

namespace TemporalGraph.Data.Tests;

// Stage 3 and Stage 4 voting, the narrowed event_order list, frozen inputs, the facts review filter, and the
// facts gold importer and scorer. Hand-made examples; no network.
public class FactsVotingAndScoringTests
{
    private const string Hound = "hound-of-the-baskervilles";
    private static readonly string Text = IndexingShared.BookText(Hound);
    private static readonly Stage0Result Stage0 = IndexingShared.Stage0For(Hound);
    private static ChunkInfo Chunk1 => Stage0.Chunks[0];
    private static string Chunk1Text => Text[Chunk1.CharStart..Chunk1.CharEnd];
    private const string Breakfast = "Mr. Sherlock Holmes, who was usually very late in the mornings";
    private const string Stick = "I stood upon the hearth-rug and picked up the stick which our visitor had left behind him the night before.";
    private static EvidenceMatch M(string quote) => EvidenceChecker.Find(quote, Chunk1Text, Chunk1.CharStart)!;
    private static EvidenceMatch Shift(EvidenceMatch m, int by) => m with { CharStart = m.CharStart + by, CharEnd = m.CharEnd + by };

    private static GraphEdge E(string type, int s, int t, EvidenceMatch m, string? kind = null) => new("", type, s, t, kind, 0, 1, "", m);
    private static GraphNode N(string label, string text, EvidenceMatch m, params (string Type, int Entity)[] links) =>
        new("", label, text, 0, 1, "", m, links.Select(l => new ExtractedLink(l.Type, l.Entity)).ToList());
    private static Stage3.CheckedChunk Sample(IEnumerable<GraphEdge> edges, IEnumerable<GraphNode> nodes) => new(edges.ToList(), nodes.ToList(), []);

    // ---- Stage 3 voting ---------------------------------------------------------------------------------

    [Fact]
    public void Stage3_vote_matches_by_type_endpoints_and_evidence_not_wording()
    {
        var stick = M(Stick);
        var later = Shift(stick, 10);       // overlaps the stick span
        var breakfast = M(Breakfast);       // does not
        var samples = new[]
        {
            Sample([E("RELATED_TO", 1, 2, stick, "friend"), E("MET", 1, 2, stick), E("MET", 1, 2, later)],   // the second MET repeats the first
                   [N("Fact", "Watson picks up the stick.", stick, ("ABOUT", 2), ("ABOUT", 1))]),
            Sample([E("RELATED_TO", 1, 2, later, "Friend"), E("MET", 1, 2, breakfast)],
                   [N("Fact", "The visitor's stick was left behind.", later, ("STATES", 2), ("ABOUT", 1)), N("Fact", "Other entities.", stick, ("ABOUT", 3))]),
            Sample([E("RELATED_TO", 1, 2, stick, "colleague")],
                   [N("Event", "Watson picks up the stick.", stick, ("PARTICIPATED_IN", 2))]),
        };

        var (edges, nodes, duplicates) = Stage3.Vote(samples);

        Assert.Equal(1, duplicates);
        var related = Assert.Single(edges, e => e.Type == "RELATED_TO");
        Assert.Equal(3, related.Votes);                   // kinds differ: still one item
        Assert.Equal("friend", related.Kind);             // the most common kind, case ignored
        Assert.Equal((stick.CharStart, stick.CharEnd), (related.EvidenceMatch.CharStart, related.EvidenceMatch.CharEnd)); // most common span
        Assert.Equal([1, 1], edges.Where(e => e.Type == "MET").Select(e => e.Votes)); // no overlap between the two METs: two items
        var fact = Assert.Single(nodes, n => n.Votes == 2);
        Assert.Equal("Watson picks up the stick.", fact.Text);   // wording differs, link types differ: the same entities {1, 2}
        Assert.Equal(3, nodes.Count);                     // + the Fact about entity 3, + the Event (another label)
    }

    [Fact]
    public void Stage3_pool_refilters_to_any_m_and_dedupes_the_overlap()
    {
        var stick = M(Stick);
        var pool = new List<GraphEdge> { E("MET", 1, 2, stick) with { Votes = 5 }, E("MET", 1, 2, stick) with { Votes = 3, Chunk = 1 }, E("LOCATED_AT", 1, 3, stick) with { Votes = 2 } };
        var nodes = new List<GraphNode> { N("Fact", "x", stick, ("ABOUT", 1)) with { Votes = 1 } };
        var r = new Stage3Result(Hound, 1, 1, 0.9, [new Stage3Chunk(0, 1, 0, 0, 0, 0, 0), new Stage3Chunk(1, 1, 0, 0, 0, 0, 0)], [], [], [], 0, 5, 3, pool, nodes);

        var m1 = Stage3.WithMinVotes(r, 1);
        Assert.Equal((2, 1, 1), (m1.Edges.Count, m1.Nodes.Count, m1.DuplicatesDropped)); // chunk 1's MET is the same item as chunk 0's
        Assert.Equal(["E1", "E2"], m1.Edges.Select(e => e.Id));
        var m3 = Stage3.WithMinVotes(r, 3);
        Assert.Equal((1, 0), (m3.Edges.Count, m3.Nodes.Count));
        Assert.Equal(3, m3.MinVotes);
        Assert.Empty(Stage3.WithMinVotes(r, 5).Nodes);
        Assert.Throws<ArgumentOutOfRangeException>(() => Stage3.WithMinVotes(r, 6));
        Assert.Throws<InvalidOperationException>(() => Stage3.WithMinVotes(r with { EdgePool = null }, 1));
        Assert.Equal(new Dictionary<string, int> { ["1"] = 1, ["2"] = 1, ["3"] = 1, ["4"] = 0, ["5"] = 1 }, Stage3.Counts(r)["poolByVotes"]);
    }

    // ---- Stage 4 voting, EDTF normalization, relations ----------------------------------------------------

    [Theory]
    [InlineData("1884-XX-XX~", "1884~")]
    [InlineData("1884-05-XX", "1884-05")]
    [InlineData("XXXX-05-XX", "XXXX-05")]
    [InlineData("1884-XX/1885-XX-XX?", "1884/1885?")]
    [InlineData("1884-05-04", "1884-05-04")]
    [InlineData("../1884-XX", "../1884")]
    [InlineData("not edtf", "not edtf")]
    public void Edtf_normalize_drops_trailing_unknown_parts(string text, string normalized) => Assert.Equal(normalized, Edtf.Normalize(text));

    [Fact]
    public void Event_order_relations_are_the_TLINK_names_about_time()
    {
        Assert.DoesNotContain("IDENTITY", TemporalRelations.Ordering);
        Assert.DoesNotContain("VAGUE", TemporalRelations.Ordering);
        Assert.All(TemporalRelations.Ordering, r => Assert.Contains(r, TemporalRelations.All));
        Assert.Equal(13, TemporalRelations.Ordering.Count);
        Assert.Equal(15, TemporalRelations.All.Count);   // the database CHECK list is unchanged
    }

    private static TimedItem T(string id, string? from = null, string? until = null, double? fabula = null) =>
        new(id, "node", 1, 0, from is null ? null : new StoryDate(from, null, null), until is null ? null : new StoryDate(until, null, null), from is null ? null : "q", null, fabula, true);

    [Fact]
    public void Stage4_vote_keeps_a_date_with_m_votes_else_null_and_unstable()
    {
        var o = new OrderRelation("A", "B", "BEFORE", 1, "", new EvidenceMatch("exact", 1, 0, 1));
        var samples = new List<Stage4.CheckedChapter>
        {
            new([T("A", "1884-XX-XX~"), T("B", "1884"), T("C", fabula: 1), T("D", "1890")], [o], []),
            new([T("A", "1884~"), T("B", "1885"), T("C", fabula: 3), T("D")], [o, o with { Relation = "AFTER" }], []),
            new([T("A"), T("B", "1886"), T("C", fabula: 2), T("D")], [], [new Stage4Reject(FailureTags.InvalidEdtf, "x", 1, "B", null, null, null)]),
        };

        var r = Stage4.Vote(samples, 2, 1);
        var items = r.Items.ToDictionary(i => i.Id);

        Assert.Equal(("1884~", 2), (items["A"].StoryFrom!.Edtf, items["A"].DateVotes));   // normalized, 2 of 3
        Assert.Equal("1884-01-01", items["A"].StoryFrom!.Lo);
        Assert.Null(items["A"].FabulaSeq);
        Assert.Null(items["B"].StoryFrom);                                                // 3 different years
        Assert.True(items["B"].UnstableDate);
        Assert.Equal(2.0, items["C"].FabulaSeq);                                          // median of 1, 3, 2
        Assert.False(items["C"].UnstableDate);
        Assert.Null(items["D"].StoryFrom);                                                // 1 vote for 1890, 2 for no date
        Assert.True(items["D"].UnstableDate);
        Assert.Equal(2, items["D"].DateVotes);
        Assert.Equal(["A BEFORE B ×2"], r.Order.Select(x => $"{x.A} {x.Relation} {x.B} ×{x.Votes}"));
        Assert.Equal(2, r.Rejected.Count(x => x.Tag == FailureTags.UnstableDate));
        Assert.Equal(2, r.Rejected.Single(x => x.Tag == FailureTags.InvalidEdtf).Sample);
        Assert.Contains("1884 ×1", r.Rejected.Single(x => x.Id == "B" && x.Tag == FailureTags.UnstableDate).Reason);

        Assert.Same(samples[0], Stage4.Vote(samples.Take(1).ToList(), 1, 1));             // one sample: unchanged
    }

    // ---- Facts gold: parser and importer ------------------------------------------------------------------

    private static string ReviewFile(params string[] lines)
    {
        var edges = lines.Where(l => l.StartsWith("E ")).Select(l => l[2..]);
        var nodes = lines.Where(l => l.StartsWith("N ")).Select(l => l[2..]);
        var order = lines.Where(l => l.StartsWith("O ")).Select(l => l[2..]);
        var missing = lines.Where(l => l.StartsWith("M ")).Select(l => l[2..]);
        return string.Join("\n", ["# t", "", "Header text.", "", FactsReviewFile.EdgesHeading, "", .. edges, "", FactsReviewFile.NodesHeading, "", .. nodes, "",
            FactsReviewFile.OrderHeading, "", .. order, "", FactsReviewFile.MissingHeading, "", "<!-- add here -->", .. missing, ""]);
    }

    private const string StickQuote = "I stood upon the hearth-rug and picked up the stick which our visitor had left behind him…";

    [Fact]
    public void Facts_review_parser_reads_every_line_type()
    {
        var content = ReviewFile(
            $"E - [x] #2 · MET · Sherlock Holmes → Dr. Watson · \"{StickQuote}\" · revealed: ch1 · story: — <!-- 3/3 runs -->",
            $"E - [~] #3 · RELATED_TO (friend) · Dr. Watson → Sherlock Holmes · \"{StickQuote}\" · revealed: ch1 · story: 1884~ until 1889 · reasoning: \"x\"",
            $"E - [-] #4 · LOCATED_AT · Dr. Watson → Baker Street · \"{StickQuote}\" · revealed: ch1 · story: —",
            $"N - [x] #5 · Fact · \"The visitor left a stick.\" · links: STATES ← Dr. Watson, ABOUT → Dr. James Mortimer · \"{StickQuote}\" · revealed: ch1 · story: ? until 1884",
            $"N - [x] #6 · Event · \"Watson picks up the stick.\" · links: PARTICIPATED_IN ← Dr. Watson, LOCATED_AT → Baker Street · \"{StickQuote}\" · revealed: ch1 · story: XXXX-05-04",
            "O - [x] BEFORE · #2 → #5 · \"the night before\"",
            "O - [-] AFTER · #5 → #6 · \"the night before\"",
            $"M - [x] #new1 · MET · Dr. Watson → Dr. James Mortimer · \"{StickQuote}\" · revealed: ch1 · story: —",
            "M - [x] SIMULTANEOUS · #new1 → #6 · \"the night before\"");

        var p = FactsGold.Parse(content);

        Assert.Empty(p.Errors);
        Assert.Equal(["2", "3", "5", "6", "new1"], p.Items.Select(i => i.Number));
        Assert.Equal(["accepted", "edited", "accepted", "accepted", "added"], p.Items.Select(i => i.Status));
        Assert.Equal("4", Assert.Single(p.Rejected).Number);
        var related = p.Items[1];
        Assert.Equal(("RELATED_TO", "friend", "Dr. Watson", "Sherlock Holmes", "1884~", "1889"), (related.Type, related.RelationKind, related.Source, related.Target, related.StoryFrom, related.StoryUntil));
        var fact = p.Items[2];
        Assert.Equal(("node", "Fact", "The visitor left a stick.", null, "1884"), (fact.Kind, fact.Type, fact.Text, fact.StoryFrom, fact.StoryUntil));
        Assert.Equal(["STATES Dr. Watson", "ABOUT Dr. James Mortimer"], fact.Links.Select(l => $"{l.Type} {l.Entity}"));
        Assert.Equal(["Dr. Watson", "Dr. James Mortimer"], fact.EntityNames);
        Assert.Equal(["BEFORE 2 5", "SIMULTANEOUS new1 6"], p.Orders.Select(o => $"{o.Relation} {o.A} {o.B}"));
        Assert.Single(p.RejectedOrders);
    }

    [Theory]
    [InlineData("E - [ ] #2 · MET · A → B · \"q\" · revealed: ch1 · story: —", "not reviewed")]
    [InlineData("E - [x] #2 · KNOWS · A → B · \"q\" · revealed: ch1 · story: —", "the type must be")]
    [InlineData("E - [x] #2 · RELATED_TO · A → B · \"q\" · revealed: ch1 · story: —", "needs a kind")]
    [InlineData("E - [x] #2 · MET (friend) · A → B · \"q\" · revealed: ch1 · story: —", "only RELATED_TO")]
    [InlineData("E - [x] #2 · MET · A and B · \"q\" · revealed: ch1 · story: —", "Source → Target")]
    [InlineData("E - [x] #2 · MET · A → B · q · revealed: ch1 · story: —", "double quotes")]
    [InlineData("E - [x] #2 · MET · A → B · \"q\" · revealed: 1 · story: —", "revealed: chN")]
    [InlineData("E - [x] #2 · MET · A → B · \"q\" · revealed: ch1 · story: 17th century", "not valid EDTF")]
    [InlineData("E - [x] #2 · MET · A → B · \"q\" · revealed: ch1 · story: — · note: x", "unexpected field")]
    [InlineData("N - [x] #5 · Fact · \"t\" · links: STATES → A · \"q\" · revealed: ch1 · story: —", "written with ←")]
    [InlineData("N - [x] #5 · Fact · \"t\" · links: PARTICIPATED_IN ← A · \"q\" · revealed: ch1 · story: —", "not to a Fact")]
    [InlineData("N - [x] #5 · Event · \"t\" · links: A and B · \"q\" · revealed: ch1 · story: —", "can't read the links")]
    [InlineData("N - [x] #5 · MET · A → B · \"q\" · revealed: ch1 · story: —", "an edge line under Facts")]
    [InlineData("O - [x] IDENTITY · #2 → #5 · \"q\"", "relation must be one of")]
    [InlineData("O - [x] BEFORE · #2 → #9 · \"q\"", "#2 is not an accepted")]
    [InlineData("M - [~] #new1 · MET · A → B · \"q\" · revealed: ch1 · story: —", "must be marked [x]")]
    public void Facts_review_parser_refuses_what_it_cannot_read(string line, string problem)
    {
        var p = FactsGold.Parse(ReviewFile(line));
        Assert.Contains(p.Errors, e => e.Problem.Contains(problem));
    }

    [Fact]
    public void Facts_review_parser_refuses_a_comment_left_open_on_a_checklist_line()
    {
        // A truncated trailing comment ran on to the next line's "-->" and silently hid that line.
        var content = ReviewFile(
            "E - [x] #2 · MET · A → B · \"q\" · revealed: ch1 · story: — <!-- 2/3 runs · story: XXXX-05-04 ",
            "E - [x] #3 · MET · A → C · \"q\" · revealed: ch1 · story: — <!-- 3/3 runs -->");

        var p = FactsGold.Parse(content);

        var error = Assert.Single(p.Errors);
        Assert.Contains("not closed on it", error.Problem);
        Assert.Equal(content.Split('\n').ToList().FindIndex(l => l.Contains("#2 ")) + 1, error.Line);
        // A multi-line comment that doesn't start on a checklist line is still fine.
        Assert.Empty(FactsGold.Parse(ReviewFile("E <!-- a note\nover two lines -->", "E - [x] #3 · MET · A → C · \"q\" · revealed: ch1 · story: —")).Errors);
    }

    [Fact]
    public void Facts_gold_import_merges_a_supplement_review_and_records_both_hashes()
    {
        // --supplement unions a second reviewed file (#sN numbers) into the same facts gold.
        var dir = Path.Combine(Path.GetTempPath(), "TemporalGraph.Data.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var main = Path.Combine(dir, "ch01.facts.review.md");
            File.WriteAllText(main, ReviewFile($"E - [x] #2 · MET · Sherlock Holmes → Dr. Watson · \"{StickQuote}\" · revealed: ch1 · story: —"));
            var supplement = Path.Combine(dir, "ch01.facts.supplement.review.md");
            File.WriteAllText(supplement, ReviewFile(
                "N - [x] #s1 · Fact · \"Holmes is late.\" · links: ABOUT → Sherlock Holmes · \"Mr. Sherlock Holmes, who was usually very late in the mornings…\" · revealed: ch1 · story: — <!-- found by: gpt54mini 3/3 -->",
                $"E - [-] #s2 · LOCATED_AT · Dr. Watson → Baker Street · \"{StickQuote}\" · revealed: ch1 · story: — <!-- found by: gpt56luna 2/3 -->"));
            var outPath = Path.Combine(dir, "ch01.facts.json");

            var outcome = FactsGold.Import(Hound, main, outPath, supplement);

            Assert.Empty(outcome.Errors);
            var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(outPath))!;
            Assert.Equal(["2", "s1"], json["items"]!.AsArray().Select(i => i!["number"]!.GetValue<string>()));
            Assert.Equal("s2", json["rejected"]!.AsArray().Single()!["number"]!.GetValue<string>());
            Assert.Equal("ch01.facts.review.md", json["source"]!["file"]!.GetValue<string>());
            Assert.Equal("ch01.facts.supplement.review.md", json["supplement"]!["file"]!.GetValue<string>());
            Assert.Equal(64, json["supplement"]!["sha256"]!.GetValue<string>().Length);
            Assert.Equal(["2", "s1"], FactsGold.Load(outPath).Select(g => g.Number));

            // Without --supplement: no supplement block.
            Assert.Empty(FactsGold.Import(Hound, main, outPath).Errors);
            Assert.Null(System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(outPath))!["supplement"]);

            // An unmarked supplement line or a number used in both files is refused, naming the supplement file.
            File.WriteAllText(supplement, ReviewFile(
                "N - [ ] #s1 · Fact · \"Holmes is late.\" · links: ABOUT → Sherlock Holmes · \"Mr. Sherlock Holmes, who was usually very late in the mornings…\" · revealed: ch1 · story: —",
                $"E - [x] #2 · MET · Sherlock Holmes → Dr. Watson · \"{StickQuote}\" · revealed: ch1 · story: —"));
            var refused = FactsGold.Import(Hound, main, outPath, supplement);
            Assert.Contains(refused.Errors, e => e.Text.StartsWith("ch01.facts.supplement.review.md: ") && e.Problem.Contains("not reviewed"));
            Assert.Contains(refused.Errors, e => e.Text == "ch01.facts.supplement.review.md: #2" && e.Problem.Contains("main review"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Facts_gold_import_locates_each_evidence_quote_in_its_chapter()
    {
        var dir = Path.Combine(Path.GetTempPath(), "TemporalGraph.Data.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var review = Path.Combine(dir, "ch01.facts.review.md");
            File.WriteAllText(review, ReviewFile(
                $"E - [x] #2 · MET · Sherlock Holmes → Dr. Watson · \"{StickQuote}\" · revealed: ch1 · story: —",
                "N - [x] #5 · Fact · \"Holmes is late.\" · links: ABOUT → Sherlock Holmes · \"Mr. Sherlock Holmes, who was usually very late in the mornings…\" · revealed: ch1 · story: 1884"));
            var outcome = FactsGold.Import(Hound, review, Path.Combine(dir, "ch01.facts.json"));
            Assert.Empty(outcome.Errors);
            var gold = FactsGold.Load(Path.Combine(dir, "ch01.facts.json"));
            var stick = M(Stick);
            Assert.Equal(stick.CharStart, gold[0].EvidenceStart);
            Assert.True(gold[0].EvidenceEnd < stick.CharEnd);      // the cut quote is a prefix of the sentence
            Assert.Equal(M(Breakfast).CharStart, gold[1].EvidenceStart);
            Assert.Equal("1884", gold[1].StoryFrom);
            Assert.Equal(64, JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "ch01.facts.json")))!["source"]!["sha256"]!.GetValue<string>().Length);

            // A quote that isn't in its revealed chapter is refused, and nothing is written.
            File.WriteAllText(review, ReviewFile($"E - [x] #2 · MET · Sherlock Holmes → Dr. Watson · \"{StickQuote}\" · revealed: ch2 · story: —"));
            var bad = FactsGold.Import(Hound, review, Path.Combine(dir, "other.json"));
            Assert.Contains(bad.Errors, e => e.Problem.Contains("not found in chapter 2"));
            Assert.False(File.Exists(Path.Combine(dir, "other.json")));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void The_committed_facts_review_imports_once_every_line_is_marked()
    {
        var review = Path.Combine(RepoPaths.FindRoot(), "gold", Hound, "ch01-03.facts.review.md");
        var dir = Path.Combine(Path.GetTempPath(), "TemporalGraph.Data.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var marked = Path.Combine(dir, "ch01-03.facts.review.md");
            File.WriteAllText(marked, File.ReadAllText(review).Replace("- [ ] ", "- [x] "));
            var outcome = FactsGold.Import(Hound, marked, Path.Combine(dir, "ch01-03.facts.json"));
            Assert.True(outcome.Errors.Count == 0, string.Join(Environment.NewLine, outcome.Errors.Take(5).Select(e => $"{e.Line}: {e.Problem} {e.Text}")));
            Assert.NotEmpty(outcome.Parsed!.Items);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ---- Facts scorer -------------------------------------------------------------------------------------

    private static FactPrediction P(string id, string kind, string type, int[] entities, int start, int end, bool states = false, string? from = null, string? until = null,
        bool unstable = false, int chapter = 1) =>
        new(id, kind, type, kind == "edge" ? entities[0] : null, kind == "edge" ? entities[1] : null, entities.ToHashSet(), states, start, end, chapter, from, until, unstable);

    private static GoldFact G(string number, string kind, string type, string[] names, int start, int end, string? from = null, string? until = null, bool states = false, int chapter = 1) =>
        new(number, kind, type, null, kind == "edge" ? names[0] : null, kind == "edge" ? names[1] : null, kind == "node" ? "t" : null,
            kind == "node" ? names.Select((n, i) => new GoldLink(i == 0 && states ? "STATES" : "ABOUT", n)).ToList() : [], "q", chapter, from, until, "accepted", 0, start, end);

    private static readonly Func<string, IReadOnlySet<int>> Ids = name => name switch
    {
        "Holmes" or "Sherlock Holmes" => new HashSet<int> { 1 },
        "Watson" => new HashSet<int> { 2 },
        "Mortimer" => new HashSet<int> { 3 },
        _ => new HashSet<int>(),
    };

    [Fact]
    public void Facts_scorer_matches_by_type_endpoints_and_evidence_and_tags_the_rest()
    {
        var run = new List<FactPrediction>
        {
            P("E1", "edge", "MET", [2, 1], 100, 150),                                  // gold #1 (MET either way): match
            P("N1", "node", "Fact", [1, 2], 200, 260, from: "1884~"),                  // gold #2 (same entities, other evidence wording): match, date compatible
            P("E2", "edge", "RELATED_TO", [3, 1], 300, 340),                           // gold #3 is a claim: stated as fact
            P("E3", "edge", "LOCATED_AT", [1, 2], 400, 450),                           // gold #4 is MET with the same entities: wrong type
            P("E4", "edge", "MET", [1, 3], 500, 550),                                  // gold #5 is MET 1–2: wrong endpoint
            P("N2", "node", "Event", [2], 600, 650, from: "1889"),                     // gold #6 undated: match, invented date
            P("N3", "node", "Fact", [1], 700, 750, unstable: true),                    // gold #7 dated: match, unstable date
            P("N4", "node", "Fact", [3], 800, 850, chapter: 2),                        // gold #8 revealed ch1: match, revealed wrong
            P("E5", "edge", "MET", [1, 2], 2000, 2050),                                // nothing there: spurious
        };
        var gold = new List<GoldFact>
        {
            G("1", "edge", "MET", ["Sherlock Holmes", "Watson"], 120, 160),
            G("2", "node", "Fact", ["Watson", "Holmes"], 210, 230, from: "1884-03"),
            G("3", "node", "Fact", ["Mortimer", "Holmes"], 300, 330, states: true),
            G("4", "edge", "MET", ["Holmes", "Watson"], 410, 420),
            G("5", "edge", "MET", ["Holmes", "Watson"], 500, 510),
            G("6", "node", "Event", ["Watson"], 600, 610),
            G("7", "node", "Fact", ["Holmes"], 700, 710, from: "1884"),
            G("8", "node", "Fact", ["Mortimer"], 800, 810),
            G("9", "edge", "MET", ["Holmes", "Mortimer"], 3000, 3010),                 // missed
        };

        var r = FactsScorer.Score(run, gold, Ids);

        Assert.Equal(["1 E1", "2 N1", "6 N2", "7 N3", "8 N4"], r.Matches.Select(m => $"{m.GoldNumber} {m.RunId}").Order());
        Assert.Equal((5, 9, 9), (r.Overall.TruePositives, r.Overall.Predicted, r.Overall.Gold));
        Assert.Equal(0.5556, r.Overall.F1);
        Assert.Equal(1, r.Count(FailureTags.StatedAsFact));
        Assert.Equal(1, r.Count(FailureTags.WrongType));
        Assert.Equal(1, r.Count(FailureTags.WrongEndpoint));
        Assert.Equal(1, r.Count(FailureTags.InventedDate));
        Assert.Equal(1, r.Count(FailureTags.UnstableDate));
        Assert.Equal(0, r.Count(FailureTags.WrongDate));
        Assert.Equal(("9", "E5"), (r.Failures.Single(f => f.Tag == FailureTags.MissedFact).GoldNumber, r.Failures.Single(f => f.Tag == FailureTags.SpuriousFact).RunId));
        Assert.Equal(0.8, r.RevealedAccuracy);
        Assert.Equal((2, 1, 2), (r.Story("exact"), r.Story("compatible"), r.Story("wrong"))); // exact: #1, #8; compatible: #2; wrong: #6, #7
    }

    [Theory]
    [InlineData(null, null, null, null, "exact")]
    [InlineData("1884-XX-XX~", null, "1884~", null, "exact")]
    [InlineData("1884", null, "1884-05", null, "compatible")]
    [InlineData("1882", "1884", "1883", null, "compatible")]
    [InlineData("1884", null, "1889", null, "wrong")]
    [InlineData("1884", null, null, null, "wrong")]
    [InlineData(null, null, "1884", null, "wrong")]
    [InlineData("XXXX-05-04", null, "1884", null, "compatible")]   // no year: open bounds
    [InlineData("../1880", null, "1884", null, "wrong")]
    public void Story_agreement(string? runFrom, string? runUntil, string? goldFrom, string? goldUntil, string expected) =>
        Assert.Equal(expected, FactsScorer.StoryAgreement(runFrom, runUntil, goldFrom, goldUntil));

    // ---- Frozen inputs ------------------------------------------------------------------------------------

    [Fact]
    public void Frozen_copy_keeps_stages_1_to_N_and_refilters_stage_3()
    {
        var root = Path.Combine(Path.GetTempPath(), "TemporalGraph.Data.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var source = RunFolder.Create(root);
            File.WriteAllText(source.File(RunFolder.Stage1Json), "{\"s\":1}");
            File.WriteAllText(source.File(RunFolder.Stage2Json), "{\"s\":2}");
            var stick = M(Stick);
            var r = new Stage3Result(Hound, 1, 3, 0.9, [], [], [], [], 0, 5, 1, [E("MET", 1, 2, stick) with { Votes = 4 }, E("LOCATED_AT", 1, 3, stick) with { Votes = 2 }], []);
            IndexingShared.WriteWith(source, RunFolder.Stage3Json, r, new Dictionary<string, object?> { ["stage"] = "stage-3", ["skill"] = new { name = "extract-facts", version = 2 } });
            File.WriteAllText(source.File(RunFolder.Stage4Json), "{}");
            foreach (var st in new[] { "stage-1", "stage-3", "stage-4" }) source.AppendFailure(new { type = "quote mismatch", stage = st });
            source.RecordCommand(new { command = "x" }, new Dictionary<string, object?>
            {
                ["book"] = Hound, ["chapters"] = new { first = 1, last = 3 },
                ["skills"] = new Dictionary<string, object> { ["extract-mentions"] = 1, ["extract-facts"] = 2, ["assign-story-time"] = 3 },
                ["sampling"] = new Dictionary<string, object> { ["stage-1"] = 1, ["stage-4"] = 4 },
            });

            var copy = FrozenStages.Copy(source, root, 3, 3);

            Assert.Equal("{\"s\":2}", File.ReadAllText(copy.File(RunFolder.Stage2Json)));
            Assert.False(File.Exists(copy.File(RunFolder.Stage4Json)));
            var copied = IndexingShared.Read<Stage3Result>(copy, RunFolder.Stage3Json);
            Assert.Equal(["MET"], copied.Edges.Select(e => e.Type));                 // m = 3 drops the 2-vote item
            Assert.Equal(2, copied.EdgePool!.Count);
            Assert.Equal(["stage-1", "stage-3"], File.ReadAllLines(copy.File(RunFolder.FailuresJsonl)).Select(l => JsonNode.Parse(l)!["stage"]!.GetValue<string>()));
            var runJson = JsonNode.Parse(File.ReadAllText(copy.File(RunFolder.RunJson)))!;
            Assert.Equal(["extract-mentions", "extract-facts"], runJson["skills"]!.AsObject().Select(kv => kv.Key));
            Assert.Equal(["stage-1"], runJson["sampling"]!.AsObject().Select(kv => kv.Key));
            Assert.Equal(source.RunId, runJson["frozenFrom"]!["runId"]!.GetValue<string>());
            Assert.Equal(1, runJson["chapters"]!["first"]!.GetValue<int>());

            Assert.Throws<FileNotFoundException>(() => FrozenStages.Copy(RunFolder.Create(root), root, 2));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    // ---- Facts review filter ------------------------------------------------------------------------------

    [Fact]
    public void Facts_review_lists_only_items_found_by_min_runs_and_the_rest_separately()
    {
        var stick = M(Stick);
        var breakfast = M(Breakfast);
        Entity Ent(int id, string name) => new(id, name, MentionType.Character, [name], [new Mention(0, name, MentionType.Character, 0, 1, "", stick.CharStart, stick.CharStart + 1, "", new EvidenceMatch("", 0, 0, 0))], stick.CharStart, [id]);
        var stage2 = new Stage2Result(Hound, [], [], [], [Ent(1, "Sherlock Holmes"), Ent(2, "Dr. Watson")], [], new ResolveSettings());
        Stage3Result S3(params GraphEdge[] edges) => new(Hound, 1, 1, 0.9, [], edges.Select((e, i) => e with { Id = $"E{i + 1}" }).ToList(), [], [], 0);
        var sources = new List<FactsReviewSource>
        {
            new("a", stage2, S3(E("MET", 1, 2, stick), E("LOCATED_AT", 1, 2, breakfast)), null),
            new("b", stage2, S3(E("MET", 1, 2, Shift(stick, 5))), null),
            new("c", stage2, S3(), null),
        };

        var main = FactsReviewFile.Generate("T", Hound, "1", sources, Text, IndexingShared.ChapterOf(Stage0), minRuns: 2);
        var rest = FactsReviewFile.Generate("T", Hound, "1", sources, Text, IndexingShared.ChapterOf(Stage0), minRuns: 2, rest: true);

        Assert.Contains("Only items found by at least 2 of the 3 runs are listed** (1 of 2", main);
        Assert.Equal(new Dictionary<string, int> { ["edge MET"] = 1 }, FactsReviewFile.LineCounts(main));
        Assert.Equal(new Dictionary<string, int> { ["edge LOCATED_AT"] = 1 }, FactsReviewFile.LineCounts(rest));
        Assert.Contains("<!-- 2/3 runs -->", main);
    }
}
