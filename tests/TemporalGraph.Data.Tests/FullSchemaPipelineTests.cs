using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using TemporalGraph.Cli;
using TemporalGraph.Cli.Indexing;
using TemporalGraph.Cli.Runs;
using TemporalGraph.Cli.Scoring;
using TemporalGraph.Query;

namespace TemporalGraph.Data.Tests;

// Stages 2–5 on the full bi-temporal schema (Facts as nodes, DISCOVERED, PERCEIVES, referenced Events), the graph
// writer with both clocks on every element, the timeline and the leak tests. No network: the model is a stub.
public class FullSchemaPipelineTests
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
        new(2, "Dr. Watson", MentionType.Character, ["Watson"], []),
        new(3, "Baker Street", MentionType.Location, [], []),
        new(4, "Dr. Mortimer", MentionType.Character, [], []),
    ];

    private static Mention M(string text, int start, MentionType type = MentionType.Character, int chapter = 1) =>
        new(0, text, type, 0, chapter, "first", start, start + text.Length, "", new EvidenceMatch("exact", 1, start, start + text.Length), 5);

    // ---- Stage 0: the narrator column ---------------------------------------------------------------------------------

    [Fact]
    public void Stage0_reads_each_chapters_narrator_from_the_chapter_titles()
    {
        string? Narrator(int chapter) => Stage0.Chapters.Single(c => c.Number == chapter).NarratorColumn;
        Assert.Equal("Dr. Watson (letter)", Narrator(8));   // "First Report of Dr. Watson"
        Assert.Equal("Dr. Watson (diary)", Narrator(10));   // "Extract from the Diary of Dr. Watson"
        Assert.All(new[] { 1, 2, 3, 4, 5, 6, 7, 9, 11, 12, 13, 14, 15 }, c => Assert.Equal("Dr. Watson", Narrator(c)));
        // Titles that name no narrator leave it null.
        Assert.All(IndexingShared.Stage0For("three-musketeers").Chapters, c => Assert.Null(c.NarratorColumn));
    }

    // ---- Stage 2: a BookNLP backbone, one decision per pair, no transitive merging ------------------------------------

    [Fact]
    public void Stage2_flags_a_chain_that_mixes_a_womans_title_with_another_name_and_splits_it()
    {
        Assert.True(CorefStage2.Flagged(["Barrymore", "Mrs. Barrymore"]));
        Assert.True(CorefStage2.Flagged(["Miss Stapleton", "Mrs. Stapleton"]));
        Assert.False(CorefStage2.Flagged(["Stapleton", "Mr. Stapleton"]));
        Assert.False(CorefStage2.Flagged(["Holmes", "Mr. Holmes", "Sherlock Holmes"]));

        // BookNLP put "Barrymore" and "Mrs. Barrymore" in chain 131; "Holmes" in chain 94; nobody covers "Selden".
        List<ChainMention> chains =
        [
            new(131, "PER", "PROP", 100, 109, "Barrymore"), new(131, "PER", "PROP", 200, 214, "Mrs. Barrymore"), new(94, "PER", "PROP", 300, 306, "Holmes"),
            new(94, "PER", "PRON", 320, 322, "he"), new(228, "PER", "NOM", 400, 411, "the baronet"), new(228, "PER", "NOM", 500, 511, "the baronet"), new(228, "PER", "NOM", 600, 611, "the baronet"),
            new(229, "PER", "NOM", 700, 705, "a man"), new(229, "PER", "NOM", 710, 715, "a man"), new(229, "PER", "NOM", 720, 725, "a man"),
        ];
        var text = new string(' ', 400) + "the baronet" + new string(' ', 89) + "the baronet" + new string(' ', 89) + "the baronet" + new string(' ', 200);
        var (clusters, aligned, flagged) = CorefStage2.BuildClusters(
            [M("Barrymore", 100), M("Mrs. Barrymore", 200), M("Holmes", 300), M("Selden", 350), M("Baker Street", 360, MentionType.Location)],
            chains, text, _ => 1, new CorefSettings());

        Assert.Equal(3, aligned);
        Assert.Equal(1, flagged);
        Assert.Equal(["booknlp:131/other", "booknlp:131/mrs", "booknlp:94", "leftover", "stage-1", "booknlp-nominal:228"], clusters.Select(c => c.Source));
        // The noun-phrase chain is last and marked; the indefinite "a man" is no candidate. Pronouns are never mentions.
        Assert.True(clusters[^1].Nominal);
        Assert.Equal(["the baronet"], clusters[^1].Surfaces);
        Assert.DoesNotContain(clusters, c => c.Surfaces.Contains("he"));
    }

    [Fact]
    public void Stage2_asks_about_pairs_that_share_a_name_word_a_spelling_or_an_honorific()
    {
        var clusters = new List<Cluster>
        {
            new(1, MentionType.Character, "booknlp:1", [M("Sir Charles Baskerville", 10)]),
            new(2, MentionType.Character, "booknlp:2", [M("Sir Henry Baskerville", 50)]),
            new(3, MentionType.Character, "booknlp:3", [M("Mrs. Lyons", 90)]),
            new(4, MentionType.Character, "booknlp:4", [M("Mrs. Oldmore", 120)]),
            new(5, MentionType.Character, "leftover", [M("Vandeleurs", 150)]),
            new(6, MentionType.Character, "leftover", [M("Vandeleur", 180)]),
            new(7, MentionType.Location, "stage-1", [M("Baskerville Hall", 210, MentionType.Location)]),
            new(8, MentionType.Location, "stage-1", [M("Lafter Hall", 240, MentionType.Location)]),
            new(9, MentionType.Character, "booknlp-nominal:9", [M("the baronet", 270)], Nominal: true),
        };
        var pairs = CorefStage2.Candidates(clusters, new CorefSettings()).Select(p => $"{p.A}-{p.B}:{p.Why}").ToList();
        // A place and a person never pair; two Halls don't pair (neither name holds the other's words); noun phrases are asked separately.
        Assert.Equal(["1-2:name-word", "3-4:honorific", "5-6:spelling"], pairs);
    }

    [Fact]
    public async Task Stage2_merges_only_what_every_pair_was_voted_the_same_no_transitive_merging()
    {
        // Lafter Hall = "the Hall" and "the Hall" = Baskerville Hall were both voted the same; Lafter Hall vs Baskerville Hall was never
        // asked. Union-find would chain all three. Here the missing pair is asked, is different, and the second merge does not happen.
        var clusters = new List<Cluster>
        {
            new(1, MentionType.Location, "stage-1", [M("Lafter Hall", 10, MentionType.Location)]),
            new(2, MentionType.Location, "stage-1", [M("the Hall", 50, MentionType.Location)]),
            new(3, MentionType.Location, "stage-1", [M("Baskerville Hall", 90, MentionType.Location)]),
        };
        ClusterDecision D(int a, int b, string relation, int votes, string why = "name-word") =>
            new(new ClusterPair(a, b, why), clusters[a - 1].Surfaces[0], clusters[b - 1].Surfaces[0], relation, new Dictionary<string, int> { [relation] = votes }, null, null, null, "test");
        var decisions = new List<ClusterDecision> { D(1, 2, ClusterRelations.Same, 4), D(2, 3, ClusterRelations.Same, 5) };
        var asked = new List<ClusterPair>();

        var (groups, notes) = await CorefStage2.MergeAsync(clusters, decisions, pair =>
        {
            asked.Add(pair);
            return Task.FromResult(D(pair.A, pair.B, ClusterRelations.Different, 5, pair.Why));
        });

        Assert.Equal([(1, 3, "closure")], asked.Select(p => (p.A, p.B, p.Why)));
        // The pair with more votes merged first (2 with 3); Lafter Hall stays on its own.
        Assert.Equal(["1", "2,3"], groups.Select(g => string.Join(",", g)));
        Assert.Contains("no transitive merging", Assert.Single(notes));
        Assert.Equal(3, decisions.Count);   // the closure decision is recorded
    }

    [Fact]
    public void Stage2_vote_needs_m_votes_and_an_identity_reveal_needs_its_quote_in_the_passages()
    {
        var a = new Cluster(75, MentionType.Character, "booknlp:1", [M("Miss Stapleton", 10)]);
        var b = new Cluster(108, MentionType.Character, "booknlp:2", [M("Mrs. Stapleton", 50)]);
        var pair = new ClusterPair(75, 108, "name-word");
        var at = EvidenceChecker.Find(Stick, Chunk1Text, Chunk1.CharStart)!.CharStart;
        List<CorefStage2.Passage> shown = [new(1, at, at + 200, Text[at..(at + 200)])];
        ClusterAnswer Same() => new("same", [75, 108], "Mrs. Stapleton", null, "r");
        ClusterAnswer Reveal(string? quote) => new("identity-reveal", [], null, quote, "the text states it");
        ClusterAnswer Different() => new("different", [], null, null, "two people");

        Assert.Equal("same", CorefStage2.Vote(pair, a, b, [Same(), Same(), Same(), Same(), Different()], 4, shown, Text, 0.9).Relation);
        Assert.Equal("different", CorefStage2.Vote(pair, a, b, [Same(), Same(), Same(), Different(), Different()], 4, shown, Text, 0.9).Relation);
        // "same" without both chain ids is not a vote for same.
        Assert.Equal("different", CorefStage2.Vote(pair, a, b, Enumerable.Repeat(new ClusterAnswer("same", [75], null, null, "r"), 5).ToList(), 4, shown, Text, 0.9).Relation);
        var reveal = CorefStage2.Vote(pair, a, b, Enumerable.Repeat(Reveal("picked up the stick which our visitor had left behind him"), 4).ToList(), 4, shown, Text, 0.9);
        Assert.Equal("identity-reveal", reveal.Relation);
        Assert.NotNull(reveal.QuoteMatch);
        // An identity reveal with a quote that is not in the passages shown is not kept.
        Assert.Equal("different", CorefStage2.Vote(pair, a, b, Enumerable.Repeat(Reveal("They were the same woman all along."), 5).ToList(), 4, shown, Text, 0.9).Relation);
    }

    [Fact]
    public void Stage2_canonical_name_is_the_fullest_proper_name_of_the_group()
    {
        List<Mention> mentions = [M("Holmes", 1), M("Holmes", 2), M("Holmes", 3), M("Mr. Sherlock Holmes", 4), M("Sherlock Holmes", 5), M("Holmes’s", 6), M("the detective", 7) with { Pass = "booknlp" }];
        Assert.Equal("Mr. Sherlock Holmes", CorefStage2.CanonicalName(mentions, []));
        Assert.Equal("Sherlock Holmes", CorefStage2.CanonicalName(mentions, ["Sherlock Holmes", "Sherlock Holmes", "Someone Else"]));
    }

    // ---- Stage 3: the schema's shape directly -------------------------------------------------------------------------------

    private static ChunkGraph Sample(List<XEvent>? events = null, List<XFact>? facts = null, List<XPerception>? perceptions = null, List<XRetraction>? retractions = null,
        List<XIdentity>? identities = null, List<XRelation>? relations = null) => new(events ?? [], facts ?? [], perceptions ?? [], retractions ?? [], identities ?? [], relations ?? []);

    private static XEvent Narrated(string id = "e1", string evidence = Stick, params int[] participants) =>
        new(id, "narrated", "Dr. Watson picks up the stick the visitor left.", "the night before", (participants.Length == 0 ? [2] : participants).Select(p => new XParticipant(p, p == 2 ? "narrator" : "listener")).ToList(), 3, evidence, null);

    [Fact]
    public void Stage3_accepts_events_facts_with_their_discoveries_perceptions_and_relations()
    {
        var r = GraphStage3.Check(Sample(
            events: [Narrated(participants: [2, 1]), new XEvent("e2", "referenced", "A visitor called and left his stick.", "the night before", [new(4, "visitor")], null, Stick, "e1")],
            facts: [new XFact("f1", " Holmes is usually very late in the mornings. ", [1, 1], Breakfast, [new("e1", "recalled"), new("e2", "told")])],
            perceptions: [new XPerception(2, "f1", null, "believes", null, Breakfast), new XPerception(2, null, 1, "trusts", "e1", Stick)],
            retractions: [new XRetraction("f1", 2, "ignored: the fact's own text is used", [], Breakfast)],
            identities: [new XIdentity(4, 1, Stick)],
            relations: [new XRelation("RELATED_TO", 2, 1, " friend ", Stick), new XRelation("LOCATED_AT", 1, 3, null, Breakfast)]), Chunk1, Chunk1Text, Entities, 0.9);

        Assert.Empty(r.Rejected);
        Assert.Equal(["e1:narrated", "e2:referenced"], r.Events.Select(e => $"{e.Local}:{e.Kind}"));
        Assert.Equal("e1", r.Events[1].MentionedIn);
        Assert.Equal(3, r.Events[0].Location);
        var fact = Assert.Single(r.Facts);
        Assert.Equal(("Holmes is usually very late in the mornings.", "1"), (fact.Text, string.Join(",", fact.Subjects)));
        Assert.Equal(["e1:recalled", "e2:told"], fact.Discoveries.Select(d => $"{d.Event}:{d.How}"));
        // A perception of a fact with no cause takes the fact's discovering event.
        Assert.Equal(["believes:e1", "trusts:e1"], r.Perceptions.Select(p => $"{p.Kind}:{p.Cause}"));
        Assert.Equal((1, 4), (r.Identities[0].A, r.Identities[0].B));
        Assert.Equal("friend", r.Relations[0].Kind);
        Assert.Equal("Holmes is usually very late in the mornings.", Assert.Single(r.Retractions).Text);
    }

    [Fact]
    public void Stage3_rejects_values_outside_the_enumerations_bad_endpoints_and_quote_mismatches()
    {
        var r = GraphStage3.Check(Sample(
            events:
            [
                Narrated("e1"),
                new XEvent("e2", "flashback", "x", null, [new(2, null)], null, Stick, null),                       // kind outside narrated | referenced
                new XEvent("e3", "narrated", "Nobody is there.", null, [new(3, null)], null, Stick, null),          // a place is no participant → none left
                new XEvent("e4", "referenced", "Mentioned nowhere.", null, [], null, Stick, "e9"),                  // no narrating event
                new XEvent("e5", "narrated", "Invented.", null, [new(2, null)], null, "Holmes and Watson walked down to the river together that evening.", null),
            ],
            facts:
            [
                new XFact("f1", "A fact.", [1], Breakfast, [new("e1", "guessed")]),                                   // discovery mode outside the six
                new XFact("f2", "Another.", [99], Breakfast, [new("e1", "told")]),                                    // no known subject
                new XFact("f3", "Third.", [1], Breakfast, []),                                                        // no discovering event
                new XFact("f4", "Kept.", [1], Breakfast, [new("e1", "witnessed"), new("e5", "told")]),                // e5 was rejected: that discovery only
            ],
            perceptions:
            [
                new XPerception(2, "f4", null, "worried", null, Breakfast),                                           // not one of the twelve
                new XPerception(2, "f4", 1, "believes", null, Breakfast),                                             // both a fact and a character
                new XPerception(2, null, 2, "trusts", "e1", Stick),                                                   // itself
                new XPerception(2, null, 1, "trusts", null, Stick),                                                   // of a character, no cause event
            ],
            relations: [new XRelation("MET", 1, 2, null, Stick), new XRelation("RELATED_TO", 1, 2, null, Stick), new XRelation("LOCATED_AT", 1, 2, null, Stick)]),
            Chunk1, Chunk1Text, Entities, 0.9);

        Assert.Equal(["e1"], r.Events.Select(e => e.Local));
        Assert.Equal(["f4"], r.Facts.Select(f => f.Local));
        Assert.Equal(["e1:witnessed"], r.Facts[0].Discoveries.Select(d => $"{d.Event}:{d.How}"));
        Assert.Empty(r.Perceptions);
        Assert.Empty(r.Relations);
        string[] expected =
        [
            FailureTags.TypeOutsideOntology, FailureTags.BadEndpoint, FailureTags.BadEndpoint, FailureTags.QuoteMismatch, FailureTags.BadEndpoint,   // e2, e3 (participant, then the event), e5, then the referenced e4
            FailureTags.TypeOutsideOntology, FailureTags.NoDiscovery, FailureTags.BadEndpoint, FailureTags.NoDiscovery, FailureTags.BadEndpoint,     // f1 (mode, then no discovery), f2, f3, f4's e5
            FailureTags.TypeOutsideOntology, FailureTags.TypeOutsideOntology, FailureTags.BadEndpoint, FailureTags.BadEndpoint,                     // the perception with two targets is the item's shape
            FailureTags.TypeOutsideOntology, FailureTags.TypeOutsideOntology, FailureTags.BadEndpoint,
        ];
        Assert.Equal(expected, r.Rejected.Select(x => x.Tag));
        Assert.Contains("witnessed, told, overheard, inferred, read, recalled", r.Rejected.First(x => x.Item == "discovery").Reason);
    }

    [Fact]
    public void Stage3_votes_facts_and_events_at_m_and_perceptions_at_three()
    {
        ChunkGraph Reply(bool withDoubt, string how = "told") => Sample(
            events: [Narrated("e1")],
            facts: [new XFact("f1", "The visitor left his stick behind.", [4], Stick, [new("e1", how)])],
            perceptions: withDoubt ? [new XPerception(2, "f1", null, "doubts", "e1", Stick)] : []);
        CheckedSamples(out var two, Reply(true), Reply(true), Reply(false), Reply(false, "witnessed"), Reply(false, "told"));
        var voted = GraphStage3.Vote(two, Chunk1, minVotes: 2, perceptionMinVotes: 3);

        Assert.Equal(5, Assert.Single(voted.Events).Votes);
        var fact = Assert.Single(voted.Facts);
        Assert.Equal(5, fact.Votes);
        // The discovery's mode is the one most samples give.
        Assert.Equal(("told", 5), (fact.Discoveries[0].How, fact.Discoveries[0].Votes));
        Assert.Empty(voted.Perceptions);   // 2 of 5 samples: below the perception threshold

        CheckedSamples(out var three, Reply(true), Reply(true), Reply(true), Reply(false), Reply(false));
        var kept = Assert.Single(GraphStage3.Vote(three, Chunk1, 2, 3).Perceptions);
        Assert.Equal((2, "doubts", 3), (kept.Character, kept.Kind, kept.Votes));

        // A fact whose discovering event did not get the votes has no DISCOVERED edge and is dropped.
        CheckedSamples(out var lonely,
            Sample(events: [Narrated("e1")], facts: [new XFact("f1", "x", [1], Breakfast, [new("e1", "told")])]),
            Sample(events: [new XEvent("e1", "narrated", "Holmes sits at breakfast.", null, [new(1, null)], null, Breakfast, null)], facts: [new XFact("f1", "x", [1], Breakfast, [new("e1", "told")])]),
            Sample(), Sample(), Sample());
        var dropped = GraphStage3.Vote(lonely, Chunk1, 2, 3);
        Assert.Empty(dropped.Facts);
        Assert.Equal(FailureTags.NoDiscovery, Assert.Single(dropped.Rejected).Tag);
    }

    private static void CheckedSamples(out List<GraphStage3.CheckedSample> samples, params ChunkGraph[] outputs) =>
        samples = outputs.Select((o, i) => GraphStage3.Check(o, Chunk1, Chunk1Text, Entities, 0.9, i)).ToList();

    [Fact]
    public void Stage3_collapses_facts_with_the_same_subjects_and_near_identical_text_and_unions_their_discoveries()
    {
        var evidence = EvidenceChecker.Find(Stick, Chunk1Text, Chunk1.CharStart)!;
        EvidenceMatch At(int offset) => evidence with { CharStart = evidence.CharStart + offset, CharEnd = evidence.CharEnd + offset };
        GEvent E(string id, int chunk, int offset) => new(id, "narrated", "Someone tells it.", null, [new(2, "listener")], null, chunk, 1, Stick, At(offset), null, 5);
        GFact F(string id, int chunk, int offset, string text, string @event, string how, params int[] subjects) =>
            new(id, text, subjects.Length == 0 ? [1] : subjects, chunk, 1, Stick, At(offset), [new GDiscovery(@event, how, 3)], 3);
        var chunks = new List<GraphStage3.ChunkItems>
        {
            new([E("1:e0", 1, 0)], [F("1:f0", 1, 0, "Sir Charles died of heart failure.", "1:e0", "read")], [], [], [], [], []),
            new([E("9:e0", 9, 9000)],
            [
                F("9:f0", 9, 9000, "Sir Charles died of heart failure!", "9:e0", "told"),               // the same fact told later: one node
                F("9:f1", 9, 9100, "Sir Charles died of heart failure.", "9:e0", "told", 2),            // other subjects: another fact
                F("9:f2", 9, 9200, "Sir Charles was found dead in the yew alley.", "9:e0", "told"),     // other wording: another fact
            ],
            [new GPerception("9:p0", 2, "9:f0", null, "believes", "9:e0", 9, 1, Stick, At(9000), 3)],
            [new GRetraction("9:x0", null, null, "Sir Charles died of heart failure", [1], 9, 1, Stick, At(9300), 2)], [], [], []),
        };

        var a = GraphStage3.Assemble(chunks, GraphStage3.DefaultDedupeThreshold);

        Assert.Equal(1, a.FactsMerged);
        Assert.Equal(["F1", "F2", "F3"], a.Facts.Select(f => f.Id));
        // The kept node is the earliest; its DISCOVERED edges are the union (the same fact reached people at different times).
        Assert.Equal(["E1:read", "E2:told"], a.Facts[0].Discoveries.Select(d => $"{d.Event}:{d.How}"));
        Assert.Single(a.Facts[0].AlsoEvidence!);
        // What pointed at the merged fact points at the kept one; a retraction of an earlier fact is matched by its text.
        Assert.Equal("F1", a.Perceptions[0].Fact);
        Assert.Equal("F1", a.Retractions[0].Fact);
        Assert.True(a.Retractions[0].MatchScore >= GraphStage3.RetractionMatchThreshold);
        Assert.True(TextSimilarity.Score("Sir Charles died of heart failure.", "Sir Charles died of heart failure!") >= 0.9);
        Assert.True(TextSimilarity.Score("Sir Charles died of heart failure.", "Sir Charles was found dead in the yew alley.") < 0.9);
    }

    [Fact]
    public async Task Stage3_run_tells_the_model_the_narrator_and_the_previous_chunks_events()
    {
        var prompts = new List<string>();
        var client = new StubChatClient(prompt =>
        {
            prompts.Add(prompt);
            return JsonSerializer.Serialize(Sample(events: [Narrated("e1")], facts: [new XFact("f1", "The visitor left his stick behind.", [4], Stick, [new("e1", "witnessed")])]), Stage1.Json);
        });
        var skill = OldSkills.Load(SkillFile.ExtractFacts);
        var twoChunks = Stage0 with { Chunks = Stage0.Chunks.Take(2).ToList() };

        var r = await GraphStage3.RunAsync(client, null, skill, twoChunks, Text, Entities, 1, 1, 0.9, null, TestContext.Current.CancellationToken, new VotingSettings(1, 1), perceptionMinVotes: 1);

        Assert.Equal(2, prompts.Count);
        Assert.Contains("Narrator of this chapter: Dr. Watson (entity 2).", prompts[0]);
        Assert.Contains("\"I\" outside quotation marks is entity 2", prompts[0]);
        Assert.DoesNotContain("Events of the previous chunk", prompts[0]);
        Assert.Contains("Events of the previous chunk, for context only", prompts[1]);
        Assert.Contains("- Dr. Watson picks up the stick the visitor left.", prompts[1]);
        Assert.Equal(GraphStage3.Shape, r.Shape);
        Assert.Single(r.Events);
        Assert.Equal("witnessed", Assert.Single(Assert.Single(r.Facts).Discoveries).How);
        Assert.Equal(1, ((Dictionary<string, int>)GraphStage3.Counts(r)["discoveriesByHow"])["witnessed"]);
    }

    // ---- Stage 4: a made-up anchor, relative dates resolved by code, nothing left undated -----------------------------

    [Theory]
    [InlineData("1889-10-01", "P1D", "1889-10-02")]
    [InlineData("1889-10-01", "-P1D", "1889-09-30")]
    [InlineData("1889-10-01", "P0D", "1889-10-01")]
    [InlineData("1889-10-01", "P1W", "1889-10-08")]
    [InlineData("1889-10-01", "-P3M", "1889-07~")]      // months give a month, approximate
    [InlineData("1889-10-01", "-P2Y", "1887~")]
    [InlineData("1889-10", "P1D", "1889-10~")]           // no finer than the date it is counted from
    [InlineData("1889-10-01~", "P1D", "1889-10-02~")]
    [InlineData("1889-10-01", "PT3H", "1889-10-01")]     // a time of day is the same day
    public void Stage4_resolves_a_relative_expression_into_EDTF(string basis, string offset, string expected) =>
        Assert.Equal(expected, TimelineStage4.Resolve(Edtf.Parse(basis), offset));

    [Fact]
    public void Stage4_rejects_an_offset_it_cannot_read() => Assert.Null(TimelineStage4.Resolve(Edtf.Parse("1889-10-01"), "the next morning"));

    [Fact]
    public void Stage4_anchor_is_the_date_with_the_votes_else_the_month_or_the_year_most_agree_on()
    {
        AnchorAnswer A(string date) => new(date, "the stick is dated 1884, five years ago");
        var day = TimelineStage4.VoteAnchor([A("1889-10-01"), A("1889-10-01"), A("1889-10-01"), A("1889-09-25"), A("1888-10-01")], 3, "E1")!;
        Assert.Equal(("1889-10-01", "day", 3), (day.Edtf, day.VotedAt, day.Votes));
        Assert.StartsWith("Assumption:", day.Note);
        Assert.Contains("five years ago", day.Note);

        var month = TimelineStage4.VoteAnchor([A("1889-10-01"), A("1889-10-12"), A("1889-10-12"), A("1889-09-25"), A("1889-10-20")], 3, null)!;
        Assert.Equal(("1889-10-12", "month", 4), (month.Edtf, month.VotedAt, month.Votes));
        var year = TimelineStage4.VoteAnchor([A("1889-10-01"), A("1889-09-25"), A("1889-11-03"), A("1888-10-01"), A("not a date")], 3, null)!;
        Assert.Equal(("1889-09-25", "year"), (year.Edtf, year.VotedAt));
        Assert.Null(TimelineStage4.VoteAnchor([A("1889-10-01"), A("1888-09-25"), A("1887-11-03")], 3, null));
    }

    [Fact]
    public void Stage4_check_resolves_relative_dates_against_the_timeline_so_far_and_this_answer()
    {
        var known = new Dictionary<string, StoryDate> { ["E1"] = new("1889-10-01", "1889-10-01", "1889-10-01") };
        var r = TimelineStage4.Check(new ChunkTimes(
        [
            new("E2", null, null, "E1", "P1D"),            // the next day
            new("E3", null, null, "E2", "-P3M"),           // relative to a date given in this answer
            new("F1", "1884", "1882", null, null),          // until before from: the end is dropped
            new("F2", null, null, null, null),              // no date: left to code
            new("E4", "XXXX-05-04", null, null, null),      // no year
            new("E5", null, null, "E9", "P1D"),             // unknown event
            new("E6", null, null, "E6", "P1D"),             // itself
            new("E7", "next spring", null, null, null),
            new("E99", "1889", null, null, null),           // not an item of the chunk
        ]), Chunk1, new HashSet<string> { "E2", "E3", "E4", "E5", "E6", "E7", "F1", "F2" }, known);

        Assert.Equal("1889-10-02", r.Dates["E2"].From.Edtf);
        Assert.Equal(("1889-07~", "E2", "-P3M"), (r.Dates["E3"].From.Edtf, r.Dates["E3"].RelativeTo, r.Dates["E3"].Offset));
        Assert.Equal(("1884", null), (r.Dates["F1"].From.Edtf, r.Dates["F1"].Until));
        Assert.Equal(["E2", "E3", "F1"], r.Dates.Keys.Order());
        Assert.Equal(6, r.Rejected.Count);
        Assert.Equal(3, r.Rejected.Count(x => x.Tag == FailureTags.InvalidEdtf));
    }

    [Fact]
    public void Stage4_vote_keeps_a_majority_date_else_the_month_they_agree_on_else_leaves_it_to_code()
    {
        static TimelineStage4.CheckedTimes S(string? edtf) => new(edtf is null ? [] : new() { ["E2"] = (TimelineStage4.Date(Edtf.Parse(edtf)), null, null, null) }, []);
        var exact = TimelineStage4.VoteItem("E2", [S("1889-10-02"), S("1889-10-02"), S("1889-10-02"), S("1889-10-03"), S(null)], 3, out var unstable)!.Value;
        Assert.Equal(("1889-10-02", "llm", 3), (exact.From.Edtf, exact.Source, exact.Votes));
        Assert.Null(unstable);
        var coarse = TimelineStage4.VoteItem("E2", [S("1889-10-02"), S("1889-10-03"), S("1889-10-05"), S("1889-11-01"), S(null)], 3, out _)!.Value;
        Assert.Equal(("1889-10~", "llm-coarse"), (coarse.From.Edtf, coarse.Source));
        Assert.Null(TimelineStage4.VoteItem("E2", [S("1889-10-02"), S("1742"), S("1650"), S(null), S(null)], 3, out unstable));
        Assert.Contains("no date had 3 of 5 votes", unstable);
        Assert.Null(TimelineStage4.VoteItem("E2", [S(null), S(null)], 3, out unstable));
        Assert.Null(unstable);   // nobody gave a date: nothing unstable
    }

    // ---- Stage 5: seeding and closing ---------------------------------------------------------------------------------

    private static GEvent Event(string id, int chapter, int pos, string kind, string? mentionedIn, params (int Character, string? Role)[] participants) =>
        new(id, kind, $"Event {id}.", null, participants.Select(p => new GParticipant(p.Character, p.Role)).ToList(), null, 0, chapter, "q", new EvidenceMatch("exact", 1, pos, pos + 10), mentionedIn, 5);

    [Fact]
    public void Stage5_seeds_believes_for_what_is_witnessed_and_doubts_for_what_a_distrusted_speaker_tells()
    {
        var told = Event("E1", 1, 100, "narrated", null, (1, "speaker"), (2, "listener"), (4, "listener"));
        bool Distrusts(int who, int whom) => who == 4 && whom == 1;

        Assert.Equal([(1, "believes"), (2, "believes"), (4, "believes")], PerceptionStage5.Seeds(told, DiscoveryModes.Witnessed, Distrusts));
        Assert.Equal([(1, "believes"), (2, "believes"), (4, "believes")], PerceptionStage5.Seeds(told, DiscoveryModes.Read, Distrusts));
        Assert.Equal([(1, "believes"), (2, "believes"), (4, "believes")], PerceptionStage5.Seeds(told, DiscoveryModes.Recalled, Distrusts));
        // Told: the speaker believes what he says; a listener who distrusts or suspects him doubts it (the cascade).
        Assert.Equal([(1, "believes"), (2, "believes"), (4, "doubts")], PerceptionStage5.Seeds(told, DiscoveryModes.Told, Distrusts));
        Assert.Equal([(1, "believes"), (2, "believes"), (4, "doubts")], PerceptionStage5.Seeds(told, DiscoveryModes.Overheard, Distrusts));
        // No role names a speaker: every other participant is one.
        var unnamed = Event("E2", 1, 200, "narrated", null, (1, null), (4, null));
        Assert.Equal([(1, "believes"), (4, "doubts")], PerceptionStage5.Seeds(unnamed, DiscoveryModes.Told, Distrusts));
    }

    [Fact]
    public void Stage5_a_later_edge_closes_the_one_before_it_and_a_kind_is_never_changed_in_place()
    {
        static StoryDate Day(string d) => new(d, d, d);
        PerceptionEdge Edge(string kind, string day, int revealed, string? fact = "F1", int? target = null) =>
            new("", 2, fact, target, kind, "E1", "seeded", null, revealed, null, revealed, revealed + 5, null, 1, Day(day), null, null);
        var ledger = new PerceptionStage5.Ledger();
        Assert.NotNull(ledger.Add(Edge("believes", "1889-10-01", 100)));
        Assert.Null(ledger.Add(Edge("believes", "1889-10-03", 300)));                       // nothing changed: no new edge
        Assert.NotNull(ledger.Add(Edge("threatened", "1889-10-03", 310)));                  // another group: held at the same time
        Assert.NotNull(ledger.Add(Edge("disbelieves", "1889-10-05", 500)));                 // the change
        Assert.NotNull(ledger.Add(Edge("trusts", "1889-10-02", 200, fact: null, target: 1)));
        Assert.Equal("disbelieves", ledger.Current(2, "F1", PerceptionKinds.Credence)!.Kind);
        Assert.Equal("believes", ledger.At(2, "F1", PerceptionKinds.Credence, "1889-10-04")!.Kind);

        var (edges, closed) = ledger.Close();

        Assert.Equal(1, closed);
        var believes = edges.Single(e => e.Kind == "believes");
        var disbelieves = edges.Single(e => e.Kind == "disbelieves");
        // The earlier edge keeps its kind; it ends where the new one starts, on both clocks.
        Assert.Equal(("1889-10-05", 500, disbelieves.Id), (believes.Until!.Lo, believes.RetractedPos, believes.ClosedBy));
        Assert.Null(disbelieves.Until);
        Assert.Null(edges.Single(e => e.Kind == "threatened").Until);   // believes and threatened stand side by side
        Assert.Null(edges.Single(e => e.Kind == "trusts").Until);
        Assert.Equal(["Q1", "Q2", "Q3", "Q4"], edges.Select(e => e.Id));
    }

    [Fact]
    public void Stage5_an_edge_added_after_a_seed_was_removed_gets_an_id_of_its_own()
    {
        // The first full-book run ended on a duplicate id: a removed seed's number was handed to the next edge.
        static StoryDate Day(string d) => new(d, d, d);
        PerceptionEdge Edge(string kind, string fact, int revealed) => new("", 2, fact, null, kind, "E1", "seeded", null, revealed, null, revealed, revealed + 5, null, 1, Day("1889-10-01"), null, null);
        var ledger = new PerceptionStage5.Ledger();
        ledger.Add(Edge("believes", "F1", 100));
        var seed = ledger.Add(Edge("believes", "F2", 200))!;
        ledger.Remove(seed);
        ledger.Add(Edge("doubts", "F2", 210));
        ledger.Add(Edge("believes", "F3", 300));

        var (edges, _) = ledger.Close();

        Assert.Equal(3, edges.Count);
        Assert.Equal(3, edges.Select(e => e.Id).Distinct().Count());
    }

    // ---- The graph: both clocks on everything, the timeline, the leak tests --------------------------------------------

    /// <summary>A small run of the full schema over the real text split: two chapters, an identity revealed in chapter 2, a referenced
    /// event from long before, a lie believed and then exposed.</summary>
    internal static (Stage2Result Stage2, GraphStage3Result Stage3, TimelineStage4Result Stage4, PerceptionStage5Result Stage5) SmallRun()
    {
        var (c1, c2) = (Stage0.Chapters[0], Stage0.Chapters[1]);
        Entity Ent(int id, string name, MentionType type, int at, params string[] also) =>
            new(id, name, type, [name, .. also], [M(name, at, type), .. also.Select((a, i) => M(a, at + 500 * (i + 1), type))], at, [id]);
        var entities = new List<Entity>
        {
            Ent(1, "Sherlock Holmes", MentionType.Character, c1.CharStart + 100, "Holmes"),
            Ent(2, "Dr. Watson", MentionType.Character, c1.CharStart + 200),
            Ent(3, "Stapleton", MentionType.Character, c1.CharStart + 3000),
            Ent(4, "Vandeleur", MentionType.Character, c2.CharStart + 4000),
            Ent(5, "Baker Street", MentionType.Location, c1.CharStart + 300),
        };
        var identity = new IdentityReveal(3, 4, "he changed his name to Vandeleur", c2.CharStart + 4000, c2.CharStart + 4040, 4);
        var stage2 = new Stage2Result(Hound, [], [], [], entities, [], new ResolveSettings(), [identity]);

        EvidenceMatch At(int pos) => new("exact", 1, pos, pos + 40);
        GEvent E(string id, int chapter, int pos, string kind, string? mentionedIn, int? location, params (int, string?)[] who) =>
            Event(id, chapter, pos, kind, mentionedIn, who) with { Location = location, EvidenceMatch = At(pos) };
        var events = new List<GEvent>
        {
            E("E1", 1, c1.CharStart + 400, "narrated", null, 5, (1, "speaker"), (2, "listener")),
            E("E2", 1, c1.CharStart + 3100, "narrated", null, null, (3, "speaker"), (2, "listener")),
            E("E3", 2, c2.CharStart + 4100, "narrated", null, null, (1, "speaker"), (2, "listener")),
            E("E4", 2, c2.CharStart + 4200, "referenced", "E3", null, (4, null)),
        };
        GFact F(string id, int chapter, int pos, string text, int[] subjects, params (string Event, string How)[] discoveries) =>
            new(id, text, subjects, 0, chapter, "q", At(pos), discoveries.Select(d => new GDiscovery(d.Event, d.How, 3)).ToList(), 3);
        var facts = new List<GFact>
        {
            F("F1", 1, c1.CharStart + 450, "Holmes is late in the mornings.", [1], ("E1", "witnessed")),
            F("F2", 1, c1.CharStart + 3200, "Stapleton is a harmless naturalist.", [3], ("E2", "told")),
            F("F3", 2, c2.CharStart + 4250, "Vandeleur kept a school in Yorkshire.", [4], ("E4", "recalled"), ("E3", "told")),
        };
        var stage3 = new GraphStage3Result(GraphStage3.Shape, Hound, 1, 2, 0.9, 5, 2, 3, 0.9, [], events, facts,
            [new GPerception("P1", 2, null, 3, "suspects", "E3", 0, 2, "q", At(c2.CharStart + 4300), 3)],
            [new GRetraction("X1", "F2", null, "Stapleton is a harmless naturalist.", [3], 0, 2, "q", At(c2.CharStart + 4400), 3)],
            [], [new GRelation("R1", Ontology.RelatedTo, 2, 1, "friend", 0, 1, "q", At(c1.CharStart + 500), 3)], 0, new Dictionary<string, int>(), 0, 0, 0);

        static StoryDate D(string edtf) => TimelineStage4.Date(Edtf.Parse(edtf));
        var anchor = new AnchorChoice("1889-10-01", "Assumption: the story's first narrated event is placed on 1889-10-01.", "the stick", 4, "day", [], "E1");
        var timed = new Dictionary<string, TimedNode>
        {
            ["E1"] = new("E1", "event", 1, 0, D("1889-10-01"), null, "anchor", 2, 4),
            ["E2"] = new("E2", "event", 1, 0, D("1889-10-01"), null, "propagated:previous-event", 3, 0),
            ["E3"] = new("E3", "event", 2, 0, D("1889-10-02"), null, "llm", 4, 3, "E2", "P1D"),
            ["E4"] = new("E4", "event", 2, 0, D("1884~"), null, "llm", 1, 3, "E3", "-P5Y"),
            ["F1"] = new("F1", "fact", 1, 0, D("1889-10-01"), null, "propagated:discovering-event", 2, 0),
            ["F2"] = new("F2", "fact", 1, 0, D("1889-10-01"), null, "propagated:discovering-event", 3, 0),
            ["F3"] = new("F3", "fact", 2, 0, D("1884~"), null, "propagated:discovering-event", 1, 0),
        };
        var stage4 = new TimelineStage4Result(TimelineStage4.Shape, Hound, anchor, [], timed.Values.ToList(),
            TimelineStage4.Timeline(Stage0, stage3, anchor, timed), 5, 3, 0, new Dictionary<string, int>());
        var stage5 = PerceptionStage5.RunAsync(null, null, null, Stage0, Text, Stage3.Entities(stage2), stage3, stage4, 0.9, null, CancellationToken.None, new VotingSettings(5, 3))
            .GetAwaiter().GetResult();
        return (stage2, stage3, stage4, stage5);
    }

    internal static string BuildSmallRun(string folder, out GraphDbCounts counts)
    {
        var (stage2, stage3, stage4, stage5) = SmallRun();
        var path = Path.Combine(folder, "book.sqlite");
        counts = GraphDb.Build(path, Stage0, stage2, stage3, stage4, stage5);
        return path;
    }

    [Fact]
    public void Stage5_code_pass_seeds_one_credence_edge_per_participant_and_applies_retractions()
    {
        var (_, _, _, stage5) = SmallRun();
        // F1 witnessed by Holmes and Watson; F2 told by Stapleton to Watson; F3 recalled by Vandeleur and told by Holmes to Watson; plus Watson suspects Stapleton.
        Assert.Equal(["1 believes F1", "2 believes F1", "2 believes F2", "3 believes F2", "1 believes F3", "2 believes F3", "4 believes F3", "2 suspects c3"],
            stage5.Edges.OrderBy(e => e.TargetCharacter is not null).ThenBy(e => e.Fact, StringComparer.Ordinal).ThenBy(e => e.Character).Select(e => $"{e.Character} {e.Kind} {e.Target}"));
        Assert.All(stage5.Edges.Where(e => e.Fact is not null), e => Assert.Equal("seeded", e.Source));
        Assert.Equal("extracted", stage5.Edges.Single(e => e.TargetCharacter == 3).Source);
        // The lie is exposed in chapter 2: the Fact itself gets a retracted_pos.
        Assert.Equal("F2", Assert.Single(stage5.FactRetractions).Fact);
        var counts = PerceptionStage5.Counts(stage5);
        Assert.Equal(7, ((Dictionary<string, int>)counts["bySource"])["seeded"]);
        Assert.Equal(2, ((Dictionary<string, int>)counts["seededByHow"])[DiscoveryModes.Witnessed]);
    }

    // What the link stage flags reaches the database: a descriptive entity's kind, and a fact whose subject was a pronoun read by rule.
    [Fact]
    public void Build_db_writes_a_descriptive_entitys_kind_and_a_pronoun_subjects_flag()
    {
        using var temp = new TempFolder();
        var (stage2, stage3, stage4, stage5) = SmallRun();
        stage2 = stage2 with { Entities = stage2.Entities.Select(e => e.Id == 4 ? e with { Kind = Entity.Descriptive } : e).ToList() };
        stage3 = stage3 with { Facts = stage3.Facts.Select(f => f.Id == "F3" ? f with { SubjectSource = GFact.InferredPronoun } : f).ToList() };
        var path = Path.Combine(temp.Path, "book.sqlite");
        GraphDb.Build(path, Stage0, stage2, stage3, stage4, stage5);

        var graph = BookGraph.Load(path);
        NodeValue Node(string key, string value) => graph.Nodes.Single(n => n.Properties.GetValueOrDefault(key) as string == value);
        Assert.Equal("descriptive", Node("name", "Vandeleur").Properties["kind"]);
        Assert.False(Node("name", "Stapleton").Properties.ContainsKey("kind"));
        Assert.Equal("inferred_pronoun", graph.Nodes.Single(n => n.Labels.Contains("Fact") && n.Properties.GetValueOrDefault("stage3Id") as string == "F3").Properties["subjectSource"]);
        Assert.False(graph.Nodes.Single(n => n.Labels.Contains("Fact") && n.Properties.GetValueOrDefault("stage3Id") as string == "F1").Properties.ContainsKey("subjectSource"));
    }

    [Fact]
    public void Build_db_puts_both_clocks_on_every_node_and_edge_writes_the_timeline_and_passes_the_leak_tests()
    {
        using var temp = new TempFolder();
        var path = BuildSmallRun(temp.Path, out var counts);

        Assert.Equal(0, counts.StoryNull);
        Assert.Equal(3, counts.StoryFromLlm);   // the anchor's event and the two events the model dated
        Assert.Equal(counts.Rows["nodes"] + counts.Rows["edges"] - 3, counts.StoryPropagated);
        Assert.Equal(counts.Edges, counts.DatedEdges);
        Assert.Equal(new Dictionary<string, int> { ["Character"] = 4, ["Event"] = 4, ["Fact"] = 3, ["Location"] = 1 }, counts.Labels);
        Assert.Equal(new Dictionary<string, int>
        {
            ["DISCOVERED"] = 4, ["IDENTICAL_TO"] = 1, ["LOCATED_AT"] = 1, ["MENTIONED_IN"] = 1, ["PARTICIPATED_IN"] = 7, ["PERCEIVES"] = 8, ["RELATED_TO"] = 1,
        }, counts.EdgeTypes);
        Assert.Equal(1 + 2 + 4, counts.Rows["timeline"]);   // the anchor, every chapter of the run, every Event

        using (var db = BookDatabase.OpenReadOnlyFile(path))
        {
            Assert.All(db.Nodes.ToList(), n => Assert.True(n.RevealedPos is not null && n.StoryFromEdtf is not null && n.StoryFromLo is not null));
            Assert.All(db.Edges.ToList(), e => Assert.True(e.RevealedPos is not null && e.StoryFromEdtf is not null && e.StoryFromLo is not null));
            Assert.Equal("Dr. Watson (letter)", db.Chapters.Single(c => c.Number == 8).Narrator);
            var anchor = db.Timeline.Single(t => t.Kind == TimelineKinds.Anchor);
            Assert.Equal("1889-10-01", anchor.StoryFromEdtf);
            Assert.StartsWith("Assumption:", anchor.Note);
        }

        var graph = BookGraph.Load(path);
        NodeValue Node(string key, string value) => graph.Nodes.Single(n => n.Properties.GetValueOrDefault(key) as string == value);
        var (c1, c2) = (Stage0.Chapters[0], Stage0.Chapters[1]);
        // A referenced event: its own earlier story time, revealed no earlier than the event that mentions it.
        var referenced = Node("stage3Id", "E4");
        Assert.Equal(("referenced", "1884~"), (referenced.Properties["kind"], referenced.Properties["storyFrom"]));
        Assert.True((long)referenced.Properties["revealedPos"]! >= (long)Node("stage3Id", "E3").Properties["revealedPos"]!);
        // Backstory pushes a character's story time earlier: Vandeleur exists from the school in 1884, and Stapleton, the same man, with him.
        Assert.Equal("1884-01-01", Node("name", "Vandeleur").Properties["storyFromLo"]);
        Assert.Equal("1884-01-01", Node("name", "Stapleton").Properties["storyFromLo"]);
        // The identity carries both clocks: revealed in chapter 2, true in story time from the start.
        var identity = graph.Relationships.Single(r => r.Type == Ontology.IdenticalTo);
        Assert.Equal(((long)(c2.CharStart + 4000), "1884-01-01"), ((long)identity.Properties["revealedPos"]!, identity.Properties["storyFromLo"]));
        // The lie: the Fact is retracted where the text exposes it.
        Assert.Equal((long)(c2.CharStart + 4400), Node("stage3Id", "F2").Properties["retractedPos"]);
        // A Fact names its subjects by node id (no ABOUT edge), and is discovered by an Event with a mode.
        Assert.Equal([Node("name", "Vandeleur").Id], LeakTests.Subjects(Node("stage3Id", "F3")));
        Assert.Equal(["recalled", "told"], graph.Incoming(Node("stage3Id", "F3").Id).Where(r => r.Type == Ontology.Discovered).Select(r => (string)r.Properties["how"]!).Order());

        Assert.All(LeakTests.Run(graph), check => Assert.True(check.Passed, $"{check.Name}: {string.Join("; ", check.Failures)}"));
        // story(pos): at the end of chapter 1 the story clock stands at the anchor day; chapter 2 moves it on a day. The 1884 flashback never turns it back.
        Assert.Equal("1889-10-01", graph.StoryAt(c1.CharEnd));
        Assert.Equal("1889-10-02", graph.StoryAt(c2.CharEnd));
    }

    [Fact]
    public void Spoiler_rule_spoilers_off_follows_the_reader_and_spoilers_on_follows_story_time()
    {
        using var temp = new TempFolder();
        var graph = BookGraph.Load(BuildSmallRun(temp.Path, out _));
        var (c1, c2) = (Stage0.Chapters[0], Stage0.Chapters[1]);
        List<string> Names(long pos, bool spoilers) => LeakTests.View(graph, pos, spoilers).Nodes.Select(id => graph.Node(id))
            .Select(n => n.Properties.GetValueOrDefault("name") as string ?? (string)n.Properties["stage3Id"]!).Order(StringComparer.Ordinal).ToList();

        // Spoilers off, end of chapter 1: Vandeleur, the 1884 school and chapter 2 are not there; the lie still stands.
        Assert.Equal(["Baker Street", "Dr. Watson", "E1", "E2", "F1", "F2", "Sherlock Holmes", "Stapleton"], Names(c1.CharEnd, false));
        // Spoilers off, end of chapter 2: everything revealed, and the lie is gone (retracted).
        Assert.Equal(["Baker Street", "Dr. Watson", "E1", "E2", "E3", "E4", "F1", "F3", "Sherlock Holmes", "Stapleton", "Vandeleur"], Names(c2.CharEnd, false));
        // Spoilers on, end of chapter 1: story time has reached 1 October 1889, so the 1884 school and Vandeleur are visible although
        // the reader meets them only in chapter 2; chapter 2's own event (2 October) is not.
        Assert.Equal(["Baker Street", "Dr. Watson", "E1", "E2", "E4", "F1", "F2", "F3", "Sherlock Holmes", "Stapleton", "Vandeleur"], Names(c1.CharEnd, true));
        var identity = graph.Relationships.Single(r => r.Type == Ontology.IdenticalTo);
        Assert.False(SpoilerGate.Visible(identity.Properties, c1.CharEnd, false, null));                       // hidden before the reveal
        Assert.True(SpoilerGate.Visible(identity.Properties, c1.CharEnd, true, graph.StoryAt(c1.CharEnd)));    // collapsed in story time
    }

    [Fact]
    public void Leak_tests_fail_on_an_edge_revealed_before_its_end_and_on_an_undated_element()
    {
        static Dictionary<string, object?> Clock(long revealed, string day) => new() { ["revealedPos"] = revealed, ["storyFrom"] = day, ["storyFromLo"] = day, ["storyFromHi"] = day };
        var late = Clock(900, "1889-10-05");
        var graph = new BookGraph(
            [new(1, ["Character"], Clock(10, "1889-10-01")), new(2, ["Character"], late), new(3, ["Fact"], new Dictionary<string, object?> { ["text"] = "undated" })],
            [new(7, "RELATED_TO", 1, 2, Clock(20, "1889-10-01"))],
            new Dictionary<int, (int, int)> { [1] = (0, 500), [2] = (500, 1000) },
            timeline: [new("chapter", 1, "One", 0, 500, "1889-10-01", null, "1889-10-01", "1889-10-01", null, null, null, null), new("chapter", 2, "Two", 500, 1000, "1889-10-05", null, "1889-10-05", "1889-10-05", null, null, null, null)]);

        var checks = LeakTests.Run(graph).ToDictionary(c => c.Name);
        Assert.Contains("edge 7 (RELATED_TO) passes but an end (2) is hidden", checks["spoilers-off"].Failures[0]);
        Assert.False(checks["spoilers-on"].Passed);
        Assert.Contains("node 3 has no revealedPos", checks["clocks"].Failures);
    }

    // ---- Reading a book file built before the full schema ------------------------------------------------------------------------

    [Fact]
    public void A_book_file_of_the_earlier_schema_is_read_through_an_upgraded_copy_and_never_changed()
    {
        using var temp = new TempFolder();
        var path = Path.Combine(temp.Path, "old.sqlite");
        BookDatabase.Create(path);
        // Take the file back to the earlier schema: no clocks on nodes, no narrator, no timeline.
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
        {
            db.Open();
            string[] statements =
            [
                "DROP TABLE timeline", "ALTER TABLE chapters DROP COLUMN narrator",
                .. new[] { "revealed_pos", "retracted_pos", "story_from_edtf", "story_until_edtf", "story_from_lo", "story_from_hi", "story_until_lo", "story_until_hi", "fabula_seq" }
                    .Select(c => $"ALTER TABLE nodes DROP COLUMN {c}"),
                "INSERT INTO chapters (number, title, char_start, char_end) VALUES (1, 'One', 0, 100)",
                "INSERT INTO nodes (id, properties, evidence_start) VALUES (5, '{\"name\":\"Holmes\"}', 3)",
            ];
            foreach (var sql in statements)
            {
                using var cmd = db.CreateCommand();
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
            }
        }
        SqliteConnection.ClearAllPools();
        var before = File.ReadAllBytes(path);
        Assert.False(BookDatabase.HasCurrentSchema(path));

        var graph = BookGraph.Load(path);
        using (var db = BookDatabase.OpenReadOnlyFile(path))
        {
            Assert.Null(db.Chapters.Single().Narrator);
            Assert.Empty(db.Timeline.ToList());
        }

        Assert.Equal("Holmes", graph.Nodes.Single().Properties["name"]);
        Assert.False(graph.Nodes.Single().Properties.ContainsKey("revealedPos"));
        Assert.Empty(graph.Timeline);
        Assert.Null(graph.StoryAt(100));
        Assert.Equal(before, File.ReadAllBytes(path));          // the file itself is untouched
        Assert.False(BookDatabase.HasCurrentSchema(path));
    }

    // ---- Promote ------------------------------------------------------------------------------------------------------

    [Fact]
    public void Promote_copies_the_timeline_leak_tests_the_graph_and_writes_the_anchor_to_the_provenance_and_the_manifest()
    {
        using var temp = new TempFolder();
        var run = RunFolder.Create(Path.Combine(temp.Path, "runs"));
        run.RecordCommand(new { command = "test" }, new Dictionary<string, object?> { ["book"] = Hound, ["textSha256"] = "hash", ["chapters"] = new { first = 1, last = 2 } });
        BuildSmallRun(run.Path, out _);
        // The committed file: the text split only, as index prepare leaves it.
        var target = Path.Combine(temp.Path, Hound + ".sqlite");
        BookDatabase.Create(target);
        using (var db = BookDatabase.OpenReadWrite(target))
        {
            db.Chapters.AddRange(Stage0.Chapters.Select(c => new Chapter { Number = c.Number, Title = c.Title, CharStart = c.CharStart, CharEnd = c.CharEnd }));
            db.Positions.AddRange(Stage0.Chapters.SelectMany(c => c.Paragraphs).Select(p => new Position { Chapter = p.Chapter, Paragraph = p.Paragraph, CharStart = p.CharStart, CharEnd = p.CharEnd }));
            db.SaveChanges();
        }
        var manifest = Path.Combine(temp.Path, "books.json");
        BookManifest.Write(manifest, [new BookEntry(Hound, "The Hound of the Baskervilles", "Arthur Conan Doyle", Chat: ["Sherlock Holmes"]), new BookEntry("other", "Other", "Someone")]);
        var provenance = Path.Combine(temp.Path, "provenance.json");
        Assert.False(Promotion.AlreadyPromoted(run, target, provenance));

        var outcome = Promotion.Promote(run, Hound, target, "hash", provenance, manifest);

        // The run that is promoted is "unchanged" when promoted again; another run, or a graph that differs, is not.
        Assert.True(Promotion.AlreadyPromoted(run, target, provenance));
        var other = RunFolder.Create(Path.Combine(temp.Path, "runs"));
        File.Copy(run.File(RunFolder.BookSqlite), other.File(RunFolder.BookSqlite));
        Assert.False(Promotion.AlreadyPromoted(other, target, provenance));
        Assert.Equal(7, outcome.Rows["timeline"]);
        var p = JsonNode.Parse(File.ReadAllText(provenance))!;
        Assert.Equal("1889-10-01", p["anchor"]!["edtf"]!.GetValue<string>());
        Assert.All(new[] { "spoilers-off", "spoilers-on", "identity", "referenced-events", "clocks" }, name => Assert.True(p["leakTests"]![name]!["passed"]!.GetValue<bool>()));
        var entries = BookManifest.Read(manifest);
        Assert.Equal("1889-10-01", entries[0].AnchorEdtf);
        Assert.StartsWith("Assumption:", entries[0].AnchorNote);
        Assert.Equal(["Sherlock Holmes"], entries[0].Chat);
        Assert.Null(entries[1].AnchorEdtf);
        using var promoted = BookDatabase.OpenReadOnlyFile(target);
        Assert.Equal("Dr. Watson (diary)", promoted.Chapters.Single(c => c.Number == 10).Narrator);   // the narrator column comes with the graph
        Assert.Equal(7, promoted.Timeline.Count());
    }

    [Fact]
    public void Stages_option_reads_a_range_within_two_to_five()
    {
        Assert.Equal((2, 5), Pipeline.ParseStages("2..5"));
        Assert.Equal((3, 4), Pipeline.ParseStages("3-4"));
        Assert.Equal((4, 4), Pipeline.ParseStages("4"));
        Assert.Null(Pipeline.ParseStages("1..5"));   // Stage 1 is index mentions
        Assert.Null(Pipeline.ParseStages("5..2"));
        Assert.Null(Pipeline.ParseStages("two"));
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

    internal sealed class TempFolder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "TemporalGraph.Data.Tests", Guid.NewGuid().ToString("N"));
        public TempFolder() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
