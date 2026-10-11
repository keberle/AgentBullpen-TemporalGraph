using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using TemporalGraph.Api;
using TemporalGraph.Cli;
using TemporalGraph.Chat;
using TemporalGraph.Cli.Runs;
using TemporalGraph.Data;
using TemporalGraph.Query;
using QueryFixture = TemporalGraph.Query.Tests.Fixture;

namespace TemporalGraph.Data.Tests;

// chat: the query tools on the hand-built graph, character resolution, script parsing, the prompt template, and the
// agent round trip against FakeLlmServer (no real network).
public class ChatTests
{
    private const string Hound = "hound-of-the-baskervilles";
    // The hand-built graphs below have the shape of before the full schema (facts as edges), so they run the query files of that shape: the
    // books not yet re-indexed (their folders are identical). The Hound's own files, on the full schema, are in HoundQueriesDir.
    private static readonly string QueriesDir = Path.Combine(AppContext.BaseDirectory, "queries-earlier");   // the earlier-shape tools' queries, for the hand-built graphs of that shape
    private static readonly string HoundQueriesDir = Path.Combine(AppContext.BaseDirectory, "queries", Hound);

    private static ChatTools Tools(BookGraph graph, int chapter, bool spoilers = false, int maxRows = 50, List<ToolCallRecord>? log = null) =>
        new(graph, ChatTools.LoadQueries(QueriesDir), chapter, spoilers, maxRows, log is null ? null : log.Add);

    private static string[] Lines(string text) => text.Split('\n');

    // ---- Tools ----

    [Fact]
    public void Each_tool_runs_its_query_file_with_the_fixed_parameters_and_renders_rows()
    {
        var log = new List<ToolCallRecord>();
        var tools = Tools(QueryFixture.Graph, chapter: 2, log: log);

        Assert.Equal(3000, tools.Pos);
        // Revealed offsets are shown as the chapter; the id columns are not shown.
        Assert.Equal(["Holmes is late in the mornings. · Sherlock Holmes · ch. 1", "Mortimer's stick dates from 1884. · Dr. Mortimer, Sherlock Holmes · ch. 1"],
            Lines(tools.Run("facts_about", "Holmes")));
        Assert.Equal(["Dr. Watson", "Dr. Mortimer"], Lines(tools.Run("who_met", "Holmes")).Order().Reverse());
        Assert.Equal(["Sherlock Holmes · Baker Street · null"], Lines(tools.Run("where_placed", null)));
        Assert.Equal(["Mortimer visits Baker Street. · null · ch. 1"], Lines(tools.Run("events_for", "Mortimer")));
        Assert.Equal(["Sherlock Holmes · friend"], Lines(tools.Run("relations", "Watson")));
        Assert.Equal(["Sherlock Holmes", "Dr. Watson", "Dr. Mortimer", "Selden"], Lines(tools.Run("characters_so_far", null)));
        Assert.Equal(["Sherlock Holmes · 2", "Dr. Mortimer · 1"], Lines(tools.Run("most_discussed", null)));
        Assert.Equal(["Sherlock Holmes ·  · null", "null · Mortimer visits Baker Street. · null"], Lines(tools.Run("at_location", "Baker Street")));

        Assert.Equal(["facts_about", "who_met", "where_placed", "events_for", "relations", "characters_so_far", "most_discussed", "at_location"], log.Select(c => c.Tool));
        Assert.Equal([2, 2, 1, 1, 1, 4, 2, 2], log.Select(c => c.Rows));
        Assert.Equal("Holmes", log[0].Args["character"]);
        Assert.Empty(log[2].Args);
        Assert.Equal("Baker Street", log[7].Args["location"]);
        Assert.All(log, c => Assert.False(c.UnknownName));
    }

    [Fact]
    public void Spoilers_off_hides_what_the_reader_has_not_reached()
    {
        Assert.Equal(["Sherlock Holmes"], Lines(Tools(QueryFixture.Graph, chapter: 1).Run("who_met", "Watson")));
        Assert.Equal(["Sherlock Holmes", "Selden"], Lines(Tools(QueryFixture.Graph, chapter: 1, spoilers: true).Run("who_met", "Watson")));
    }

    [Fact]
    public void Unknown_name_returns_nothing_and_a_hint_with_the_canonical_names()
    {
        var log = new List<ToolCallRecord>();
        // "Sherlock" alone now resolves by containment (Sherlock Holmes); a name no alias contains gets the hint.
        var text = Tools(QueryFixture.Graph, chapter: 1, log: log).Run("facts_about", "Moriarty");

        Assert.Equal([ChatTools.Nothing, "No character goes by \"Moriarty\". Known names: Sherlock Holmes, Dr. Watson, Dr. Mortimer."], Lines(text));
        var call = Assert.Single(log);
        Assert.Equal(0, call.Rows);
        Assert.True(call.UnknownName);
    }

    [Fact]
    public void Known_name_with_no_rows_gets_no_hint()
    {
        Assert.Equal(ChatTools.Nothing, Tools(QueryFixture.Graph, chapter: 1).Run("relations", "Selden"));
    }

    [Fact]
    public void Results_are_capped_at_max_rows()
    {
        var nodes = Enumerable.Range(1, 60).Select(i => new NodeValue(i, ["Character"],
            new Dictionary<string, object?> { ["name"] = $"C{i}", ["aliases"] = new List<object?> { $"C{i}" }, ["evidenceStart"] = (long)i }));
        var graph = new BookGraph(nodes, [], new Dictionary<int, (int, int)> { [1] = (0, 1000) });
        var log = new List<ToolCallRecord>();

        var lines = Lines(Tools(graph, chapter: 1, maxRows: 50, log: log).Run("characters_so_far", null));

        Assert.Equal(51, lines.Length);
        Assert.Equal("C50", lines[49]);
        Assert.Equal("(first 50 of 60 rows)", lines[50]);
        Assert.Equal(60, log.Single().Rows);
        Assert.True(log.Single().Truncated);
    }

    [Fact]
    public void Tools_are_bound_with_their_names_descriptions_and_parameters()
    {
        var functions = Tools(QueryFixture.Graph, chapter: 1).AsAITools().Cast<Microsoft.Extensions.AI.AIFunction>().ToList();

        Assert.Equal(ChatTools.Specs.Select(s => s.Name), functions.Select(f => f.Name));
        Assert.Equal(ChatTools.Specs.Select(s => s.Description), functions.Select(f => f.Description));
        foreach (var (f, spec) in functions.Zip(ChatTools.Specs))
        {
            var properties = f.JsonSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToList();
            Assert.Equal(spec.Parameter switch { ToolParameter.None => [], ToolParameter.Location => ["location"], ToolParameter.Keywords => ["keywords"], _ => ["character"] }, properties);
        }
    }

    [Fact]
    public void Unknown_location_returns_nothing_and_a_hint_with_the_places_mentioned_so_far()
    {
        var log = new List<ToolCallRecord>();
        var text = Tools(QueryFixture.Graph, chapter: 1, log: log).Run("at_location", "the moor");

        // Baskerville Hall is first mentioned at 900 < 1000 (end of chapter 1); Baker Street at 30.
        Assert.Equal([ChatTools.Nothing, "No location goes by \"the moor\". Known places: Baker Street, Baskerville Hall."], Lines(text));
        Assert.True(Assert.Single(log).UnknownName);
    }

    [Fact]
    public void Facts_about_accepts_a_place_name_without_a_hint()
    {
        var log = new List<ToolCallRecord>();
        Assert.Equal(ChatTools.Nothing, Tools(QueryFixture.Graph, chapter: 2, log: log).Run("facts_about", "Baker Street"));
        Assert.False(Assert.Single(log).UnknownName);
    }

    // ---- Knowledge pack ----

    [Fact]
    public void Knowledge_pack_has_the_five_sections_for_the_selected_character()
    {
        var log = new List<ToolCallRecord>();
        var (text, sections) = Tools(QueryFixture.Graph, chapter: 2, log: log).KnowledgePack("Sherlock Holmes", "Holmes");

        Assert.Equal(
            """
            What you, Sherlock Holmes, know at this point (from the record; nothing else is known):
            Rows are stamped with the chapter in which you learned them. Phrases like "three months ago" or "earlier that evening" are relative to that chapter, not to now.
            ## About yourself
            Holmes is late in the mornings. · Sherlock Holmes · ch. 1
            Mortimer's stick dates from 1884. · Dr. Mortimer, Sherlock Holmes · ch. 1
            ## People you have met
            Dr. Watson
            Dr. Mortimer
            ## Your relations
            Dr. Watson · friend
            ## Events you took part in
            Mortimer visits Baker Street. · null · ch. 1
            ## Where people have been placed
            Sherlock Holmes · Baker Street · null
            """.ReplaceLineEndings("\n"), text);
        Assert.Equal([2, 2, 1, 1, 1], sections.Select(s => s.Rows));
        Assert.Empty(log); // not tool calls
    }

    [Fact]
    public void Knowledge_pack_says_nothing_for_an_empty_section_and_respects_the_gate()
    {
        var (text, _) = Tools(QueryFixture.Graph, chapter: 2).KnowledgePack("Selden", "Selden");
        Assert.Contains("## About yourself\n(nothing)\n## People you have met\nDr. Watson\n## Your relations\n(nothing)\n## Events you took part in\n(nothing)\n", text);

        // Chapter 1: Selden's meeting with Watson (revealed at 2100) is not known yet.
        Assert.Contains("## People you have met\n(nothing)", Tools(QueryFixture.Graph, chapter: 1).KnowledgePack("Selden", "Selden").Text);
        Assert.Contains("## People you have met\nDr. Watson", Tools(QueryFixture.Graph, chapter: 1, spoilers: true).KnowledgePack("Selden", "Selden").Text);
    }

    [Fact]
    public void Knowledge_pack_sections_are_capped_at_max_rows()
    {
        var nodes = new List<NodeValue> { new(1000, ["Location"], new Dictionary<string, object?> { ["name"] = "Moor", ["aliases"] = new List<object?> { "Moor" } }) };
        nodes.AddRange(Enumerable.Range(1, 60).Select(i => new NodeValue(i, ["Character"],
            new Dictionary<string, object?> { ["name"] = $"C{i}", ["aliases"] = new List<object?> { $"C{i}" }, ["evidenceStart"] = (long)i })));
        var edges = Enumerable.Range(1, 60).Select(i => new RelationshipValue(2000 + i, "LOCATED_AT", i, 1000, new Dictionary<string, object?> { ["revealedPos"] = (long)i }));
        var graph = new BookGraph(nodes, edges, new Dictionary<int, (int, int)> { [1] = (0, 1000) });

        var (text, sections) = Tools(graph, chapter: 1, maxRows: 50).KnowledgePack("C1", "C1");

        Assert.Equal(60, sections.Single(s => s.Tool == "where_placed").Rows);
        // where_placed is ordered by reveal, so the cap keeps the latest rows.
        Assert.Contains("## Where people have been placed\nC11 · Moor · null\n", text);
        Assert.EndsWith("C60 · Moor · null\n(last 50 of 60 rows; earlier rows omitted)", text);
    }

    // ---- Display names, recent-first cap, chapter stamps, name retry ----

    private static NodeValue Entity(long id, string label, string name, params (string Alias, long First)[] aliases) =>
        new(id, [label], new Dictionary<string, object?>
        {
            ["name"] = name,
            ["aliases"] = aliases.Select(a => (object?)a.Alias).ToList(),
            ["aliasFirstPos"] = aliases.ToDictionary(a => a.Alias, a => (object?)a.First),
            ["evidenceStart"] = aliases.Min(a => a.First),
        });

    private static NodeValue FactNode(long id, string text) => new(id, ["Fact"], new Dictionary<string, object?> { ["text"] = text });

    private static RelationshipValue About(long id, long fact, long entity, long revealed) =>
        new(id, "ABOUT", fact, entity, new Dictionary<string, object?> { ["revealedPos"] = revealed });

    /// <summary>The fixture: "the lady" (100), "Miss Stapleton" (5000), "Beryl Garcia" (90000), and her brother.</summary>
    private static NodeValue Lady => Entity(1, "Character", "Beryl Garcia", ("the lady", 100), ("Miss Stapleton", 5000), ("Beryl Garcia", 90000));

    private static BookGraph StapletonGraph(params (int Chapter, int Start, int End)[] chapters) => new(
        [Lady, Entity(2, "Character", "Mr. Stapleton", ("Stapleton", 50), ("Mr. Stapleton", 50)), FactNode(10, "She lives with her brother.")],
        [About(20, 10, 1, 150), About(21, 10, 2, 150)],
        chapters.ToDictionary(c => c.Chapter, c => (c.Start, c.End)));

    [Theory]
    [InlineData(2000L, false, "the lady")]
    [InlineData(20000L, false, "the lady")]
    [InlineData(2000L, true, "Beryl Garcia")]
    public void Display_name_is_the_earliest_alias_mentioned_by_pos_or_the_canonical_name_with_spoilers(long pos, bool spoilers, string expected) =>
        Assert.Equal((expected, false), ChatTools.DisplayName(Lady, pos, spoilers));

    [Fact]
    public void Display_name_rules()
    {
        // Only aliases mentioned by $pos qualify: with "the lady" first spelled at 30000, "Miss Stapleton" is used before that.
        var node = Entity(1, "Character", "Beryl Garcia", ("the lady", 30000), ("Miss Stapleton", 5000), ("Beryl Garcia", 90000));
        Assert.Equal("Miss Stapleton", ChatTools.DisplayName(node, 20000, spoilers: false).Name);
        Assert.Equal("Miss Stapleton", ChatTools.DisplayName(node, 89999, spoilers: false).Name);
        // Not mentioned by any name yet: the canonical name, flagged late.
        Assert.Equal(("Beryl Garcia", true), ChatTools.DisplayName(Lady, 50, spoilers: false));
        // Ties: the longest alias.
        Assert.Equal(("Mr. Stapleton", false), ChatTools.DisplayName(Entity(2, "Character", "X", ("Stapleton", 50), ("Mr. Stapleton", 50)), 2000, spoilers: false));
        // Offsets read from the database come back as double (BookGraph): the same result.
        var asDouble = Entity(1, "Character", "Beryl Garcia", ("Beryl", 148500), ("Beryl Garcia", 341045));
        asDouble = asDouble with { Properties = new Dictionary<string, object?>(asDouble.Properties) { ["aliasFirstPos"] = new Dictionary<string, object?> { ["Beryl"] = 148500.0, ["Beryl Garcia"] = 341045.0 } } };
        Assert.Equal(("Beryl", false), ChatTools.DisplayName(asDouble, 174096, spoilers: false));
        // A node without aliasFirstPos (a graph built without alias first positions) keeps its name.
        Assert.Equal(("Sherlock Holmes", false), ChatTools.DisplayName(QueryFixture.Graph.Nodes.First(), 0, spoilers: false));
    }

    [Fact]
    public void Display_name_is_the_short_name_once_the_reader_has_met_it()
    {
        NodeValue WithShort(NodeValue n, string shortName) => n with { Properties = new Dictionary<string, object?>(n.Properties) { ["shortName"] = shortName } };
        // Athos: "Athos" first at 500 (double, as read from the database), the canonical "Monsieur Athos" at 900.
        var athos = WithShort(Entity(5, "Character", "Monsieur Athos", ("Athos", 500), ("Monsieur Athos", 900)), "Athos");
        athos = athos with { Properties = new Dictionary<string, object?>(athos.Properties) { ["aliasFirstPos"] = new Dictionary<string, object?> { ["Athos"] = 500.0, ["Monsieur Athos"] = 900.0 } } };
        Assert.Equal(("Athos", false), ChatTools.DisplayName(athos, 1000, spoilers: false));
        Assert.Equal(("Athos", false), ChatTools.DisplayName(athos, 1000, spoilers: true));
        // Not met yet: the earliest alias, never the later short name; spoilers on: the short name.
        var lady = WithShort(Lady, "Miss Stapleton");
        Assert.Equal(("the lady", false), ChatTools.DisplayName(lady, 2000, spoilers: false));
        Assert.Equal(("Miss Stapleton", false), ChatTools.DisplayName(lady, 20000, spoilers: false));
        Assert.Equal(("Miss Stapleton", false), ChatTools.DisplayName(lady, 2000, spoilers: true));
    }

    [Fact]
    public void Tool_rows_and_about_lists_show_display_names()
    {
        // Chapter 1 ends at 2000, chapter 2 at 20000.
        var graph = StapletonGraph((1, 0, 2000), (2, 2000, 20000), (3, 20000, 100000));
        var tools = Tools(graph, chapter: 1);
        Assert.Equal("She lives with her brother. · the lady, Mr. Stapleton · ch. 1", tools.Run("facts_about", "Stapleton"));
        Assert.Equal("She lives with her brother. · the lady, Mr. Stapleton · ch. 1", Tools(graph, chapter: 2).Run("facts_about", "Stapleton"));
        Assert.Equal("She lives with her brother. · Beryl Garcia, Mr. Stapleton · ch. 1", Tools(graph, chapter: 1, spoilers: true).Run("facts_about", "Miss Stapleton"));
        Assert.Equal(0, tools.Calls.Single().LateRowsSuppressed);

        // Without "the lady": "Miss Stapleton" at chapter 2 (5000 <= 20000), never "Beryl Garcia" before chapter 3 ends.
        var later = new BookGraph(
            [Entity(1, "Character", "Beryl Garcia", ("Miss Stapleton", 5000), ("Beryl Garcia", 90000)), Entity(2, "Character", "Mr. Stapleton", ("Stapleton", 50)), FactNode(10, "She lives with her brother.")],
            [About(20, 10, 1, 6000), About(21, 10, 2, 6000)],
            new Dictionary<int, (int, int)> { [1] = (0, 2000), [2] = (2000, 20000), [3] = (20000, 100000) });
        Assert.Equal("She lives with her brother. · Miss Stapleton, Stapleton · ch. 2", Tools(later, chapter: 2).Run("facts_about", "Stapleton"));
        Assert.Equal("She lives with her brother. · Beryl Garcia, Mr. Stapleton · ch. 2", Tools(later, chapter: 2, spoilers: true).Run("facts_about", "Stapleton"));
    }

    /// <summary>The late-name fixture: the fact is revealed at 150, but its second entity is first mentioned at 15,000 (chapter 2).</summary>
    private static BookGraph LateGraph(params (string Text, long Revealed)[] more) => new(
        [Entity(1, "Character", "Stapleton", ("Stapleton", 50)), Entity(2, "Character", "Beryl Garcia", ("Beryl", 15000)), FactNode(10, "A fact."),
         .. more.Select((m, i) => FactNode(11 + i, m.Text))],
        [About(20, 10, 1, 150), About(21, 10, 2, 150), .. more.Select((m, i) => About(30 + i, 11 + i, 1, m.Revealed))],
        new Dictionary<int, (int, int)> { [1] = (0, 10_000), [2] = (10_000, 20_000) });

    [Fact]
    public void Late_rows_are_suppressed_with_spoilers_off_and_counted()
    {
        // The late row is dropped and counted; a row without a late entity stays.
        var graph = LateGraph(("Another fact.", 200));
        var tools = Tools(graph, chapter: 1);
        Assert.Equal("Another fact. · Stapleton · ch. 1", tools.Run("facts_about", "Stapleton"));
        var call = tools.Calls.Single();
        Assert.Equal(1, call.Rows);
        Assert.Equal(1, call.LateRowsSuppressed);
        Assert.Contains("→ 1 row (1 late row hidden)", ChatCommand.Describe(call));

        // Only late rows: nothing known.
        var only = Tools(LateGraph(), chapter: 1);
        Assert.Equal(ChatTools.Nothing, only.Run("facts_about", "Stapleton"));
        Assert.Equal((0, 1), (only.Calls.Single().Rows, only.Calls.Single().LateRowsSuppressed));

        // Spoilers on: nothing changes (canonical names, nothing dropped). Chapter 2: "Beryl" is mentioned, the row is not late.
        var on = Tools(graph, chapter: 1, spoilers: true);
        Assert.Equal("A fact. · Stapleton, Beryl Garcia · ch. 1\nAnother fact. · Stapleton · ch. 1", on.Run("facts_about", "Stapleton"));
        Assert.Equal(0, on.Calls.Single().LateRowsSuppressed);
        Assert.Equal("A fact. · Stapleton, Beryl · ch. 1\nAnother fact. · Stapleton · ch. 1", Tools(graph, chapter: 2).Run("facts_about", "Stapleton"));
    }

    [Fact]
    public void Knowledge_pack_drops_late_rows_with_spoilers_off()
    {
        var (text, sections) = Tools(LateGraph(), chapter: 1).KnowledgePack("Stapleton", "Stapleton");
        Assert.Contains("## About yourself\n(nothing)\n", text);
        Assert.DoesNotContain("Beryl Garcia", text);
        var about = sections.Single(s => s.Tool == "facts_about");
        Assert.Equal((0, 1), (about.Rows, about.LateRowsSuppressed));

        var (on, onSections) = Tools(LateGraph(), chapter: 1, spoilers: true).KnowledgePack("Stapleton", "Stapleton");
        Assert.Contains("## About yourself\nA fact. · Stapleton, Beryl Garcia · ch. 1\n", on);
        Assert.All(onSections, s => Assert.Equal(0, s.LateRowsSuppressed));
    }

    [Fact]
    public void The_cap_counts_rows_after_late_rows_are_dropped()
    {
        var more = Enumerable.Range(1, 3).Select(i => ($"F{i}", 200L + i)).ToArray();
        var log = new List<ToolCallRecord>();
        var lines = Lines(Tools(LateGraph(more), chapter: 1, maxRows: 3, log: log).Run("facts_about", "Stapleton"));
        Assert.Equal(["F1 · Stapleton · ch. 1", "F2 · Stapleton · ch. 1", "F3 · Stapleton · ch. 1"], lines);
        Assert.False(log.Single().Truncated);
    }

    [Fact]
    public void Knowledge_pack_counts_late_rows_and_shows_display_names()
    {
        var graph = StapletonGraph((1, 0, 2000), (2, 2000, 100000));
        var (text, sections) = Tools(graph, chapter: 1).KnowledgePack("the lady", "the lady");
        Assert.StartsWith("What you, the lady, know at this point", text);
        Assert.Contains("## About yourself\nShe lives with her brother. · the lady, Mr. Stapleton · ch. 1\n", text);
        Assert.All(sections, s => Assert.Equal(0, s.LateRowsSuppressed));
    }

    [Fact]
    public void Hints_list_display_names()
    {
        var graph = new BookGraph(
            [Entity(1, "Character", "Beryl Garcia", ("the lady", 100), ("Beryl Garcia", 90000)), Entity(2, "Location", "Grimpen Mire", ("Grimpen", 200), ("Grimpen Mire", 70000))],
            [], new Dictionary<int, (int, int)> { [1] = (0, 10_000), [10] = (80_000, 100_000) });
        Assert.EndsWith("Known names: the lady.", Tools(graph, chapter: 1).Run("facts_about", "Irene Adler"));
        Assert.EndsWith("Known names: Beryl Garcia.", Tools(graph, chapter: 1, spoilers: true).Run("facts_about", "Irene Adler"));
        Assert.EndsWith("Known places: Grimpen.", Tools(graph, chapter: 1).Run("at_location", "Baker Street"));
    }

    [Fact]
    public void Recent_first_cap_keeps_the_latest_facts()
    {
        var nodes = new List<NodeValue> { Entity(1, "Character", "Holmes", ("Holmes", 0)) };
        nodes.AddRange(Enumerable.Range(1, 100).Select(i => FactNode(100 + i, $"F{i}")));
        var edges = Enumerable.Range(1, 100).Select(i => About(1000 + i, 100 + i, 1, i * 100L));
        var graph = new BookGraph(nodes, edges, new Dictionary<int, (int, int)> { [1] = (0, 5_000), [2] = (5_000, 20_000) });
        var log = new List<ToolCallRecord>();

        var lines = Lines(Tools(graph, chapter: 2, maxRows: 80, log: log).Run("facts_about", "Holmes"));

        Assert.Equal(81, lines.Length);
        Assert.Equal("F21 · Holmes · ch. 1", lines[0]);   // revealed at 2100
        Assert.Equal("F50 · Holmes · ch. 2", lines[29]);  // revealed at 5000, where chapter 2 starts
        Assert.Equal("F100 · Holmes · ch. 2", lines[79]);
        Assert.Equal("(last 80 of 100 rows; earlier rows omitted)", lines[80]);
        Assert.True(log.Single().Truncated);
    }

    [Fact]
    public void Chapter_stamp_is_the_chapter_holding_the_offset()
    {
        var graph = new BookGraph([], [], new Dictionary<int, (int, int)> { [1] = (100, 1000), [2] = (1000, 3000) });
        Assert.Equal("ch. 1", ChatTools.ChapterStamp(graph, 100));
        Assert.Equal("ch. 1", ChatTools.ChapterStamp(graph, 999));
        Assert.Equal("ch. 2", ChatTools.ChapterStamp(graph, 1000));
        Assert.Equal("ch. 2", ChatTools.ChapterStamp(graph, 5000));
        Assert.Equal("before ch. 1", ChatTools.ChapterStamp(graph, 50));
    }

    [Fact]
    public void Max_rows_is_80()
    {
        Assert.Equal(80, Template().GetValue<int>("Chat:MaxRows"));
        Assert.Equal(80, ChatCommand.DefaultMaxRows);
    }

    [Fact]
    public void Unknown_name_is_retried_without_its_article_and_with_s_added_or_removed()
    {
        var graph = new BookGraph(
            [
                Entity(1, "Character", "the hounds", ("the hounds", 100)),
                Entity(2, "Location", "Baker Street", ("Baker Street", 50)),
                FactNode(10, "A legend of a hound."),
            ],
            [About(20, 10, 1, 150), new(21, "LOCATED_AT", 1, 2, new Dictionary<string, object?> { ["revealedPos"] = 160L })],
            new Dictionary<int, (int, int)> { [1] = (0, 10_000) });
        var log = new List<ToolCallRecord>();
        var tools = Tools(graph, chapter: 1, log: log);

        Assert.Equal("A legend of a hound. · the hounds · ch. 1", tools.Run("facts_about", "the hound"));
        Assert.Equal("A legend of a hound. · the hounds · ch. 1", tools.Run("facts_about", "Hound"));
        Assert.Equal("the hounds ·  · null", tools.Run("at_location", "the Baker Street"));
        Assert.Equal(["the hounds", "the hounds", "Baker Street"], log.Select(c => c.ResolvedName));
        Assert.All(log, c => Assert.False(c.UnknownName));
        Assert.Equal("the hound", log[0].Args["character"]);
        Assert.Contains("(resolved as \"the hounds\")", ChatCommand.Describe(log[0]));

        // Only facts_about and at_location retry.
        Assert.Contains("No character goes by", tools.Run("relations", "the hound"));
    }

    [Fact]
    public void Retry_needs_exactly_one_matching_entity()
    {
        var graph = new BookGraph(
            [Entity(1, "Character", "the Hound of the Baskervilles", ("the hound", 100)), Entity(2, "Character", "the hounds", ("the hounds", 120))],
            [], new Dictionary<int, (int, int)> { [1] = (0, 10_000) });
        var log = new List<ToolCallRecord>();
        var text = Tools(graph, chapter: 1, log: log).Run("facts_about", "hound");

        Assert.StartsWith(ChatTools.Nothing + "\nNo character goes by \"hound\".", text);
        // Both contain the word (one as "hounds"): the hint lists them.
        Assert.EndsWith("Names that contain it: the hound, the hounds.", text);
        Assert.Null(log.Single().ResolvedName);
        Assert.True(log.Single().UnknownName);
    }

    [Fact]
    public void Unknown_name_is_matched_as_a_whole_word_inside_one_alias()
    {
        // "hound" → "Hound of the Baskervilles", the only alias containing the word ("Houndsworth" doesn't).
        var graph = new BookGraph(
            [
                Entity(1, "Character", "Hound of the Baskervilles", ("Hound of the Baskervilles", 100)),
                Entity(2, "Character", "Mr. Houndsworth", ("Houndsworth", 120)),
                FactNode(10, "The hound is spectral."),
            ],
            [About(20, 10, 1, 150)],
            new Dictionary<int, (int, int)> { [1] = (0, 10_000) });
        var log = new List<ToolCallRecord>();
        var tools = Tools(graph, chapter: 1, log: log);

        Assert.Equal("The hound is spectral. · Hound of the Baskervilles · ch. 1", tools.Run("facts_about", "hound"));
        Assert.Equal("The hound is spectral. · Hound of the Baskervilles · ch. 1", tools.Run("facts_about", "the Hound"));
        Assert.Equal(["Hound of the Baskervilles", "Hound of the Baskervilles"], log.Select(c => c.ResolvedName));
        Assert.All(log, c => Assert.False(c.UnknownName));
        Assert.Contains("(resolved as \"Hound of the Baskervilles\")", ChatCommand.Describe(log[0]));

        // Not a whole word: no match, the usual hint.
        Assert.Contains("No character goes by \"Houndsw\". Known names:", tools.Run("facts_about", "Houndsw"));
    }

    [Fact]
    public void Containment_uses_only_names_mentioned_by_pos_with_spoilers_off()
    {
        // The alias is first spelled in chapter 2: at chapter 1 with spoilers off "hound" reaches nothing.
        var graph = new BookGraph(
            [Entity(1, "Character", "Hound of the Baskervilles", ("the beast", 100), ("Hound of the Baskervilles", 15_000)), FactNode(10, "The hound is spectral.")],
            [About(20, 10, 1, 150)],
            new Dictionary<int, (int, int)> { [1] = (0, 10_000), [2] = (10_000, 20_000) });
        var off = Tools(graph, chapter: 1);
        Assert.StartsWith(ChatTools.Nothing + "\nNo character goes by \"hound\". Known names: the beast.", off.Run("facts_about", "hound"));
        Assert.Null(off.Calls.Single().ResolvedName);

        var on = Tools(graph, chapter: 1, spoilers: true);
        Assert.Equal("The hound is spectral. · Hound of the Baskervilles · ch. 1", on.Run("facts_about", "hound"));
        Assert.Equal("Hound of the Baskervilles", on.Calls.Single().ResolvedName);
        Assert.Equal("The hound is spectral. · the beast · ch. 1", Tools(graph, chapter: 2).Run("facts_about", "hound"));
    }

    // ---- Ambiguous aliases, whole-word search, per-term caps ----

    [Fact]
    public void An_alias_shared_with_another_entity_is_skipped_unless_it_is_the_only_one_revealed()
    {
        // Two entities with the same alias "Baskerville"; and Sir Henry's surname inside Sir Charles's name.
        var henry = Entity(1, "Character", "Sir Henry Baskerville", ("Baskerville", 100), ("Mr. Henry Baskerville", 300), ("Sir Henry Baskerville", 5000));
        var charles = Entity(2, "Character", "Sir Charles Baskerville", ("Sir Charles Baskerville", 50), ("Sir Charles", 60));
        var hugo = Entity(3, "Character", "Hugo Baskerville", ("Baskerville", 20), ("Hugo", 40));
        var graph = new BookGraph([henry, charles, hugo, Lady with { Id = 9 }], [], new Dictionary<int, (int, int)> { [1] = (0, 200), [2] = (200, 10_000) });

        var ambiguous = ChatTools.AmbiguousAliases(graph);
        Assert.Equal(["Baskerville"], ambiguous.Order());
        Assert.Equal(("Baskerville", false), ChatTools.DisplayName(henry, 200, false, ambiguous));          // the only one revealed: kept
        Assert.Equal(("Mr. Henry Baskerville", false), ChatTools.DisplayName(henry, 300, false, ambiguous)); // the earliest unambiguous
        Assert.Equal(("Hugo", false), ChatTools.DisplayName(hugo, 300, false, ambiguous));
        Assert.Equal(("Baskerville", false), ChatTools.DisplayName(henry, 300, false));                     // without the set: the first-mention rule
        Assert.Equal(("Sir Henry Baskerville", false), ChatTools.DisplayName(henry, 300, true, ambiguous)); // spoilers: canonical
        // The late-name fixture keeps its names (no shared words with another entity here).
        Assert.Equal(("the lady", false), ChatTools.DisplayName(Lady, 20000, false, ambiguous));
        // The tools use the set: rows name Sir Henry by his unambiguous alias.
        Assert.Equal("Mr. Henry Baskerville", Tools(graph, chapter: 2).DisplayNameOf(1).Name);
    }

    [Fact]
    public void Promoted_hound_names_sir_henry_by_an_unambiguous_alias()
    {
        // The promoted graph: Sir Henry (first named in ch. 3) goes by "Baskerville" too, which other characters share.
        var (_, graph) = HoundDb();
        var ambiguous = ChatTools.AmbiguousAliases(graph);
        Assert.Contains("Baskerville", ambiguous);
        Assert.Contains("Stapleton", ambiguous);
        var henry = graph.Nodes.Single(n => n.Properties.GetValueOrDefault("name") as string == "Sir Henry Baskerville");
        Assert.Equal(("Sir Henry", false), ChatTools.DisplayName(henry, graph.Chapters[3].CharEnd, false, ambiguous));
        Assert.Equal(("Sir Henry", false), ChatTools.DisplayName(henry, graph.Chapters[8].CharEnd, false, ambiguous));
        Assert.True(ChatTools.DisplayName(henry, graph.Chapters[2].CharEnd, false, ambiguous).Late);   // not yet mentioned
    }

    private static BookGraph SearchGraph(int hounds) => new(
        [
            Entity(1, "Character", "Holmes", ("Holmes", 10)),
            FactNode(2, "Mortimer gave a presentation of the stick."),
            FactNode(3, "The letter was sent to the hotel."),
            .. Enumerable.Range(0, hounds).Select(i => FactNode(100 + i, $"The hound howled, night {i}.")),
        ],
        [
            About(20, 2, 1, 100), About(21, 3, 1, 200),
            .. Enumerable.Range(0, hounds).Select(i => About(1000 + i, 100 + i, 1, 300L + i)),
        ],
        new Dictionary<int, (int, int)> { [1] = (0, 10_000) });

    [Fact]
    public void Search_matches_whole_words_only()
    {
        var tools = Tools(SearchGraph(0), chapter: 1);
        Assert.Equal("The letter was sent to the hotel. · Holmes · ch. 1", tools.Run("search_facts", "sent"));   // not "presentation"
        Assert.Equal(ChatTools.Nothing, tools.Run("search_facts", "hote"));
        Assert.Equal("The letter was sent to the hotel. · Holmes · ch. 1", tools.Run("search_facts", "HOTEL"));   // case-insensitive
        Assert.Matches(ChatTools.WholeWord("stapleton"), "Stapleton's wife");
        Assert.DoesNotMatch(ChatTools.WholeWord("sent"), "presentation");
    }

    [Fact]
    public void A_term_with_more_than_the_cap_contributes_its_most_recent_rows_and_the_ids_follow_the_rows_shown()
    {
        var log = new List<ToolCallRecord>();
        var tools = new ChatTools(SearchGraph(50), ChatTools.LoadQueries(QueriesDir), 1, false, 80, log.Add, searchMaxPerTerm: 40);
        var lines = Lines(tools.Run("search_facts", "hound"));
        Assert.Equal(41, lines.Length);
        Assert.Equal("The hound howled, night 10. · Holmes · ch. 1", lines[0]);   // nights 10…49: the 40 most recent, in reveal order
        Assert.Equal("(term \"hound\" matched 50 rows; showing the 40 most recent)", lines[^1]);
        var call = log.Single();
        Assert.Equal(40, call.Rows);
        Assert.Equal(41, call.NodeIdList.Count);   // 40 facts + Holmes
        Assert.DoesNotContain(100L, call.NodeIdList);
        Assert.Equal(40, call.EdgeIdList.Count);
        Assert.Equal(40, ChatSettings.From(Template()).SearchMaxPerTerm);
    }

    // ---- The ids of the rows the model saw ----

    [Fact]
    public void Tool_calls_carry_the_node_and_edge_ids_of_their_rows_and_never_show_them()
    {
        var log = new List<ToolCallRecord>();
        var tools = Tools(QueryFixture.Graph, chapter: 2, log: log);

        var text = tools.Run("facts_about", "Holmes");
        Assert.DoesNotContain("103", text);
        Assert.DoesNotContain("105", text);
        var call = log.Single();
        Assert.Equal([1L, 3, 6, 7], call.NodeIdList.Order());   // the facts and everything they are about
        Assert.Equal([103L, 105], call.EdgeIdList.Order());     // the ABOUT edges to Holmes

        tools.Run("who_met", "Holmes");
        Assert.Equal([2L, 3], log[1].NodeIdList.Order());
        Assert.Equal([101L, 102], log[1].EdgeIdList.Order());
        tools.Run("events_for", "Holmes");
        Assert.Equal([8L], log[2].NodeIdList);
        Assert.Equal([107L], log[2].EdgeIdList);
    }

    [Fact]
    public void Search_ids_cover_facts_events_what_they_are_about_and_their_edges()
    {
        var log = new List<ToolCallRecord>();
        Tools(QueryFixture.Graph, chapter: 2, log: log).Run("search_facts", "Mortimer Baker");
        var call = log.Single();
        Assert.Equal([1L, 3, 4, 7, 8], call.NodeIdList.Order());
        Assert.Equal([104L, 105, 106, 107, 109], call.EdgeIdList.Order());
    }

    [Fact]
    public void Ids_of_hidden_late_rows_are_left_out()
    {
        var log = new List<ToolCallRecord>();
        Tools(LateGraph(("Another fact.", 200)), chapter: 1, log: log).Run("facts_about", "Stapleton");
        var call = log.Single();
        Assert.Equal(1, call.LateRowsSuppressed);
        Assert.DoesNotContain(10L, call.NodeIdList);   // "A fact." names Beryl Garcia, not mentioned yet
        Assert.DoesNotContain(2L, call.NodeIdList);
        Assert.Contains(11L, call.NodeIdList);
    }

    [Fact]
    public void Id_columns_are_recognised_by_their_suffix()
    {
        Assert.True(ChatTools.IsIdColumn("aboutIds"));
        Assert.True(ChatTools.IsIdColumn("edgeId"));
        Assert.True(ChatTools.IsEdgeIdColumn("edgeIds"));
        Assert.False(ChatTools.IsEdgeIdColumn("factId"));
        Assert.False(ChatTools.IsIdColumn("revealed"));
    }

    // ---- search_facts ----

    [Fact]
    public void Search_terms_drop_stop_words_short_words_and_punctuation()
    {
        Assert.Equal(["killer"], ChatTools.SearchTerms("Who is the killer?"));
        Assert.Equal(["murder", "hound", "charles"], ChatTools.SearchTerms("murder, hound; Sir Charles"));
        Assert.Equal(["grimpen", "mire"], ChatTools.SearchTerms("\"Grimpen Mire\" grimpen"));
        Assert.Empty(ChatTools.SearchTerms("who is the one of it"));
    }

    [Fact]
    public void Search_ranks_by_matched_terms_then_reveal_order_and_renders_like_facts_about()
    {
        var log = new List<ToolCallRecord>();
        var tools = Tools(QueryFixture.Graph, chapter: 2, log: log);

        // "baker" and "mortimer" both match the event (text + place); "holmes" and "mortimer" match the stick fact once each.
        var lines = Lines(tools.Run("search_facts", "Mortimer Baker"));
        Assert.Equal("Mortimer visits Baker Street. · Dr. Mortimer, Sherlock Holmes, Baker Street · ch. 1", lines[0]);
        Assert.Equal("Mortimer's stick dates from 1884. · Dr. Mortimer, Sherlock Holmes · ch. 1", lines[1]);
        Assert.Equal(2, lines.Length);

        var call = log.Single();
        Assert.Equal("search_facts", call.Tool);
        Assert.Equal("Mortimer Baker", call.Args["keywords"]);
        Assert.Equal(2, call.Rows);
        Assert.Equal("search_facts(keywords: \"Mortimer Baker\") → 2 rows", ChatCommand.Describe(call));

        // One term: reveal order (Holmes's late mornings at 12 before the stick at 510).
        Assert.Equal(["Holmes is late in the mornings. · Sherlock Holmes · ch. 1", "Mortimer's stick dates from 1884. · Dr. Mortimer, Sherlock Holmes · ch. 1",
            "Mortimer visits Baker Street. · Dr. Mortimer, Sherlock Holmes, Baker Street · ch. 1"], Lines(tools.Run("search_facts", "holmes")));
        Assert.Equal(ChatTools.Nothing, tools.Run("search_facts", "moriarty"));
    }

    [Fact]
    public void Search_with_only_stop_words_gives_the_hint()
    {
        var log = new List<ToolCallRecord>();
        Assert.Equal(ChatTools.Nothing + "\n" + ChatTools.SearchHint, Tools(QueryFixture.Graph, chapter: 2, log: log).Run("search_facts", "who is the one"));
        Assert.Contains("give 2–4 content words", ChatTools.SearchHint);
        Assert.Equal(0, log.Single().Rows);
    }

    [Fact]
    public void Search_respects_the_gate_and_drops_late_rows_with_spoilers_off()
    {
        // The late-name fixture: "A fact." names Beryl Garcia, first mentioned in ch. 2: hidden at ch. 1 off, shown with spoilers or at ch. 2.
        var graph = LateGraph(("Another fact.", 200));
        var off = Tools(graph, chapter: 1);
        Assert.Equal("Another fact. · Stapleton · ch. 1", off.Run("search_facts", "fact"));
        Assert.Equal((1, 1), (off.Calls.Single().Rows, off.Calls.Single().LateRowsSuppressed));
        // Searching by the later name finds nothing visible, and the name is not echoed back in rows.
        Assert.Equal(ChatTools.Nothing, off.Run("search_facts", "Garcia"));
        Assert.Equal("A fact. · Stapleton, Beryl Garcia · ch. 1\nAnother fact. · Stapleton · ch. 1", Tools(graph, chapter: 1, spoilers: true).Run("search_facts", "fact"));
        Assert.Equal("A fact. · Stapleton, Beryl · ch. 1\nAnother fact. · Stapleton · ch. 1", Tools(graph, chapter: 2).Run("search_facts", "fact"));
        // Nothing revealed after $pos.
        Assert.Equal(ChatTools.Nothing, Tools(LateGraph(("Later fact.", 15_000)), chapter: 1).Run("search_facts", "later"));
    }

    [Fact]
    public void Search_finds_the_promoted_hound_murder_facts_at_chapter_15()
    {
        var (path, graph) = HoundDb();
        var tools = new ChatTools(graph, ChatTools.LoadQueries(HoundQueriesDir), 15, false, 80, locations: ChatCharacters.LoadLocations(path));
        var text = tools.Run("search_facts", "murder Stapleton");
        Assert.Contains("Stapleton is planning murder, refined, cold-blooded and deliberate.", text);
        // "stapleton" matches hundreds of facts, so it contributes only its 40 most recent and says so; "murder" keeps all of its own.
        Assert.StartsWith("Stapleton's attempt upon Sir Henry that night did not help prove murder against him.", Lines(text)[0]);   // the row both words match comes first
        Assert.Matches("\\(term \"stapleton\" matched \\d{3} rows; showing the 40 most recent\\)$", text);
    }

    // ---- Avatar slugs ----

    [Theory]
    [InlineData("Dr. Watson", "dr-watson")]
    [InlineData("Sir Henry Baskerville", "sir-henry-baskerville")]
    [InlineData("Miss Stapleton", "miss-stapleton")]
    [InlineData("  d'Artagnan, the Gascon!  ", "d-artagnan-the-gascon")]
    [InlineData("Mme. Montpensier & Mlle. Carère", "mme-montpensier-mlle-carère")]
    [InlineData("d'Artagnan", "d-artagnan")]   // Books.json's straight apostrophe …
    [InlineData("D’Artagnan", "d-artagnan")]   // … and the graph's curly one (U+2019) make the same file name
    public void Slug_lowercases_and_turns_runs_of_other_characters_into_one_hyphen(string name, string expected) =>
        Assert.Equal(expected, ChatCharacters.Slug(name));

    // Every book with avatars (all three); each roster name's slug is a file in the folder, and the folder holds nothing else.
    [Theory]
    [InlineData(Hound, "books/hound-of-the-baskervilles/characters")]
    [InlineData("three-musketeers", "books/three-musketeers/characters")]
    [InlineData("treasure-island", "books/treasure-island/characters")]
    public void Every_roster_name_has_its_avatar_file(string bookId, string avatars)
    {
        var root = RepoPaths.FindRoot();
        var book = BookManifest.Read(Path.Combine(root, "data", "books", BookManifest.FileName)).Single(b => b.Id == bookId);
        Assert.Equal(avatars, book.Avatars);
        var folder = Path.Combine(root, "web", "public", book.Avatars!.Replace('/', Path.DirectorySeparatorChar));
        var files = Directory.GetFiles(folder, "*.webp").Select(Path.GetFileNameWithoutExtension).Order().ToList();
        Assert.Equal(8, book.Chat!.Count);
        Assert.Equal(files, book.Chat!.Select(ChatCharacters.Slug).Order());
    }

    [Theory]
    [InlineData("Mr. Sherlock Holmes", "SH")]
    [InlineData("Dr. Watson", "W")]
    [InlineData("the lady", "L")]
    [InlineData("Sir Henry Baskerville", "HB")]
    [InlineData("Miss", "M")]
    [InlineData("Mme. Bonacieux", "B")]
    [InlineData("M. de Tréville", "T")]
    [InlineData("Anne of Austria", "AA")]
    [InlineData("D’Artagnan", "D")]
    [InlineData("Captain Smollett", "S")]
    [InlineData("Squire Trelawney", "T")]
    [InlineData("Master Billy Bones", "BB")]
    [InlineData("Cardinal Richelieu", "R")]
    public void Initials_skip_titles(string name, string expected) => Assert.Equal(expected, ChatCharacters.DeriveInitials(name));

    [Fact]
    public void Resolved_name_and_late_rows_are_written_to_chat_jsonl()
    {
        using var root = new TempRoot();
        var run = RunFolder.Create(Path.Combine(root.Path, "runs"));
        new ChatJsonl(run).Tool(new ToolCallRecord(1, "facts_about", new Dictionary<string, string> { ["character"] = "the hound" }, 3, 1.5, false, false, "the hounds", 2), 1000, false);

        var line = JsonNode.Parse(File.ReadAllLines(run.File(ChatJsonl.FileName)).Single())!;
        Assert.Equal("the hounds", line["resolvedName"]!.GetValue<string>());
        Assert.Equal(2, line["lateRowsSuppressed"]!.GetValue<int>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Prompt_ends_with_the_three_tg23_rules_then_the_length_rule(bool knowledgePack)
    {
        var book = new BookEntry(Hound, "The Hound of the Baskervilles", "Arthur Conan Doyle");
        Assert.EndsWith(
            // The length rule is the last paragraph; the grounding rules end the rules before it.
            "\"Every character known so far\" lists names you have heard of, not people you have met. Say you have met someone only if a \"People you have met\" row or a who_met result says so.\n"
            + "Say only what the rows say. Do not add how a place looks, what a thing is for, or what a person is like unless a row states it. If you would add such a detail, leave it out.\n"
            + "When a row names you, speak of yourself in the first person.\n"
            + "If a question does not name a person or place you can look up — who did something, what happened, why, who is responsible — call search_facts with two to four key words from the question before answering, and then look up any person it names.\n\n"
            // The length rule.
            + "Answer the question that was asked and nothing more: two to four sentences, one short paragraph at most. Do not recount other things the tools returned unless they are needed to answer. If the reader asks for the whole story or for details, you may use up to three short paragraphs.",
            ChatPrompt.Fill("Mr. Sherlock Holmes", book, 3, "proper Victorian British English", knowledgePack));
        Assert.DoesNotContain("Answer in at most three short paragraphs.", ChatPrompt.Template);
        Assert.DoesNotContain("Manner of speaking:", ChatPrompt.Fill("Mr. Sherlock Holmes", book, 3, "proper Victorian British English", knowledgePack));
    }

    // ---- Voices ----

    [Fact]
    public void The_voice_is_the_prompts_last_paragraph()
    {
        // Moved from after the register sentence to its own final paragraph; before the length rule, which is last.
        var book = new BookEntry(Hound, "The Hound of the Baskervilles", "Arthur Conan Doyle");
        var prompt = ChatPrompt.Fill("Barrymore", book, 7, "proper Victorian British English", voice: "  Formal and spare.  ");
        Assert.EndsWith("look up any person it names.\n\nManner of speaking: Formal and spare. Keep this manner in every answer.\n\n" + ChatPrompt.LengthRule, prompt);
        Assert.Contains("Speak in the first person, in proper Victorian British English. Stay in character;", prompt);
        Assert.Equal(ChatPrompt.Fill("Barrymore", book, 7, "x", knowledgePack: true).Replace("\n\n" + ChatPrompt.LengthRule,
                "\n\nManner of speaking: Formal and spare. Keep this manner in every answer.\n\n" + ChatPrompt.LengthRule),
            ChatPrompt.Fill("Barrymore", book, 7, "x", knowledgePack: true, voice: "Formal and spare."));
        Assert.Equal(ChatPrompt.Fill("Barrymore", book, 7, "x"), ChatPrompt.Fill("Barrymore", book, 7, "x", voice: " "));
    }

    [Fact]
    public void The_hound_voices_are_keyed_by_roster_name_and_resolve_to_their_characters()
    {
        var (path, graph) = HoundDb();
        var characters = ChatCharacters.Load(path, graph);
        var book = BookManifest.Read(Path.Combine(RepoPaths.FindRoot(), "data", "books", BookManifest.FileName)).Single(b => b.Id == Hound);
        Assert.Equal(book.Chat!.Order(), book.Voices!.Keys.Order());
        Assert.StartsWith("A butler's formal, deferential speech", ChatCharacters.Voice(book, characters, "Barrymore"));
        Assert.StartsWith("Crisp, precise, faintly amused", ChatCharacters.Voice(book, characters, "Sherlock Holmes"));
        Assert.Null(ChatCharacters.Voice(book, characters, "Selden"));
        Assert.Null(ChatCharacters.Voice(book with { Voices = null }, characters, "Barrymore"));
        // Manner only: no voice line names a character or place of the book.
        var names = graph.Nodes.Where(n => n.Labels.Contains("Character") || n.Labels.Contains("Location"))
            .SelectMany(n => ((IReadOnlyList<object?>?)n.Properties.GetValueOrDefault("aliases") ?? []).OfType<string>()).Where(n => n.Length > 3).Distinct().ToList();
        foreach (var voice in book.Voices!.Values)
            Assert.All(names, name => Assert.DoesNotMatch(@"\b" + Regex.Escape(name) + @"\b", voice));
    }

    [Fact]
    public async Task The_chat_command_sends_the_characters_voice()
    {
        using var root = new TempRoot();
        using var server = new FakeLlmServer();
        var books = Path.Combine(root.Path, "data", "books");
        BookManifest.Write(Path.Combine(books, BookManifest.FileName),
            [new BookEntry(Hound, "The Hound of the Baskervilles", "Arthur Conan Doyle", ["Barrymore"], Voices: new Dictionary<string, string> { ["Barrymore"] = "Formal and spare." })]);
        var settings = new ChatCommand.Settings { Book = Hound, Chapter = 7, Character = "Barrymore", Message = "Who are you?" };

        Assert.Equal(0, await ChatCommand.RunAsync(settings, Config(server), root.Path, TextWriter.Null, TestContext.Current.CancellationToken));

        var system = JsonNode.Parse(server.Requests[0].Body)!["messages"]![0]!["content"]!.GetValue<string>();
        Assert.EndsWith("\n\nManner of speaking: Formal and spare. Keep this manner in every answer.\n\n" + ChatPrompt.LengthRule, system);
    }

    [Fact]
    public async Task Chat_llm_picks_the_characters_model_profile_and_llm_overrides_it()
    {
        // Chat:Llm names a profile as Indexing:Llm does; --llm still wins; unset → the Llm section.
        using var root = new TempRoot();
        using var server = new FakeLlmServer();
        string[] profile = ["Llm:Profiles:{0}:Type", "OpenAI", "Llm:Profiles:{0}:Endpoint", server.BaseUrl + "/openai/v1/", "Llm:Profiles:{0}:Model", "{0}-deployment"];
        (string, string)[] Profile(string name) => [(string.Format(profile[0], name), profile[1]), (string.Format(profile[2], name), profile[3]), (string.Format(profile[4], name), string.Format(profile[5], name))];
        var config = Config(server, [("Chat:Llm", "chatmodel"), .. Profile("chatmodel"), .. Profile("other")]);

        Assert.Equal(0, await ChatCommand.RunAsync(new ChatCommand.Settings { Book = Hound, Chapter = 3, Character = "Holmes", Message = "Who?" }, config, root.Path, TextWriter.Null, TestContext.Current.CancellationToken));
        Assert.Contains("\"model\":\"chatmodel-deployment\"", server.Requests[^1].Body.Replace(" ", ""));
        Assert.Equal(0, await ChatCommand.RunAsync(new ChatCommand.Settings { Book = Hound, Chapter = 3, Character = "Holmes", Message = "Who?", Llm = "other" }, config, root.Path, TextWriter.Null, TestContext.Current.CancellationToken));
        Assert.Contains("\"model\":\"other-deployment\"", server.Requests[^1].Body.Replace(" ", ""));
        Assert.Equal(0, await ChatCommand.RunAsync(new ChatCommand.Settings { Book = Hound, Chapter = 3, Character = "Holmes", Message = "Who?" }, Config(server), root.Path, TextWriter.Null, TestContext.Current.CancellationToken));
        Assert.Contains("\"model\":\"my-deployment\"", server.Requests[^1].Body.Replace(" ", ""));

        Assert.Null(ChatSettings.LlmProfile(Template()));   // the committed template leaves it unset
        Assert.Equal("", Template()["Chat:Llm"]);
    }

    [Fact]
    public void Query_header_is_stripped()
    {
        Assert.Equal("MATCH (n) RETURN n", ChatTools.StripHeader("// one\r\n// two\r\nMATCH (n) RETURN n\r\n"));
    }

    // ---- Characters, script, prompt ----

    private static (string Path, BookGraph Graph) HoundDb()
    {
        var path = BookDatabase.EmbeddedCopy(BookResources.Assembly, BookResources.DatabaseResourceName(new BookEntry(Hound, "", "")));
        return (path, BookGraph.Load(path));
    }

    [Fact]
    public void Character_resolves_canonical_name_alias_and_unknown()
    {
        var (path, graph) = HoundDb();
        var characters = ChatCharacters.Load(path, graph);

        Assert.Equal("Sherlock Holmes", characters[0].Name);
        Assert.Equal("Sherlock Holmes", ChatCharacters.Resolve(characters, "Mr. Sherlock Holmes"));
        Assert.Equal("Sherlock Holmes", ChatCharacters.Resolve(characters, "Sherlock Holmes"));
        Assert.Equal("Sherlock Holmes", ChatCharacters.Resolve(characters, "holmes"));
        Assert.Equal("Dr. Watson", ChatCharacters.Resolve(characters, "Watson"));
        Assert.Null(ChatCharacters.Resolve(characters, "Irene Adler"));
    }

    [Fact]
    public void Script_skips_blank_lines_and_comments()
    {
        Assert.Equal(["Who is Dr. Mortimer?", "Who is Stapleton?"],
            ChatScript.Parse("# a comment\r\n\r\nWho is Dr. Mortimer?\n   \n  # indented comment\nWho is Stapleton?\n"));
    }

    [Fact]
    public void Committed_script_has_the_ten_questions()
    {
        var file = Path.Combine(RepoPaths.FindRoot(), "scripts", Hound, "ch03-holmes.txt");
        var questions = ChatScript.Parse(File.ReadAllText(file));
        Assert.Equal(10, questions.Count);
        Assert.Equal("Who is Dr. Mortimer?", questions[0]);
        Assert.Equal("Who do you suspect?", questions[^1]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Prompt_has_title_author_chapter_and_register_and_no_other_proper_names(bool knowledgePack)
    {
        var (_, graph) = HoundDb();
        var book = new BookEntry(Hound, "The Hound of the Baskervilles", "Arthur Conan Doyle");
        var register = ChatPrompt.Register(Template(), Hound);
        var prompt = ChatPrompt.Fill("Sherlock Holmes", book, 3, register, knowledgePack);

        // The two rules follow "Everything you know comes from the tools." only with the pack.
        const string rules = "Everything you know comes from the tools.\n"
            + "The record of what you know is given to you at the start. For any person, place or thing not covered by that record, call the tools before answering.\n"
            + "If neither the record nor the tools mention it, you have not heard of it: say so in character. Do not fill the gap with anything else you may know about the story.\n"
            + "Before answering a question about people";
        if (knowledgePack) Assert.Contains(rules, prompt);
        else
        {
            Assert.DoesNotContain("The record of what you know", prompt);
            Assert.Contains("Everything you know comes from the tools. Before answering", prompt);
        }

        Assert.Equal("proper Victorian British English", register);
        Assert.Contains("You are Sherlock Holmes, a character in \"The Hound of the Baskervilles\" by Arthur Conan Doyle.", prompt);
        Assert.Contains("up to the end of chapter 3.", prompt);
        Assert.Contains("in proper Victorian British English.", prompt);
        Assert.DoesNotContain("{", prompt);

        // No character or place name of the book other than the one filled in (word match, case-sensitive).
        var holmes = graph.Nodes.Single(n => Equals(n.Properties.GetValueOrDefault("name"), "Sherlock Holmes"));
        var own = ((IReadOnlyList<object?>)holmes.Properties["aliases"]!).OfType<string>().Append("Sherlock Holmes").ToHashSet();
        var names = graph.Nodes.Where(n => n.Labels.Contains("Character") || n.Labels.Contains("Location"))
            .SelectMany(n => ((IReadOnlyList<object?>?)n.Properties.GetValueOrDefault("aliases") ?? []).OfType<string>().Append((string)n.Properties["name"]!))
            .Where(name => !own.Contains(name)).Distinct().ToList();
        Assert.NotEmpty(names);
        var rest = prompt.Replace("Sherlock Holmes", "").Replace(book.Title, "").Replace(book.Author, "");
        Assert.All(names, name => Assert.DoesNotMatch(@"\b" + Regex.Escape(name) + @"\b", rest));

        Assert.Equal(ChatPrompt.DefaultRegister, ChatPrompt.Register(Template(), "some-other-book"));
        Assert.Equal("the register of Dumas in English translation", ChatPrompt.Register(Template(), "three-musketeers"));
        Assert.Equal("Stevenson's plain eighteenth-century English, with sailors' talk where it fits", ChatPrompt.Register(Template(), "treasure-island"));
    }

    private static IConfiguration Template() => new ConfigurationBuilder()
        .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "templates", "cli.appsettings.json")).Build();

    // ---- Command ----

    private sealed class TempRoot : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "TemporalGraph.Data.Tests", Guid.NewGuid().ToString("N"));

        /// <summary>A repo-shaped folder: data/books (manifest + the committed Hound graph), queries/hound-of-the-baskervilles.</summary>
        public TempRoot()
        {
            var books = System.IO.Path.Combine(Path, "data", "books");
            Directory.CreateDirectory(books);
            BookManifest.Write(System.IO.Path.Combine(books, BookManifest.FileName), [new BookEntry(Hound, "The Hound of the Baskervilles", "Arthur Conan Doyle")]);
            File.Copy(HoundDb().Path, System.IO.Path.Combine(books, Hound + ".sqlite"));
            var queries = Directory.CreateDirectory(System.IO.Path.Combine(Path, "queries", Hound)).FullName;
            foreach (var f in Directory.GetFiles(HoundQueriesDir)) File.Copy(f, System.IO.Path.Combine(queries, System.IO.Path.GetFileName(f)));
        }

        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
    }

    private static IConfiguration Config(FakeLlmServer? server, params (string Key, string Value)[] overrides) => new ConfigurationBuilder()
        .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "templates", "cli.appsettings.json"))
        .AddInMemoryCollection(server is null ? [] : new Dictionary<string, string?>
        {
            ["Llm:Type"] = "OpenAI",
            ["Llm:Model"] = "my-deployment",
            ["Llm:Endpoint"] = server.BaseUrl + "/openai/v1/",
            ["Llm:ApiKey"] = "fake-key-for-tests-0123456789",
        })
        .AddInMemoryCollection(overrides.Select(o => KeyValuePair.Create(o.Key, (string?)o.Value)))
        .Build();

    private const string PackFrame = "know at this point (from the record; nothing else is known):";

    private static int Count(string text, string part) => (text.Length - text.Replace(part, "").Length) / part.Length;

    /// <summary>The request's tool_choice ("required", "auto", …), or null when it isn't sent.</summary>
    private static string? ToolChoice(FakeLlmServer.Request r) => JsonNode.Parse(r.Body)!["tool_choice"]?.ToJsonString().Trim('"');

    [Fact]
    public async Task Chat_without_llm_settings_exits_3()
    {
        using var root = new TempRoot();
        var settings = new ChatCommand.Settings { Book = Hound, Chapter = 3, Character = "Sherlock Holmes", Message = "Who is Dr. Mortimer?" };

        Assert.Equal(3, await ChatCommand.RunAsync(settings, Config(null), root.Path, TextWriter.Null, TestContext.Current.CancellationToken));   // Not ready
        Assert.False(Directory.Exists(Path.Combine(root.Path, "runs")));
    }

    [Theory]
    [InlineData(null, 3, "Sherlock Holmes", "Who?")]      // no --book / --db
    [InlineData(Hound, 99, "Sherlock Holmes", "Who?")]    // unknown chapter
    [InlineData(Hound, 3, "Irene Adler", "Who?")]         // unknown character
    [InlineData("no-such-book", 3, "Sherlock Holmes", "Who?")]
    [InlineData(Hound, 3, "Sherlock Holmes", null)]       // no --message / --script
    public async Task Bad_arguments_exit_2(string? book, int chapter, string character, string? message)
    {
        using var root = new TempRoot();
        using var server = new FakeLlmServer();
        var settings = new ChatCommand.Settings { Book = book, Chapter = chapter, Character = character, Message = message };

        Assert.Equal(2, await ChatCommand.RunAsync(settings, Config(server), root.Path, TextWriter.Null, TestContext.Current.CancellationToken));   // Bad input
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task Agent_round_trip_calls_facts_about_then_answers_with_json_output_and_chat_jsonl()
    {
        using var root = new TempRoot();
        using var server = new FakeLlmServer();
        server.OpenAIMessages.Enqueue(FakeLlmServer.ToolCall("call_1", "facts_about", new { character = "Holmes" }));
        server.OpenAIMessages.Enqueue(FakeLlmServer.Text("I am a consulting detective."));
        var settings = new ChatCommand.Settings { Book = Hound, Chapter = 3, Character = "Sherlock Holmes", Message = "Who are you?", Json = true };
        var output = new StringWriter();

        var exit = await ChatCommand.RunAsync(settings, Config(server), root.Path, output, TestContext.Current.CancellationToken);

        Assert.Equal(0, exit);
        // The rows the tool should have returned: facts-about.cypher at the end of chapter 3, spoilers off.
        var (_, graph) = HoundDb();
        var expectedRows = CypherEngine.Execute(graph, File.ReadAllText(Path.Combine(HoundQueriesDir, "facts-about.cypher")),
            new Dictionary<string, object?> { ["character"] = "Holmes", ["pos"] = (long)graph.Chapters[3].CharEnd, ["spoilers"] = false }, TestContext.Current.CancellationToken).Rows.Count;
        Assert.True(expectedRows > 0);

        // --json shape.
        var json = JsonNode.Parse(output.ToString())!.AsObject();
        Assert.Equal(["book", "chapter", "character", "spoilers", "model", "knowledgePack", "requireToolFirst", "turns", "totals", "runId"], json.Select(kv => kv.Key));
        Assert.True(json["requireToolFirst"]!.GetValue<bool>());
        Assert.True(json["knowledgePack"]!["About yourself"]!.GetValue<int>() > 0);
        Assert.Equal(Hound, json["book"]!.GetValue<string>());
        Assert.Equal(3, json["chapter"]!.GetValue<int>());
        Assert.Equal("Sherlock Holmes", json["character"]!.GetValue<string>());
        Assert.False(json["spoilers"]!.GetValue<bool>());
        Assert.Equal(FakeLlmServer.OpenAIModel, json["model"]!.GetValue<string>());
        var turn = Assert.Single(json["turns"]!.AsArray())!;
        Assert.Equal("Who are you?", turn["question"]!.GetValue<string>());
        Assert.Equal("I am a consulting detective.", turn["answer"]!.GetValue<string>());
        var call = Assert.Single(turn["toolCalls"]!.AsArray())!;
        Assert.Equal("facts_about", call["tool"]!.GetValue<string>());
        Assert.Equal("Holmes", call["args"]!["character"]!.GetValue<string>());
        Assert.Equal(expectedRows, call["rows"]!.GetValue<int>());
        // The ids of the rows the model saw.
        Assert.NotEmpty(call["nodeIds"]!.AsArray());
        Assert.Empty(call["edgeIds"]!.AsArray());   // A Fact is a node and names its subjects in a property; no edge to carry
        Assert.Equal(22, turn["inputTokens"]!.GetValue<int>()); // two LLM calls of 11
        Assert.Equal(2, turn["outputTokens"]!.GetValue<int>());
        Assert.Equal(2, json["totals"]!["llmCalls"]!.GetValue<int>());
        Assert.Equal(1, json["totals"]!["toolCalls"]!.GetValue<int>());

        // The requests: system prompt and the 7 tools in the first; the tool result in the second.
        Assert.Equal(2, server.Requests.Count);
        var first = JsonNode.Parse(server.Requests[0].Body)!;
        Assert.Equal("system", first["messages"]![0]!["role"]!.GetValue<string>());
        Assert.StartsWith("You are Holmes,", first["messages"]![0]!["content"]!.ToJsonString().Trim('"'));
        Assert.Equal(ChatTools.AllSpecs.Select(s => s.Name), first["tools"]!.AsArray().Select(t => t!["function"]!["name"]!.GetValue<string>()));   // The Hound has the three extra tools
        var second = JsonNode.Parse(server.Requests[1].Body)!["messages"]!.AsArray();
        Assert.Equal("tool", second[^1]!["role"]!.GetValue<string>());

        // Run folder.
        var run = Directory.GetDirectories(Path.Combine(root.Path, "runs")).Single();
        var lines = File.ReadAllLines(Path.Combine(run, ChatJsonl.FileName)).Select(l => JsonNode.Parse(l)!).ToList();
        Assert.Equal(["pack", "tool", "turn"], lines.Select(l => l["type"]!.GetValue<string>()));
        Assert.Equal("facts_about", lines[1]["tool"]!.GetValue<string>());
        Assert.Equal(expectedRows, lines[1]["rows"]!.GetValue<int>());
        Assert.Equal(1, lines[1]["turn"]!.GetValue<int>());
        Assert.Equal(graph.Chapters[3].CharEnd, lines[1]["pos"]!.GetValue<int>());
        Assert.False(lines[1]["spoilers"]!.GetValue<bool>());
        Assert.NotNull(lines[1]["elapsedMs"]);
        Assert.Equal("I am a consulting detective.", lines[2]["answer"]!.GetValue<string>());
        Assert.Equal(["About yourself", "People you have met", "Your relations", "Events you took part in", "Where people have been placed"],
            lines[0]["sections"]!.AsArray().Select(x => x!["heading"]!.GetValue<string>()));
        Assert.StartsWith("What you, Holmes, know at this point", File.ReadAllText(Path.Combine(run, ChatCommand.KnowledgePackTxt)));
        var runJson = JsonNode.Parse(File.ReadAllText(Path.Combine(run, RunFolder.RunJson)))!;
        Assert.Equal("Sherlock Holmes", runJson["character"]!.GetValue<string>());
        Assert.StartsWith("You are Holmes,", runJson["systemPrompt"]!.GetValue<string>());
        Assert.Equal(2, File.ReadAllLines(Path.Combine(run, RunFolder.CallsJsonl)).Length);
        Assert.True(File.Exists(Path.Combine(run, RunFolder.CostJson)));
        foreach (var file in Directory.GetFiles(run))
            Assert.DoesNotContain("fake-key-for-tests-0123456789", File.ReadAllText(file));
    }

    [Fact]
    public async Task Script_runs_all_questions_in_one_conversation_and_records_unknown_names()
    {
        using var root = new TempRoot();
        using var server = new FakeLlmServer();
        server.OpenAIMessages.Enqueue(FakeLlmServer.Text("Answer one."));
        server.OpenAIMessages.Enqueue(FakeLlmServer.ToolCall("call_1", "who_met", new { character = "Irene Adler" }));
        server.OpenAIMessages.Enqueue(FakeLlmServer.Text("I have not heard of her."));
        var script = Path.Combine(root.Path, "q.txt");
        File.WriteAllText(script, "# test\nQuestion one?\n\nQuestion two?\n");
        var settings = new ChatCommand.Settings { Book = Hound, Chapter = 3, Character = "Holmes", Script = script };
        var output = new StringWriter();

        Assert.Equal(0, await ChatCommand.RunAsync(settings, Config(server), root.Path, output, TestContext.Current.CancellationToken));

        Assert.Equal(3, server.Requests.Count);
        Assert.Contains("Question one?", server.Requests[1].Body); // history: same session
        Assert.Contains("Answer one.", server.Requests[1].Body);
        var text = output.ToString();
        Assert.Contains("Q1: Question one?", text);
        Assert.Contains("A2: I have not heard of her.", text);
        Assert.Contains("    who_met(character: \"Irene Adler\") → 0 rows (unknown name)", text);
        var run = Directory.GetDirectories(Path.Combine(root.Path, "runs")).Single();
        var tool = File.ReadAllLines(Path.Combine(run, ChatJsonl.FileName)).Select(l => JsonNode.Parse(l)!).Single(l => l["type"]!.GetValue<string>() == "tool");
        Assert.Equal("unknown name", tool["note"]!.GetValue<string>());
        Assert.Equal(2, tool["turn"]!.GetValue<int>());
    }

    [Fact]
    public async Task Knowledge_pack_is_sent_once_and_a_tool_call_is_required_on_the_first_round_of_each_turn()
    {
        using var root = new TempRoot();
        using var server = new FakeLlmServer();
        server.OpenAIMessages.Enqueue(FakeLlmServer.ToolCall("call_1", "at_location", new { location = "Baskerville Hall" }));
        server.OpenAIMessages.Enqueue(FakeLlmServer.Text("Answer one."));
        server.OpenAIMessages.Enqueue(FakeLlmServer.ToolCall("call_2", "characters_so_far", new { }));
        server.OpenAIMessages.Enqueue(FakeLlmServer.Text("Answer two."));
        var script = Path.Combine(root.Path, "q.txt");
        File.WriteAllText(script, "Question one?\nQuestion two?\n");
        var settings = new ChatCommand.Settings { Book = Hound, Chapter = 3, Character = "Holmes", Script = script };

        Assert.Equal(0, await ChatCommand.RunAsync(settings, Config(server), root.Path, TextWriter.Null, TestContext.Current.CancellationToken));

        Assert.Equal(4, server.Requests.Count);
        // tool_choice: required on each turn's first model call; after the tool round FunctionInvokingChatClient resets the mode and the
        // OpenAI adapter sends "auto" (tools present, no mode).
        Assert.Equal(["required", "auto", "required", "auto"], server.Requests.Select(ToolChoice));
        // The pack: added once (turn 1) as a system message; later requests carry it once, from the session history.
        Assert.All(server.Requests, r => Assert.Equal(1, Count(r.Body, PackFrame)));
        var first = JsonNode.Parse(server.Requests[0].Body)!["messages"]!.AsArray();
        Assert.Equal(["system", "system", "user"], first.Select(m => m!["role"]!.GetValue<string>()));
        Assert.Contains(PackFrame, first[1]!["content"]!.ToJsonString());
        Assert.Contains("The record of what you know is given to you at the start.", first[0]!["content"]!.ToJsonString());
        var third = JsonNode.Parse(server.Requests[2].Body)!["messages"]!.AsArray();
        Assert.Equal(1, third.Count(m => m!["role"]!.GetValue<string>() == "system" && m["content"]!.ToJsonString().Contains(PackFrame)));
        Assert.Equal("user", third[^1]!["role"]!.GetValue<string>());

        var run = Directory.GetDirectories(Path.Combine(root.Path, "runs")).Single();
        var runJson = JsonNode.Parse(File.ReadAllText(Path.Combine(run, RunFolder.RunJson)))!;
        Assert.Equal(11, runJson["knowledgePackTokens"]!.GetValue<int>()); // the fake server's prompt_tokens for the first call
        Assert.True(runJson["settings"]!["knowledgePack"]!.GetValue<bool>());
        Assert.True(runJson["settings"]!["requireToolFirst"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Pack_and_require_tool_first_can_be_turned_off()
    {
        using var root = new TempRoot();
        using var server = new FakeLlmServer();
        var settings = new ChatCommand.Settings { Book = Hound, Chapter = 3, Character = "Holmes", Message = "Who are you?" };

        Assert.Equal(0, await ChatCommand.RunAsync(settings, Config(server, ("Chat:KnowledgePack", "false"), ("Chat:RequireToolFirst", "false")),
            root.Path, TextWriter.Null, TestContext.Current.CancellationToken));

        var request = Assert.Single(server.Requests);
        Assert.Equal("auto", ToolChoice(request));
        Assert.Equal(0, Count(request.Body, PackFrame));
        Assert.DoesNotContain("The record of what you know", request.Body);
        var run = Directory.GetDirectories(Path.Combine(root.Path, "runs")).Single();
        Assert.False(File.Exists(Path.Combine(run, ChatCommand.KnowledgePackTxt)));
        Assert.Null(JsonNode.Parse(File.ReadAllText(Path.Combine(run, RunFolder.RunJson)))!["knowledgePackTokens"]);
    }

    // ---- Musketeers ----

    [Fact]
    public void Promoted_hound_rows_use_names_spelled_by_the_chapter()
    {
        // On the promoted graph: "Grimpen Mire" is first spelled in ch. 7. Miss Stapleton is called Beryl in
        // ch. 7 (an identity, not a merge), and the two are shown by the name the reader saw first, "Miss Stapleton".
        var (path, graph) = HoundDb();
        var queries = ChatTools.LoadQueries(HoundQueriesDir);
        var locations = ChatCharacters.LoadLocations(path);
        string All(int chapter, bool spoilers)
        {
            var tools = new ChatTools(graph, queries, chapter, spoilers, 1000, locations: locations);
            var names = ChatCharacters.Load(path, graph).SelectMany(c => c.Aliases).Distinct();
            return string.Join("\n", names.Select(n => tools.Run("facts_about", n)).Concat(names.Select(n => tools.Run("who_met", n))).Append(tools.Run("where_placed", null))
                .Append(tools.Run("characters_so_far", null)));
        }

        // The promoted graph: the book says "Beryl" in ch. 7 ("Halloa, Beryl!"), so she is "Beryl Stapleton" from
        // there; "Garcia", her maiden name, comes in ch. 15. The mire is "the Grimpen Mire" from ch. 7.
        // ("Grimpen Mire" without the article is first placed in ch. 9.)
        var ch9 = All(9, spoilers: false);
        Assert.DoesNotContain("Garcia", ch9);
        Assert.Contains("Miss Stapleton", ch9);
        Assert.Contains("Grimpen Mire", ch9);
        var ch5 = All(5, spoilers: false);
        Assert.DoesNotContain("Grimpen Mire", ch5);
        Assert.DoesNotContain("Beryl", ch5);
    }

    [Fact]
    public async Task Selected_character_is_addressed_by_its_display_name()
    {
        using var root = new TempRoot();
        using var server = new FakeLlmServer();
        // By ch. 8 Sir Henry Baskerville has been called "Sir Henry", the short name he is shown by. "Baskerville"
        // is shared with other entities' names (Sir Charles Baskerville, Hugo Baskerville, …), so it is never the display name.
        server.OpenAIMessages.Enqueue(FakeLlmServer.ToolCall("call_1", "facts_about", new { character = "Baskerville" }));
        server.OpenAIMessages.Enqueue(FakeLlmServer.Text("I am the heir."));
        var settings = new ChatCommand.Settings { Book = Hound, Chapter = 8, Character = "Sir Henry Baskerville", Message = "Who are you?" };

        Assert.Equal(0, await ChatCommand.RunAsync(settings, Config(server), root.Path, TextWriter.Null, TestContext.Current.CancellationToken));

        var first = JsonNode.Parse(server.Requests[0].Body)!["messages"]!.AsArray();
        Assert.StartsWith("You are Sir Henry, a character", first[0]!["content"]!.GetValue<string>());
        Assert.StartsWith("What you, Sir Henry, know at this point", first[1]!["content"]!.GetValue<string>());
        var run = Directory.GetDirectories(Path.Combine(root.Path, "runs")).Single();
        var runJson = JsonNode.Parse(File.ReadAllText(Path.Combine(run, RunFolder.RunJson)))!;
        Assert.Equal("Sir Henry Baskerville", runJson["character"]!.GetValue<string>());
        Assert.Equal("Sir Henry", runJson["displayName"]!.GetValue<string>());
        Assert.NotNull(runJson["lateRowsSuppressed"]); // counted (late rows are dropped with spoilers off)
    }

    [Fact]
    public void Musketeers_queries_are_the_hound_queries_and_run_on_an_empty_graph()
    {
        var root = RepoPaths.FindRoot();
        var hound = Path.Combine(root, "queries", Hound);
        var musketeers = Path.Combine(root, "queries", "three-musketeers");
        var island = Path.Combine(root, "queries", "treasure-island");
        // The three books are on the full schema, so their tool queries are one text (the header's example name aside): the
        // twelve tool files and the four queries the full schema added.
        string[] added = ["events-of.cypher", "facts-discovered-by.cypher", "perceptions-of.cypher", "referenced-events.cypher"];
        foreach (var book in new[] { musketeers, island })
        {
            Assert.Equal(Directory.GetFiles(hound).Select(Path.GetFileName).Order(), Directory.GetFiles(book).Select(Path.GetFileName).Order());
            Assert.Equal(ChatTools.LoadQueries(hound), ChatTools.LoadQueries(book));
            Assert.Empty(ChatTools.MissingFiles(book));
            Assert.All(ChatTools.ExtraSpecs, s => Assert.True(ChatTools.LoadQueries(book).ContainsKey(s.Name)));
        }
        // The shape of before the full schema (the fixture graphs of these tests) is the same twelve files without the four.
        Assert.Equal(Directory.GetFiles(QueriesDir).Select(Path.GetFileName).Concat(added).Order(), Directory.GetFiles(hound).Select(Path.GetFileName).Order());
        Assert.All(ChatTools.ExtraSpecs, s => Assert.False(ChatTools.LoadQueries(QueriesDir).ContainsKey(s.Name)));

        var empty = new BookGraph([], [], new Dictionary<int, (int, int)> { [1] = (0, 1000) });
        var tools = new ChatTools(empty, ChatTools.LoadQueries(musketeers), 1, false, 50);
        foreach (var spec in ChatTools.Specs)
            Assert.StartsWith(ChatTools.Nothing, tools.Run(spec.Name, spec.Parameter == ToolParameter.None ? null : "d'Artagnan"));
        Assert.All(tools.Calls, c => Assert.Equal(0, c.Rows));
        Assert.All(ChatTools.PackSections, s => Assert.Contains($"## {s.Heading}\n(nothing)", tools.KnowledgePack("d'Artagnan", "d'Artagnan").Text));
    }

    [Fact]
    public async Task Llm_profile_is_used_and_recorded_in_run_json()
    {
        // --llm picks Llm:Profiles:<name>; same Type and Endpoint as Llm, so the key falls back to Llm:ApiKey; run.json records the profile.
        using var root = new TempRoot();
        using var server = new FakeLlmServer();
        server.OpenAIMessages.Enqueue(FakeLlmServer.ToolCall("call_1", "characters_so_far", new { }));
        server.OpenAIMessages.Enqueue(FakeLlmServer.Text("Watson and Mortimer."));
        var config = Config(null,
            ("Llm:Type", "OpenAI"), ("Llm:Endpoint", server.BaseUrl + "/openai/v1/"), ("Llm:ApiKey", "fake-key-for-tests-0123456789"),
            ("Llm:Profiles:p1:Type", "OpenAI"), ("Llm:Profiles:p1:Model", "p1-deployment"), ("Llm:Profiles:p1:Endpoint", server.BaseUrl + "/openai/v1/"),
            ("Llm:Profiles:p1:ReasoningEffort", "low"));
        var settings = new ChatCommand.Settings { Book = Hound, Chapter = 3, Character = "Sherlock Holmes", Message = "Whom do you know?", Llm = "p1" };

        Assert.Equal(0, await ChatCommand.RunAsync(settings, config, root.Path, TextWriter.Null, TestContext.Current.CancellationToken));

        Assert.All(server.Requests, r => Assert.Contains("\"model\":\"p1-deployment\"", r.Body.Replace(" ", "")));
        Assert.All(server.Requests, r => Assert.Contains("\"reasoning_effort\":\"low\"", r.Body.Replace(" ", "")));
        var run = Directory.GetDirectories(Path.Combine(root.Path, "runs")).Single();
        var runJson = JsonNode.Parse(File.ReadAllText(Path.Combine(run, RunFolder.RunJson)))!;
        Assert.Equal("p1", runJson["settings"]!["llm"]!["profile"]!.GetValue<string>());
        Assert.Equal("p1-deployment", runJson["settings"]!["llm"]!["model"]!.GetValue<string>());
        Assert.Equal("low", runJson["settings"]!["sampling"]!["reasoningEffort"]!.GetValue<string>());
        Assert.EndsWith("--llm p1", runJson["command"]!.GetValue<string>());
    }

    [Fact]
    public async Task Unconfigured_llm_profile_exits_3_without_a_run()
    {
        using var root = new TempRoot();
        var settings = new ChatCommand.Settings { Book = Hound, Chapter = 3, Character = "Sherlock Holmes", Message = "Who?", Llm = "gpt54mini" };
        Assert.Equal(3, await ChatCommand.RunAsync(settings, Config(null), root.Path, TextWriter.Null, TestContext.Current.CancellationToken));   // Not ready
        Assert.False(Directory.Exists(Path.Combine(root.Path, "runs")));
    }

    [Fact]
    public async Task Chat_with_a_book_without_characters_exits_3_without_calling_the_model()
    {
        using var root = new TempRoot();
        using var server = new FakeLlmServer();
        const string musketeers = "three-musketeers";
        var books = Path.Combine(root.Path, "data", "books");
        BookManifest.Write(Path.Combine(books, BookManifest.FileName), [new BookEntry(musketeers, "The Three Musketeers", "Alexandre Dumas")]);
        var db = Path.Combine(books, musketeers + ".sqlite");
        File.Copy(BookDatabase.EmbeddedCopy(BookResources.Assembly, BookResources.DatabaseResourceName(new BookEntry(musketeers, "", ""))), db);
        // The committed Musketeers graph is promoted, so empty its characters table (a book indexed no further than prepare).
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db};Pooling=False"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM characters";
            cmd.ExecuteNonQuery();
        }
        var queries = Directory.CreateDirectory(Path.Combine(root.Path, "queries", musketeers)).FullName;
        foreach (var f in Directory.GetFiles(QueriesDir)) File.Copy(f, Path.Combine(queries, Path.GetFileName(f)));
        var settings = new ChatCommand.Settings { Book = musketeers, Chapter = 1, Character = "d'Artagnan", Message = "Who are you?" };

        Assert.Equal(3, await ChatCommand.RunAsync(settings, Config(server), root.Path, TextWriter.Null, TestContext.Current.CancellationToken));   // Not ready (the book is not indexed)
        Assert.Empty(server.Requests);
    }
}
