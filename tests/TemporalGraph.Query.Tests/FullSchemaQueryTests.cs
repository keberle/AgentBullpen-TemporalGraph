namespace TemporalGraph.Query.Tests;

/// <summary>
/// A small hand-built graph of the full bi-temporal schema. Chapter 1 is [0, 1000), chapter 2 [1000, 3000). Both clocks on
/// every node and edge. In chapter 1 (1 October 1889) Stapleton tells Watson he is a harmless naturalist; in
/// chapter 2 (2 October) Holmes exposes him: the lie is retracted, Watson's belief is closed by a disbelief, Stapleton turns out to
/// be Vandeleur, and Vandeleur's school of 1884 is a referenced event.
/// </summary>
public static class FullFixture
{
    private static List<object?> L(params object[] items) => items.Cast<object?>().ToList();

    private static Dictionary<string, object?> P(long revealed, string from, params (string Key, object? Value)[] more)
    {
        var (lo, hi) = from.Length == 4 ? ($"{from}-01-01", $"{from}-12-31") : (from, from);
        var p = new Dictionary<string, object?> { ["revealedPos"] = revealed, ["storyFrom"] = from, ["storyFromLo"] = lo, ["storyFromHi"] = hi };
        foreach (var (k, v) in more) p[k] = v;
        return p;
    }

    private const string Day1 = "1889-10-01", Day2 = "1889-10-02";

    public static readonly BookGraph Graph = new(
    [
        new(1, ["Character"], P(10, Day1, ("name", "Sherlock Holmes"), ("aliases", L("Sherlock Holmes", "Holmes")), ("aliasFirstPos", new Dictionary<string, object?> { ["Sherlock Holmes"] = 10.0, ["Holmes"] = 15.0 }))),
        new(2, ["Character"], P(20, Day1, ("name", "Dr. Watson"), ("aliases", L("Dr. Watson", "Watson")), ("aliasFirstPos", new Dictionary<string, object?> { ["Dr. Watson"] = 20.0, ["Watson"] = 22.0 }))),
        new(3, ["Character"], P(600, "1884", ("name", "Stapleton"), ("aliases", L("Stapleton")), ("aliasFirstPos", new Dictionary<string, object?> { ["Stapleton"] = 600.0 }))),
        new(4, ["Character"], P(2000, "1884", ("name", "Vandeleur"), ("aliases", L("Vandeleur")), ("aliasFirstPos", new Dictionary<string, object?> { ["Vandeleur"] = 2000.0 }))),
        new(5, ["Location"], P(30, Day1, ("name", "Baker Street"), ("aliases", L("Baker Street")), ("aliasFirstPos", new Dictionary<string, object?> { ["Baker Street"] = 30.0 }))),
        new(6, ["Event"], P(40, Day1, ("kind", "narrated"), ("summary", "Holmes and Watson breakfast at Baker Street."))),
        new(7, ["Event"], P(610, Day1, ("kind", "narrated"), ("summary", "Stapleton calls on Watson."))),
        new(8, ["Event"], P(2010, Day2, ("kind", "narrated"), ("summary", "Holmes exposes Stapleton."))),
        new(9, ["Event"], P(2020, "1884", ("kind", "referenced"), ("summary", "Vandeleur keeps a school in Yorkshire."))),
        new(10, ["Fact"], P(45, Day1, ("text", "Holmes is late in the mornings."), ("subject", L(1L)))),
        new(11, ["Fact"], P(620, Day1, ("text", "Stapleton is a harmless naturalist."), ("subject", L(3L)), ("retractedPos", 2030L))),
        new(12, ["Fact"], P(2025, "1884", ("text", "Vandeleur kept a school in Yorkshire."), ("subject", L(4L)))),
    ],
    [
        new(101, "PARTICIPATED_IN", 1, 6, P(40, Day1, ("role", "speaker"))),
        new(102, "PARTICIPATED_IN", 2, 6, P(40, Day1, ("role", "listener"))),
        new(103, "PARTICIPATED_IN", 3, 7, P(610, Day1, ("role", "speaker"))),
        new(104, "PARTICIPATED_IN", 2, 7, P(610, Day1, ("role", "listener"))),
        new(105, "PARTICIPATED_IN", 1, 8, P(2010, Day2, ("role", "speaker"))),
        new(106, "PARTICIPATED_IN", 2, 8, P(2010, Day2, ("role", "listener"))),
        new(107, "PARTICIPATED_IN", 4, 9, P(2020, "1884")),
        new(108, "LOCATED_AT", 6, 5, P(40, Day1)),
        new(109, "DISCOVERED", 6, 10, P(45, Day1, ("how", "witnessed"))),
        new(110, "DISCOVERED", 7, 11, P(620, Day1, ("how", "told"), ("retractedPos", 2030L))),
        new(111, "DISCOVERED", 9, 12, P(2025, "1884", ("how", "recalled"))),
        new(112, "DISCOVERED", 8, 12, P(2025, Day2, ("how", "told"))),
        new(113, "PERCEIVES", 2, 11, P(620, Day1, ("kind", "believes"), ("retractedPos", 2030L), ("storyUntil", Day2), ("storyUntilLo", Day2), ("storyUntilHi", Day2))),
        new(114, "PERCEIVES", 2, 11, P(2030, Day2, ("kind", "disbelieves"), ("retractedPos", 2031L))),
        new(115, "PERCEIVES", 2, 3, P(2040, Day2, ("kind", "suspects"))),
        new(116, "MENTIONED_IN", 9, 8, P(2020, Day2)),
        new(117, "IDENTICAL_TO", 3, 4, P(2000, "1884")),
        new(118, "RELATED_TO", 2, 1, P(50, Day1, ("kind", "friend"))),
        new(119, "PERCEIVES", 2, 10, P(45, Day1, ("kind", "believes"))),
        new(120, "PERCEIVES", 2, 12, P(2025, Day2, ("kind", "believes"))),
    ],
    new Dictionary<int, (int, int)> { [1] = (0, 1000), [2] = (1000, 3000) },
    timeline:
    [
        new("anchor", null, "anchor", null, null, Day1, null, Day1, Day1, null, null, null, "Assumption: the first narrated event is placed on 1889-10-01."),
        new("chapter", 1, "One", 0, 1000, Day1, null, Day1, Day1, null, null, null, null),
        new("chapter", 2, "Two", 1000, 3000, Day2, null, Day2, Day2, null, null, null, null),
        new("event", 6, "Holmes and Watson breakfast at Baker Street.", 40, 60, Day1, null, Day1, Day1, null, null, 2, null),
        new("event", 7, "Stapleton calls on Watson.", 610, 630, Day1, null, Day1, Day1, null, null, 3, null),
        new("event", 8, "Holmes exposes Stapleton.", 2010, 2030, Day2, null, Day2, Day2, null, null, 4, null),
        new("event", 9, "Vandeleur keeps a school in Yorkshire.", 2020, 2040, "1884", null, "1884-01-01", "1884-12-31", null, null, 1, null),
    ]);

    public const long Ch1 = 1000, Ch2 = 3000;

    public static string QueryFile(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "queries-full", name));

    public static List<string> Rows(string file, long pos, bool spoilers, params (string Name, object? Value)[] more)
    {
        var parameters = new Dictionary<string, object?> { ["pos"] = pos, ["spoilers"] = spoilers };
        foreach (var (k, v) in more) parameters[k] = v;
        return Fixture.Rows(CypherEngine.Execute(Graph, QueryFile(file), parameters, TestContext.Current.CancellationToken));
    }
}

// The spoiler rule (SpoilerGate, the engine's visible()) and The Hound's query files on the full schema.
public class FullSchemaQueryTests
{
    private const long Ch1 = FullFixture.Ch1, Ch2 = FullFixture.Ch2;
    private static List<string> Rows(string file, long pos, bool spoilers, params (string Name, object? Value)[] more) => FullFixture.Rows(file, pos, spoilers, more);
    private static List<string> First(List<string> rows, int columns = 1) => rows.Select(r => string.Join(" | ", r.Split(" | ").Take(columns))).ToList();

    public static TheoryData<string> Files => new(Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "queries-full"), "*.cypher").Select(Path.GetFileName).Order()!);

    [Fact]
    public void The_hound_has_the_twelve_tool_queries_and_the_four_new_ones()
    {
        var files = Files.Select(f => (string)f.Data).ToList();
        Assert.Equal(16, files.Count);
        Assert.Superset(new HashSet<string> { "facts-discovered-by.cypher", "perceptions-of.cypher", "events-of.cypher", "referenced-events.cypher" }, files.ToHashSet());
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void Each_query_file_parses_has_a_header_and_no_edge_type_the_schema_removed(string file)
    {
        var text = FullFixture.QueryFile(file);
        Assert.StartsWith("// ", text);
        Assert.IsType<ReturnClause>(AstBuilder.Parse(text).Clauses[^1]);
        var query = string.Join("\n", text.Split('\n').Where(l => !l.TrimStart().StartsWith("//")));
        Assert.DoesNotMatch(@":(MET|KNOWS|ABOUT|STATES)\b", query);
    }

    // The three books are on the full schema, ten samples each (they run against the promoted graphs in the API tests).
    [Theory]
    [InlineData("hound-of-the-baskervilles")]
    [InlineData("three-musketeers")]
    [InlineData("treasure-island")]
    public void Every_books_samples_parse_and_run(string book)
    {
        var files = Directory.GetFiles(Path.Combine(SearchFiles.Root(), "queries", book, "samples"), "*.cypher").Order(StringComparer.Ordinal).ToList();
        Assert.Equal(10, files.Count);
        foreach (var f in files)
            foreach (var spoilers in new[] { false, true })
                CypherEngine.Execute(FullFixture.Graph, File.ReadAllText(f), new Dictionary<string, object?> { ["pos"] = Ch2, ["spoilers"] = spoilers }, TestContext.Current.CancellationToken);
    }

    // A pattern whose WHERE names its start node by id (a Fact's subjects are node ids in a property) starts from those nodes
    // instead of every node of the graph. The rows are the same either way round, and the same as without the lookup.
    [Fact]
    public void A_node_named_by_id_in_the_where_gives_the_same_rows_as_a_scan()
    {
        List<string> Run(string q) => Fixture.Rows(CypherEngine.Execute(FullFixture.Graph, q, new Dictionary<string, object?> { ["pos"] = Ch2, ["spoilers"] = true }, TestContext.Current.CancellationToken));
        const string returns = " RETURN f.text AS fact, c.name AS who ORDER BY fact, who";
        var lookup = Run("MATCH (f:Fact), (c:Character) WHERE id(c) IN f.subject AND visible(f) AND visible(c)" + returns);        // c by id, f known
        var scan = Run("MATCH (c:Character), (f:Fact) WHERE visible(f) AND visible(c) AND id(c) IN f.subject" + returns);          // f is not named by id: a scan
        Assert.NotEmpty(lookup);
        Assert.Equal(scan, lookup);
        // An equality, either way round; an id no node has gives no row; a condition under OR is no lookup, and still right.
        var holmes = Run("MATCH (c:Character) WHERE c.name = 'Sherlock Holmes' RETURN id(c) AS id").Single();
        Assert.Equal(["Sherlock Holmes"], Run($"MATCH (c) WHERE id(c) = {holmes} RETURN c.name AS who"));
        Assert.Equal(["Sherlock Holmes"], Run($"MATCH (c) WHERE {holmes} = id(c) AND c:Character RETURN c.name AS who"));
        Assert.Empty(Run("MATCH (c) WHERE id(c) = 987654 RETURN c.name AS who"));
        Assert.Equal(Run("MATCH (c:Character) RETURN c.name AS who ORDER BY who"), Run($"MATCH (c:Character) WHERE id(c) = {holmes} OR c.name <> 'x' RETURN c.name AS who ORDER BY who"));
    }

    [Fact]
    public void Story_of_pos_is_the_latest_story_time_the_reader_has_been_shown()
    {
        Assert.Null(FullFixture.Graph.StoryAt(0));                  // nothing read yet
        Assert.Equal("1889-10-01", FullFixture.Graph.StoryAt(Ch1)); // chapter 2 starts at 1000 and has not been read
        Assert.Equal("1889-10-01", FullFixture.Graph.StoryAt(1500));
        Assert.Equal("1889-10-02", FullFixture.Graph.StoryAt(2010));
        // The 1884 flashback told at 2020 does not turn the clock back, and its year-wide upper bound does not push it on.
        Assert.Equal("1889-10-02", FullFixture.Graph.StoryAt(Ch2));
    }

    [Fact]
    public void Visible_is_the_spoiler_rule_and_needs_pos_and_spoilers()
    {
        static List<string> Names(long pos, bool spoilers) => Fixture.Rows(CypherEngine.Execute(FullFixture.Graph, "MATCH (c:Character) WHERE visible(c) RETURN c.name ORDER BY c.name",
            new Dictionary<string, object?> { ["pos"] = pos, ["spoilers"] = spoilers }, TestContext.Current.CancellationToken));
        Assert.Equal(["Dr. Watson", "Sherlock Holmes", "Stapleton"], Names(Ch1, false));
        Assert.Equal(["Dr. Watson", "Sherlock Holmes", "Stapleton", "Vandeleur"], Names(Ch2, false));
        // Spoilers on: Vandeleur has existed since 1884 in story time, whenever the reader meets him.
        Assert.Equal(["Dr. Watson", "Sherlock Holmes", "Stapleton", "Vandeleur"], Names(Ch1, true));
        Assert.Contains("needs the parameter $pos", Assert.Throws<CypherException>(() => CypherEngine.Execute(FullFixture.Graph, "MATCH (c) WHERE visible(c) RETURN c")).Reason);
        Assert.Contains("needs a node or relationship", Assert.Throws<CypherException>(() => CypherEngine.Execute(FullFixture.Graph, "RETURN visible(1)",
            new Dictionary<string, object?> { ["pos"] = 1L, ["spoilers"] = false })).Reason);
    }

    [Fact]
    public void Who_met_is_two_participants_of_one_event()
    {
        Assert.Equal(["Sherlock Holmes | 1", "Stapleton | 3"], First(Rows("who-met.cypher", Ch1, false, ("character", "Watson")), 2));
        Assert.Equal(["Dr. Watson | 2"], First(Rows("who-met.cypher", Ch2, false, ("character", "Stapleton")), 2));
        Assert.Empty(Rows("who-met.cypher", 500, false, ("character", "Stapleton")));
    }

    [Fact]
    public void Facts_about_reads_the_subject_property_and_a_retracted_fact_goes()
    {
        Assert.Equal(["Stapleton is a harmless naturalist. | Stapleton | 620"], First(Rows("facts-about.cypher", Ch1, false, ("character", "Stapleton")), 3));
        Assert.Empty(Rows("facts-about.cypher", Ch2, false, ("character", "Stapleton")));                     // exposed in chapter 2
        Assert.Empty(Rows("facts-about.cypher", Ch1, false, ("character", "Vandeleur")));                     // not met yet
        Assert.Equal(["Vandeleur kept a school in Yorkshire."], First(Rows("facts-about.cypher", Ch1, true, ("character", "Vandeleur"))));   // true in story time since 1884
    }

    [Fact]
    public void Events_of_and_events_for_character()
    {
        Assert.Equal(["Holmes and Watson breakfast at Baker Street. | 1889-10-01 | 40", "Stapleton calls on Watson. | 1889-10-01 | 610"],
            First(Rows("events-for-character.cypher", Ch1, false, ("character", "Watson")), 3));
        Assert.Equal(["Stapleton calls on Watson. | narrated | speaker | Dr. Watson, Stapleton"], First(Rows("events-of.cypher", Ch1, false, ("character", "Stapleton")), 4));
        Assert.Equal(["Vandeleur keeps a school in Yorkshire. | referenced"], First(Rows("events-of.cypher", Ch2, false, ("character", "Vandeleur")), 2));
    }

    [Fact]
    public void Facts_discovered_by_gives_the_event_and_how()
    {
        Assert.Equal(["Holmes is late in the mornings. | witnessed | Holmes and Watson breakfast at Baker Street.", "Stapleton is a harmless naturalist. | told | Stapleton calls on Watson."],
            First(Rows("facts-discovered-by.cypher", Ch1, false, ("character", "Watson")), 3));
        Assert.Equal(["Holmes is late in the mornings. | witnessed", "Vandeleur kept a school in Yorkshire. | told"], First(Rows("facts-discovered-by.cypher", Ch2, false, ("character", "Watson")), 2));
        Assert.Equal(["Vandeleur kept a school in Yorkshire. | recalled"], First(Rows("facts-discovered-by.cypher", Ch2, false, ("character", "Vandeleur")), 2));
    }

    [Fact]
    public void Perceptions_of_follows_a_change_as_a_closed_edge_and_a_new_one()
    {
        Assert.Equal(["believes | Holmes is late in the mornings.", "believes | Stapleton is a harmless naturalist."], First(Rows("perceptions-of.cypher", Ch1, false, ("character", "Watson"), ("target", null)), 2));
        // After the exposure: the belief is closed, and the lie itself is retracted, so only what still stands is listed.
        Assert.Equal(["believes | Holmes is late in the mornings.", "believes | Vandeleur kept a school in Yorkshire.", "suspects | Stapleton"],
            First(Rows("perceptions-of.cypher", Ch2, false, ("character", "Watson"), ("target", null)), 2));
        // One person: the perception of him, and of the facts about him.
        Assert.Equal(["believes | Stapleton is a harmless naturalist. | Stapleton"], First(Rows("perceptions-of.cypher", Ch1, false, ("character", "Watson"), ("target", "Stapleton")), 3));
        Assert.Equal(["suspects | Stapleton"], First(Rows("perceptions-of.cypher", Ch2, false, ("character", "Watson"), ("target", "Stapleton")), 2));
        // Spoilers on at the end of chapter 1 (story time 1 October): the belief holds; the disbelief of 2 October has not happened.
        Assert.Contains("believes | Stapleton is a harmless naturalist.", First(Rows("perceptions-of.cypher", Ch1, true, ("character", "Watson"), ("target", null)), 2));
        Assert.DoesNotContain(Rows("perceptions-of.cypher", Ch1, true, ("character", "Watson"), ("target", null)), r => r.StartsWith("disbelieves") || r.StartsWith("suspects"));
    }

    [Fact]
    public void Referenced_events_show_once_their_narrating_event_does()
    {
        Assert.Empty(Rows("referenced-events.cypher", Ch1, false));
        Assert.Equal(["Vandeleur keeps a school in Yorkshire. | 1884 | Holmes exposes Stapleton."], First(Rows("referenced-events.cypher", Ch2, false), 3));
        Assert.Empty(Rows("referenced-events.cypher", Ch1, true));   // the event that mentions it is of 2 October
    }

    [Fact]
    public void The_other_tool_queries_run_on_the_full_schema()
    {
        Assert.Equal(["Sherlock Holmes | 1", "Dr. Watson | 2", "Stapleton | 3"], Rows("characters-so-far.cypher", Ch1, false));
        Assert.Equal(["Sherlock Holmes | friend | 1 | 118"], Rows("relations.cypher", Ch1, false, ("character", "Watson")));
        Assert.Equal(["null | Holmes and Watson breakfast at Baker Street."], First(Rows("at-location.cypher", Ch1, false, ("location", "Baker Street")), 2));
        Assert.Empty(Rows("where.cypher", Ch1, false));
        Assert.Equal(["Sherlock Holmes | 1 | 1", "Stapleton | 1 | 3"], Rows("most-discussed.cypher", Ch1, false).Order().ToList());
        Assert.Equal(["Stapleton is a harmless naturalist. | Stapleton | 620 | 11 | 3"], Rows("search-facts.cypher", Ch1, false, ("term", "NATURALIST")));
        Assert.Equal(["Holmes is late in the mornings."], First(Rows("search-facts.cypher", Ch1, false, ("term", "sherlock"))));   // by the subject's name
        Assert.Equal(["Stapleton calls on Watson."], First(Rows("search-events.cypher", Ch1, false, ("term", "stapleton"))));
        Assert.Equal(["Sherlock Holmes", "Stapleton"], Rows("unmentioned-optional.cypher", Ch1, false).Where(n => n != "Dr. Watson").ToList());
        Assert.Equal(["Vandeleur kept a school in Yorkshire. | 1884 | 1884-12-31 | 2025"], Fixture.Rows(CypherEngine.Execute(FullFixture.Graph, FullFixture.QueryFile("true-in-story-by.cypher"))));
    }

    [Fact]
    public void The_fixture_passes_the_leak_tests_and_every_tool_query_returns_only_visible_ids()
    {
        Assert.All(LeakTests.Run(FullFixture.Graph), c => Assert.True(c.Passed, $"{c.Name}: {string.Join("; ", c.Failures)}"));
        var nodes = FullFixture.Graph.Nodes.ToDictionary(n => n.Id);
        var edges = FullFixture.Graph.Relationships.ToDictionary(r => r.Id);
        foreach (var file in Files.Select(f => (string)f.Data).Where(f => f != "true-in-story-by.cypher"))
            foreach (var pos in new[] { Ch1, Ch2 })
                foreach (var spoilers in new[] { false, true })
                    foreach (var name in new[] { "Watson", "Stapleton", "Vandeleur", "Holmes" })
                    {
                        var story = spoilers ? FullFixture.Graph.StoryAt(pos) : null;
                        var result = CypherEngine.Execute(FullFixture.Graph, FullFixture.QueryFile(file), new Dictionary<string, object?>
                        {
                            ["pos"] = pos, ["spoilers"] = spoilers, ["character"] = name, ["location"] = "Baker Street", ["term"] = "a", ["target"] = null,
                        }, TestContext.Current.CancellationToken);
                        for (var c = 0; c < result.Columns.Count; c++)
                        {
                            var column = result.Columns[c];
                            if (!column.EndsWith("Id") && !column.EndsWith("Ids")) continue;
                            foreach (var row in result.Rows)
                                foreach (var id in row[c] is IReadOnlyList<object?> list ? list.OfType<long>() : row[c] is long one ? [one] : [])
                                    Assert.True(SpoilerGate.Visible(column.StartsWith("edge") ? edges[id].Properties : nodes[id].Properties, pos, spoilers, story),
                                        $"{file} at {pos}, spoilers {spoilers}, {name}: {column} {id} is hidden");
                        }
                    }
    }
}
