using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TemporalGraph.Data;
using static TemporalGraph.Query.Tests.Fixture;

namespace TemporalGraph.Query.Tests;

public class ParserTests
{
    private static string QueryFile(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "queries", name));

    public static TheoryData<string> QueryFiles => new(Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "queries"), "*.cypher").Select(Path.GetFileName).Order()!);

    [Fact]
    public void There_are_twelve_query_files() => Assert.Equal(12, QueryFiles.Count);   // + search-facts, search-events

    [Theory]
    [MemberData(nameof(QueryFiles))]
    public void Each_query_file_parses_and_has_a_comment_header(string file)
    {
        var text = QueryFile(file);
        Assert.StartsWith("// ", text);
        var ast = AstBuilder.Parse(text);
        Assert.IsType<ReturnClause>(ast.Clauses[^1]);
    }

    // A stack overflow cannot be caught, so each of these would end the test process if the limit were not there.
    [Theory]
    [InlineData('(', ')')]
    [InlineData('[', ']')]
    public void A_query_nested_a_thousand_deep_is_refused_and_the_process_survives(char open, char close)
    {
        var query = "RETURN " + new string(open, 1000) + "1" + new string(close, 1000);

        var parse = Assert.Throws<CypherException>(() => AstBuilder.Parse(query));
        var run = Assert.Throws<CypherException>(() => CypherEngine.Execute(Fixture.Graph, query));

        Assert.Contains("nested too deeply", parse.Message);
        Assert.Contains("nested too deeply", run.Message);
        Assert.Equal((1, 7 + CypherLimits.MaxDepth), (parse.Line, parse.Column));   // the bracket that goes past the limit
        Assert.Single(CypherEngine.Execute(Fixture.Graph, "RETURN 1 AS one").Rows);   // still here
    }

    [Fact]
    public void A_query_at_the_nesting_limit_runs_and_one_level_more_is_refused()
    {
        static string Nested(int depth) => "RETURN " + new string('(', depth) + "1" + new string(')', depth) + " AS n";

        Assert.Equal(1L, CypherEngine.Execute(Fixture.Graph, Nested(CypherLimits.MaxDepth)).Rows[0][0]);
        Assert.Throws<CypherException>(() => AstBuilder.Parse(Nested(CypherLimits.MaxDepth + 1)));
    }

    [Fact]
    public void Brackets_inside_a_string_or_a_back_quoted_name_are_not_nesting()
    {
        var brackets = new string('(', 500);

        Assert.Equal(0, CypherLimits.DeepestAt($"RETURN '{brackets}' AS s").Depth);
        Assert.Equal(0, CypherLimits.DeepestAt($"RETURN \"{brackets}\\\"{brackets}\" AS s").Depth);
        Assert.Equal(1, CypherLimits.DeepestAt($"MATCH (n) RETURN n.`{brackets}`").Depth);
        Assert.Equal(brackets, CypherEngine.Execute(Fixture.Graph, $"RETURN '{brackets}' AS s").Rows[0][0]);
    }

    [Fact]
    public void An_over_long_query_is_refused()
    {
        var atTheLimit = "RETURN '" + new string('x', CypherLimits.MaxLength - 14) + "' AS s";
        Assert.Equal(CypherLimits.MaxLength, atTheLimit.Length);
        Assert.Single(CypherEngine.Execute(Fixture.Graph, atTheLimit).Rows);

        var ex = Assert.Throws<CypherException>(() => AstBuilder.Parse(atTheLimit + " "));
        Assert.Contains("too long", ex.Message);
        Assert.Contains("4,000", ex.Message);
        Assert.Throws<CypherException>(() => CypherEngine.Execute(Fixture.Graph, "RETURN " + new string('1', 100_000)));
    }

    // A chain of operators is a deep tree with no bracket in it. Inside the length limit it either runs or is refused; it never
    // ends the process, on the small stack too.
    [Theory]
    [InlineData("NOT", 1024)]
    [InlineData("NOT", 256)]
    [InlineData("AND", 256)]
    [InlineData("OR", 256)]
    public void The_longest_chain_of_operators_the_length_limit_allows_runs_or_is_refused(string op, int stackKb)
    {
        var query = op == "NOT"
            ? "RETURN " + string.Concat(Enumerable.Repeat("NOT ", (CypherLimits.MaxLength - 11) / 4)) + "true"
            : "RETURN " + string.Join($" {op} ", Enumerable.Repeat("true", op == "OR" ? 495 : 440));
        Assert.InRange(query.Length, 3000, CypherLimits.MaxLength);

        Exception? error = null;
        var thread = new Thread(() => { try { CypherEngine.Execute(Fixture.Graph, query); } catch (Exception e) { error = e; } }, stackKb * 1024);
        thread.Start();
        thread.Join();

        if (error is not null) Assert.Contains("nested too deeply", Assert.IsType<CypherException>(error).Message);
    }

    // Every query file in the repo is inside both limits, with room: the longest and the deepest are what the limits were chosen from.
    [Fact]
    public void Every_shipped_query_parses_and_is_well_inside_the_limits()
    {
        var root = SearchFiles.Root();
        var files = Directory.GetFiles(Path.Combine(root, "queries"), "*.cypher", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(Path.Combine(root, "tests", "fixtures"), "*.cypher", SearchOption.AllDirectories)).ToList();
        Assert.True(files.Count >= 80, $"{files.Count} query files");

        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            Assert.IsType<ReturnClause>(AstBuilder.Parse(text).Clauses[^1]);
        }
        Assert.InRange(files.Max(f => File.ReadAllText(f).Length), 1, CypherLimits.MaxLength / 3);
        Assert.InRange(files.Max(f => CypherLimits.DeepestAt(File.ReadAllText(f)).Depth), 1, CypherLimits.MaxDepth / 8);
    }

    [Fact]
    public void A_syntax_error_reports_its_position()
    {
        var ex = Assert.Throws<CypherException>(() => AstBuilder.Parse("MATCH (n:Character)\nRETURN n.name AS"));
        Assert.StartsWith("syntax error", ex.Reason);
        Assert.Equal(2, ex.Line);
        Assert.True(ex.Column >= 14, $"column {ex.Column}");
        Assert.True(ex.Offset > 19);

        var unclosed = Assert.Throws<CypherException>(() => AstBuilder.Parse("MATCH (n RETURN n"));
        Assert.Equal(1, unclosed.Line);
    }

    [Theory]
    [InlineData("MATCH (a)-[r:MET*1..3]->(b) RETURN b", "variable-length paths", 16)]
    [InlineData("MATCH (a)-[*]->(b) RETURN b", "variable-length paths", 11)]
    [InlineData("CREATE (n:Character {name: 'X'}) RETURN n", "CREATE", 0)]
    [InlineData("MERGE (n:Character {name: 'X'}) RETURN n", "MERGE", 0)]
    [InlineData("MATCH (n) SET n.name = 'X' RETURN n", "SET", 10)]
    [InlineData("MATCH (n) DELETE n", "DELETE", 10)]
    [InlineData("MATCH (n) DETACH DELETE n", "DELETE", 10)]
    [InlineData("MATCH (n) REMOVE n.name RETURN n", "REMOVE", 10)]
    [InlineData("UNWIND [1, 2] AS x RETURN x", "UNWIND", 0)]
    [InlineData("MATCH (n) RETURN n UNION MATCH (m) RETURN m", "UNION", 19)]
    [InlineData("MATCH p = (a)-->(b) RETURN p", "path variables", 6)]
    [InlineData("MATCH (n:Character:Location) RETURN n", "more than one label", 8)]
    [InlineData("MATCH (n) RETURN n.x + 1", "arithmetic", 17)]
    [InlineData("MATCH (n) RETURN toString(n.name)", "the function toString()", 17)]
    [InlineData("MATCH (n) RETURN count(DISTINCT n)", "DISTINCT inside count()", 17)]
    [InlineData("MATCH (n) WHERE n.name ENDS WITH 's' RETURN n", "ENDS WITH", 22)]
    [InlineData("MATCH (n) RETURN CASE WHEN true THEN 1 END AS x", "CASE", 17)]
    [InlineData("MATCH (n) RETURN *", "* in a projection", 17)]
    [InlineData("MATCH (n) WITH n.name RETURN 1 AS x", "without AS", 15)]
    [InlineData("MATCH (n) WHERE count(n) > 1 RETURN n", "aggregate functions in WHERE", 10)]
    [InlineData("MATCH (a)<-[r]->(b) RETURN r", "two arrowheads", 9)]
    [InlineData("MATCH (n) RETURN n.aliases[0] AS x", "list indexing", 26)]
    [InlineData("MATCH (n) WHERE true XOR false RETURN n", "XOR", 16)]
    public void Features_outside_the_subset_say_not_supported_with_a_position(string query, string what, int offset)
    {
        var ex = Assert.Throws<CypherException>(() => CypherEngine.Execute(Graph, query, null, TestContext.Current.CancellationToken));
        Assert.StartsWith("not supported", ex.Reason);
        Assert.Contains(what, ex.Reason);
        Assert.Equal(offset, ex.Offset);
        Assert.Equal(1, ex.Line);
        Assert.Contains($"column {offset + 1}", ex.Message);
    }
}

public class EvaluatorTests
{
    [Fact]
    public void Match_with_label_and_inline_properties_in_id_order()
    {
        Assert.Equal(["Sherlock Holmes", "Dr. Watson", "Dr. Mortimer", "Selden"], Rows(Run("MATCH (c:Character) RETURN c.name")));
        Assert.Equal(["Dr. Watson"], Rows(Run("MATCH (c:Character {name: 'Dr. Watson'}) RETURN c.name AS n")));
        Assert.Equal(["Dr. Watson"], Rows(Run("MATCH (c {name: $n}) RETURN c.name AS n", ("n", "Dr. Watson"))));
        Assert.Equal(["Sherlock Holmes | Dr. Watson"], Rows(Run("MATCH (a)-[:MET {revealedPos: 25}]->(b) RETURN a.name, b.name")));
        Assert.Equal(9, Run("MATCH (n) RETURN n").Rows.Count);
        var r = Run("MATCH (c:Character) RETURN c.name AS name");
        Assert.Equal(["name"], r.Columns);
        Assert.Equal(["c.name"], Run("MATCH (c:Character) RETURN c.name").Columns);
    }

    [Fact]
    public void Directions_undirected_and_type_alternatives()
    {
        Assert.Equal(["Dr. Watson"], Rows(Run("MATCH (a {name: 'Sherlock Holmes'})-[:MET]->(b) RETURN b.name")));
        Assert.Equal(["Dr. Mortimer"], Rows(Run("MATCH (a {name: 'Sherlock Holmes'})<-[:MET]-(b) RETURN b.name")));
        // Undirected: both edges, in edge-id order.
        Assert.Equal(["Dr. Watson | 101", "Dr. Mortimer | 102"], Rows(Run("MATCH (a {name: 'Sherlock Holmes'})-[m:MET]-(b) RETURN b.name, id(m)")));
        Assert.Equal(["MET | Dr. Watson", "MET | Dr. Mortimer", "RELATED_TO | Dr. Watson"],
            Rows(Run("MATCH (a {name: 'Sherlock Holmes'})-[r:MET|RELATED_TO]-(b) RETURN type(r), b.name")));
        Assert.Equal(["MET | Dr. Watson", "MET | Dr. Mortimer", "RELATED_TO | Dr. Watson"],
            Rows(Run("MATCH (a {name: 'Sherlock Holmes'})-[r:MET|:RELATED_TO]-(b) RETURN type(r), b.name")));
        // An anonymous relationship of any type, both directions.
        Assert.Equal(7, Run("MATCH (a {name: 'Sherlock Holmes'})--(b) RETURN b").Rows.Count); // 101 102 103 105 107 108 110
    }

    [Fact]
    public void Comma_patterns_share_variables_and_a_relationship_is_used_once_per_match()
    {
        // Watson's MET partners besides via the same edge: 101 (Holmes) and 112 (Selden).
        Assert.Equal(["Sherlock Holmes | Selden", "Selden | Sherlock Holmes"],
            Rows(Run("MATCH (w {name: 'Dr. Watson'})-[:MET]-(x), (w)-[:MET]-(y) RETURN x.name, y.name")));
        Assert.Equal(["Sherlock Holmes | Baker Street"], Rows(Run("MATCH (c:Character)-[:LOCATED_AT]->(l), (l:Location) RETURN c.name, l.name")));
    }

    [Fact]
    public void Where_operators()
    {
        string Names(string where, params (string, object?)[] ps) => string.Join(", ", Rows(Run($"MATCH (c:Character) WHERE {where} RETURN c.name", ps)));
        Assert.Equal("Dr. Watson", Names("c.name = 'Dr. Watson'"));
        Assert.Equal("Sherlock Holmes, Dr. Mortimer, Selden", Names("c.name <> 'Dr. Watson'"));
        Assert.Equal("Sherlock Holmes", Names("c.evidenceStart < 20"));
        Assert.Equal("Sherlock Holmes, Dr. Watson", Names("c.evidenceStart <= 20"));
        Assert.Equal("Selden", Names("c.evidenceStart > 500"));
        Assert.Equal("Dr. Mortimer, Selden", Names("c.evidenceStart >= 500"));
        Assert.Equal("Dr. Watson, Dr. Mortimer", Names("c.evidenceStart > 10 AND c.evidenceStart < 1000"));
        Assert.Equal("Sherlock Holmes, Selden", Names("c.evidenceStart = 10 OR c.name = 'Selden'"));
        Assert.Equal("Dr. Watson, Dr. Mortimer, Selden", Names("NOT c.evidenceStart = 10"));
        Assert.Equal("Dr. Watson, Dr. Mortimer, Selden", Names("c.evidenceEnd IS NULL"));
        Assert.Equal("Sherlock Holmes", Names("c.evidenceEnd IS NOT NULL"));
        Assert.Equal("Sherlock Holmes, Selden", Names("c.name IN ['Selden', 'Sherlock Holmes', null]"));
        Assert.Equal("Dr. Watson, Dr. Mortimer", Names("c.name STARTS WITH 'Dr.'"));
        Assert.Equal("Sherlock Holmes", Names("c.name CONTAINS 'lock'"));
        Assert.Equal("Sherlock Holmes", Names("$who IN c.aliases", ("who", "Holmes")));
        Assert.Equal("Dr. Mortimer", Names("c.evidenceStart = $n", ("n", 500)));        // an int parameter is an integer
        Assert.Equal("Dr. Mortimer", Names("c.evidenceStart = 500.0"));                 // 500 = 500.0
        Assert.Equal("Dr. Mortimer", Names("c.evidenceStart = -1 OR c.evidenceStart = 500"));
        Assert.Equal("Dr. Watson", Names("c.name = \"Dr. Watson\" AND true"));
    }

    [Fact]
    public void Null_and_mixed_types_follow_openCypher()
    {
        string One(string expr) => Rows(Run($"MATCH (c {{name: 'Selden'}}) RETURN {expr} AS v"))[0];
        Assert.Equal("null", One("null = null"));
        Assert.Equal("null", One("c.missing = 1"));
        Assert.Equal("false", One("1 = 'a'"));            // equality across types: false
        Assert.Equal("null", One("1 < 'a'"));             // comparison across types: null
        Assert.Equal("null", One("'a' < true"));
        Assert.Equal("false", One("false AND null"));
        Assert.Equal("null", One("true AND null"));
        Assert.Equal("true", One("true OR null"));
        Assert.Equal("null", One("NOT null"));
        Assert.Equal("null", One("2 IN [1, null]"));
        Assert.Equal("false", One("null IN []"));
        Assert.Equal("null", One("c.missing STARTS WITH 'a'"));
        Assert.Equal("null", One("c.missing.deeper"));
        Assert.Equal("true", One("c.missing IS NULL"));
        Assert.Equal("true", One("'b' > 'a'"));
        Assert.Equal("true", One("[1, 2] = [1, 2]"));
        // A null WHERE result filters the row out, and so does false.
        Assert.Empty(Run("MATCH (c:Character) WHERE c.missing = 1 RETURN c").Rows);
        Assert.Empty(Run("MATCH (c:Character) WHERE c.name < 5 RETURN c").Rows);
    }

    [Fact]
    public void Functions()
    {
        Assert.Equal(["4 | Location | Baker Street"], Rows(Run("MATCH (l {name: 'Baker Street'}) RETURN id(l), labels(l), l.name")));
        Assert.Equal(["RELATED_TO | friend"], Rows(Run("MATCH ()-[r:RELATED_TO]->() RETURN type(r), r.kind")));
        Assert.Equal(["x | 10"], Rows(Run("MATCH (c {name: 'Sherlock Holmes'}) RETURN coalesce(c.missing, 'x'), coalesce(c.missing, c.evidenceStart, 1)")));
        Assert.Equal(["null"], Rows(Run("MATCH (c {name: 'Sherlock Holmes'}) RETURN coalesce(c.missing, null)")));
        Assert.Equal(["null | null"], Rows(Run("MATCH (c:Character {name: 'Selden'}) OPTIONAL MATCH (c)-[r:LOCATED_AT]->(l) RETURN type(r), id(l)")));
    }

    [Fact]
    public void Aggregation_groups_on_the_non_aggregated_items()
    {
        Assert.Equal(["Character | 4", "Location | 2", "Fact | 2", "Event | 1"], Rows(Run("MATCH (n) RETURN labels(n) AS label, count(*) AS n")));
        Assert.Equal(["9"], Rows(Run("MATCH (n) RETURN count(n)")));
        Assert.Equal(["1"], Rows(Run("MATCH (n) RETURN count(n.evidenceEnd)")));    // count(x) skips nulls
        Assert.Equal(["Sherlock Holmes | Dr. Watson, Dr. Mortimer"], Rows(Run("MATCH (a {name: 'Sherlock Holmes'})-[:MET]-(b) RETURN a.name, collect(b.name)")));
        Assert.Equal(["Dr. Watson, Dr. Mortimer"], Rows(Run("MATCH (c:Character) WHERE c.evidenceStart > 10 AND c.evidenceStart < 1000 RETURN collect(c.name) AS names")));
        // No rows: an aggregate without grouping keys still gives one row; with grouping keys, none.
        Assert.Equal(["0 | "], Rows(Run("MATCH (n:Nothing) RETURN count(*), collect(n.name)")));
        Assert.Empty(Run("MATCH (n:Nothing) RETURN n.name, count(*)").Rows);
        // Grouping keys use equivalence: null groups with null.
        Assert.Equal(["null | 3", "25 | 1"], Rows(Run("MATCH (c:Character) RETURN c.evidenceEnd AS e, count(*) AS n ORDER BY e DESC")));
    }

    [Fact]
    public void Distinct_order_skip_limit()
    {
        Assert.Equal(["Dr. Watson", "Dr. Mortimer", "Sherlock Holmes"], Rows(Run("MATCH (a:Character)-[:MET]-(b) WHERE b.name <> 'Selden' RETURN DISTINCT b.name")));
        Assert.Equal(6, Run("MATCH (a:Character)-[:MET]-(b) RETURN b.name").Rows.Count);   // 3 MET edges, both ways
        Assert.Equal(["Selden", "Dr. Mortimer", "Dr. Watson", "Sherlock Holmes"], Rows(Run("MATCH (c:Character) RETURN c.name ORDER BY c.evidenceStart DESC")));
        Assert.Equal(["Dr. Mortimer", "Dr. Watson"], Rows(Run("MATCH (c:Character) RETURN c.name AS n ORDER BY n SKIP 0 LIMIT 2")));
        Assert.Equal(["Sherlock Holmes"], Rows(Run("MATCH (c:Character) RETURN c.name AS n ORDER BY n DESC LIMIT $k", ("k", 1))));
        Assert.Equal(["Selden", "Dr. Watson"], Rows(Run("MATCH (c:Character) RETURN c.name AS n ORDER BY n DESC SKIP 1 LIMIT 2")));
        // Nulls sort last ascending, first descending; ties keep match order.
        Assert.Equal(["Dr. Watson | null", "Dr. Mortimer | null", "Selden | null", "Sherlock Holmes | 25"],
            Rows(Run("MATCH (c:Character) RETURN c.name, c.evidenceEnd ORDER BY c.evidenceEnd DESC")));
        Assert.Equal(["Sherlock Holmes | 25", "Dr. Watson | null"], Rows(Run("MATCH (c:Character) RETURN c.name, c.evidenceEnd ORDER BY c.evidenceEnd LIMIT 2")));
        // ORDER BY on two keys.
        Assert.Equal(["ABOUT | 105", "ABOUT | 104", "ABOUT | 103", "LOCATED_AT | 109"], Rows(Run("MATCH ()-[r]->() RETURN type(r) AS t, id(r) AS i ORDER BY t, i DESC LIMIT 4")));
        var e = Assert.Throws<CypherException>(() => Run("MATCH (c) RETURN c LIMIT -1"));
        Assert.Contains("non-negative integer", e.Reason);
    }

    [Fact]
    public void With_projects_and_filters()
    {
        Assert.Equal(["Sherlock Holmes | 2", "Dr. Watson | 2"], Rows(Run("MATCH (c:Character)-[m:MET]-() WITH c, count(m) AS met WHERE met >= 2 RETURN c.name, met")));
        Assert.Equal(["Dr. Watson"], Rows(Run("MATCH (c:Character) WITH c.name AS n, c.evidenceStart AS s WHERE s = 20 RETURN n")));
        // Variables not carried by WITH are gone.
        var e = Assert.Throws<CypherException>(() => Run("MATCH (c:Character) WITH c.name AS n RETURN c.name"));
        Assert.Contains("variable 'c' is not defined", e.Reason);
        Assert.Equal(44, e.Offset);
    }

    [Fact]
    public void Optional_match_with_no_match_gives_nulls_and_where_applies_inside()
    {
        Assert.Equal(["Selden | null"], Rows(Run("MATCH (c {name: 'Selden'}) OPTIONAL MATCH (c)-[:LOCATED_AT]->(l) RETURN c.name, l.name")));
        Assert.Equal(["Sherlock Holmes | Baker Street"], Rows(Run("MATCH (c {name: 'Sherlock Holmes'}) OPTIONAL MATCH (c)-[:LOCATED_AT]->(l) RETURN c.name, l.name")));
        // The WHERE of an OPTIONAL MATCH filters the optional part, not the row.
        Assert.Equal(["Sherlock Holmes | null"], Rows(Run("MATCH (c {name: 'Sherlock Holmes'}) OPTIONAL MATCH (c)-[:LOCATED_AT]->(l) WHERE l.name = 'Mars' RETURN c.name, l.name")));
        Assert.Equal(["Selden | 0"], Rows(Run("MATCH (c {name: 'Selden'}) OPTIONAL MATCH (c)-[:LOCATED_AT]->(l) RETURN c.name, count(l)")));
    }

    [Fact]
    public void Parameters_bind_and_a_missing_one_is_an_error()
    {
        Assert.Equal(["Dr. Mortimer"], Rows(Run("MATCH (c:Character) WHERE $alias IN c.aliases RETURN c.name", ("alias", "Mortimer"))));
        Assert.Equal(["Sherlock Holmes", "Selden"], Rows(Run("MATCH (c:Character) WHERE c.name IN $names RETURN c.name", ("names", new[] { "Selden", "Sherlock Holmes" }))));
        var e = Assert.Throws<CypherException>(() => Run("MATCH (c:Character)\nWHERE c.name = $nope RETURN c"));
        Assert.Contains("missing parameter $nope", e.Reason);
        Assert.Equal((2, 15), (e.Line, e.Column));
    }

    [Fact]
    public void Type_errors_are_errors_not_wrong_answers()
    {
        Assert.Contains("type error", Assert.Throws<CypherException>(() => Run("MATCH (c:Character) WHERE c.name RETURN c")).Reason);
        Assert.Contains("type error", Assert.Throws<CypherException>(() => Run("MATCH (c:Character) RETURN c.name.x")).Reason);
        Assert.Contains("type error", Assert.Throws<CypherException>(() => Run("MATCH (c:Character) WHERE 'a' IN 'abc' RETURN c")).Reason);
        Assert.Contains("type error", Assert.Throws<CypherException>(() => Run("MATCH (c:Character) RETURN type(c)")).Reason);
    }
}

public class QueryFileTests
{
    private static QueryResult File_(string name, params (string, object?)[] ps) =>
        Run(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "queries", name)), ps);

    private static readonly (string, object?) Pos = ("pos", 1000L);
    private static readonly (string, object?) Off = ("spoilers", false);
    private static readonly (string, object?) On = ("spoilers", true);
    private static readonly (string, object?) Holmes = ("character", "Holmes");

    [Fact]
    public void Characters_so_far() =>
        Assert.Equal(["Sherlock Holmes | 1", "Dr. Watson | 2", "Dr. Mortimer | 3"], Rows(File_("characters-so-far.cypher", Pos)));

    [Fact]
    public void Characters_so_far_depends_on_the_position() =>
        Assert.Equal(["Sherlock Holmes | 1", "Dr. Watson | 2"], Rows(File_("characters-so-far.cypher", ("pos", 100L))));

    [Fact]
    public void Who_met() => Assert.Equal(["Dr. Watson | 2 | 101", "Dr. Mortimer | 3 | 102"], Rows(File_("who-met.cypher", Holmes, Pos, Off)));

    [Fact]
    public void Who_met_hides_what_is_not_revealed_yet_unless_spoilers_are_on()
    {
        Assert.Equal(["Dr. Watson | 2 | 101"], Rows(File_("who-met.cypher", Holmes, ("pos", 100L), Off)));
        Assert.Equal(["Dr. Watson | 2 | 101", "Dr. Mortimer | 3 | 102"], Rows(File_("who-met.cypher", Holmes, ("pos", 100L), On)));
        // Watson met Selden only in chapter 2 (revealedPos 2100).
        Assert.Equal(["Sherlock Holmes | 1 | 101"], Rows(File_("who-met.cypher", ("character", "Watson"), Pos, Off)));
        Assert.Equal(["Sherlock Holmes | 1 | 101", "Selden | 9 | 112"], Rows(File_("who-met.cypher", ("character", "Watson"), Pos, On)));
    }

    [Fact]
    public void Facts_about() =>
        // Fact 7 is about Mortimer and Holmes: its about list names both (ABOUT edges in id order); aboutIds holds their node ids;
        // factId and edgeId (the ABOUT edge to the asked character) are for the graph highlight.
        Assert.Equal(["Holmes is late in the mornings. | Sherlock Holmes | 12 | 1 | 6 | 103", "Mortimer's stick dates from 1884. | Dr. Mortimer, Sherlock Holmes | 510 | 3, 1 | 7 | 105"],
            Rows(File_("facts-about.cypher", Holmes, Pos, Off)));

    [Fact]
    public void Facts_about_a_location()
    {
        // No label on c: a Location with aliases can be asked about.
        var graph = new BookGraph(
        [
            new(1, ["Location"], new Dictionary<string, object?> { ["name"] = "Baskerville Hall", ["aliases"] = new List<object?> { "Baskerville Hall", "the Hall" } }),
            new(2, ["Character"], new Dictionary<string, object?> { ["name"] = "Barrymore", ["aliases"] = new List<object?> { "Barrymore" } }),
            new(3, ["Fact"], new Dictionary<string, object?> { ["text"] = "Barrymore is the butler at the Hall." }),
        ],
        [
            new(10, "ABOUT", 3, 2, new Dictionary<string, object?> { ["revealedPos"] = 5L }),
            new(11, "ABOUT", 3, 1, new Dictionary<string, object?> { ["revealedPos"] = 5L }),
        ]);
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "queries", "facts-about.cypher"));
        var result = CypherEngine.Execute(graph, text, new Dictionary<string, object?> { ["character"] = "the Hall", ["pos"] = 10L, ["spoilers"] = false },
            TestContext.Current.CancellationToken);
        Assert.Equal(["Barrymore is the butler at the Hall. | Barrymore, Baskerville Hall | 5 | 2, 1 | 3 | 11"], Rows(result));
    }

    [Fact]
    public void At_location_lists_people_and_events_placed_there() =>
        Assert.Equal(["Sherlock Holmes |  | null | 1 | 108", "null | Mortimer visits Baker Street. | null | 8 | 109"],
            Rows(File_("at-location.cypher", ("location", "Baker Street"), Pos, Off)));

    [Fact]
    public void At_location_hides_what_is_not_revealed_yet() =>
        Assert.Equal(["Sherlock Holmes |  | null | 1 | 108"], Rows(File_("at-location.cypher", ("location", "Baker Street"), ("pos", 100L), Off)));

    [Fact]
    public void Facts_about_with_spoilers_ignores_the_position() =>
        Assert.Equal(2, File_("facts-about.cypher", Holmes, ("pos", 0L), On).Rows.Count);

    [Fact]
    public void Events_for_character_in_reading_order() =>
        Assert.Equal(["Mortimer visits Baker Street. | null | 600 | 8 | 107"], Rows(File_("events-for-character.cypher", Holmes, Pos, Off)));

    [Fact]
    public void Where() => Assert.Equal(["Sherlock Holmes | Baker Street | null | 1 | 4 | 108"], Rows(File_("where.cypher", Pos, Off)));

    [Fact]
    public void Relations() => Assert.Equal(["Dr. Watson | friend | 2 | 110"], Rows(File_("relations.cypher", Holmes, Pos, Off)));

    [Fact]
    public void Most_discussed() => Assert.Equal(["Sherlock Holmes | 2 | 1", "Dr. Mortimer | 1 | 3"], Rows(File_("most-discussed.cypher", Pos, Off)));

    [Fact]
    public void True_in_story_by_is_distinct_and_needs_no_parameters()
    {
        Assert.Equal(["Mortimer's stick dates from 1884. | 1884 | 1884-12-31 | 510", "Holmes is late in the mornings. | 1889~ | 1889-12-31 | 12"], Rows(File_("true-in-story-by.cypher")));
        // Fact 7 has two ABOUT edges (Mortimer, Holmes) with the same date: DISTINCT gives one row. Without it, two.
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "queries", "true-in-story-by.cypher"));
        Assert.Contains("RETURN DISTINCT", text);
    }

    [Fact]
    public void Unmentioned_optional() => Assert.Equal(["Dr. Watson", "Dr. Mortimer"], Rows(File_("unmentioned-optional.cypher", Pos)));

    [Fact]
    public void Every_query_with_a_reveal_filter_takes_spoilers()
    {
        foreach (var f in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "queries"), "*.cypher"))
        {
            var text = File.ReadAllText(f);
            if (!text.Contains("revealedPos <=")) continue;
            Assert.Contains("($spoilers OR ", text);
            Assert.Contains("$spoilers", text.ReplaceLineEndings("\n").Split('\n')[1]);   // named in the header comment
        }
    }
}

public class BookGraphTests
{
    [Fact]
    public void Load_maps_the_schema_and_opens_read_only()
    {
        var dir = Path.Combine(Path.GetTempPath(), "TemporalGraph.Query.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "book.sqlite");
        try
        {
            BookDatabase.Create(path);
            using (var db = BookDatabase.OpenReadWrite(path))
            {
                var a = new Node { Properties = "{\"name\":\"Holmes\",\"aliases\":[\"Holmes\"],\"evidenceStart\":999,\"n\":2.5,\"gone\":null}", EvidenceStart = 10, EvidenceEnd = 16, Labels = [new NodeLabel { Label = "Character" }] };
                var b = new Node { Properties = "{\"text\":\"x\"}", EvidenceStart = 20, Labels = [new NodeLabel { Label = "Fact" }] };
                db.Nodes.AddRange(a, b);
                db.Edges.Add(new Edge { Source = b, Target = a, Type = "ABOUT", Properties = "{\"kind\":\"k\",\"stage3Id\":\"N1\"}", RevealedPos = 20, EvidenceStart = 20, EvidenceEnd = 30,
                    StoryFromEdtf = "1884~", StoryFromLo = "1884-01-01", StoryFromHi = "1884-12-31", FabulaSeq = 1.5 });
                db.Chapters.Add(new Chapter { Number = 1, Title = "One", CharStart = 0, CharEnd = 100 });
                db.SaveChanges();
            }

            var graph = BookGraph.Load(path);
            var holmes = graph.Nodes[0];
            Assert.Equal(["Character"], holmes.Labels);
            Assert.Equal(10L, holmes.Properties["evidenceStart"]);          // the column wins over the JSON key
            Assert.Equal(["node 1: evidenceStart"], graph.Collisions);
            Assert.Equal(16L, holmes.Properties["evidenceEnd"]);
            Assert.Equal(2.5, holmes.Properties["n"]);
            Assert.False(holmes.Properties.ContainsKey("gone"));
            Assert.Equal(["Holmes"], (IEnumerable<object?>)holmes.Properties["aliases"]!);
            var about = graph.Relationships.Single();
            Assert.Equal(("ABOUT", 2L, 1L), (about.Type, about.StartId, about.EndId));
            Assert.Equal("k", about.Properties["kind"]);
            Assert.Equal(20L, about.Properties["revealedPos"]);
            Assert.Equal("1884~", about.Properties["storyFrom"]);
            Assert.Equal("1884-12-31", about.Properties["storyFromHi"]);
            Assert.Equal(1.5, about.Properties["fabulaSeq"]);
            Assert.False(about.Properties.ContainsKey("retractedPos"));     // null column → missing → null
            Assert.Equal((0, 100), graph.Chapters[1]);
            Assert.Equal(["x | Holmes | 1884~ | null"],
                Rows(CypherEngine.Execute(graph, "MATCH (f:Fact)-[r:ABOUT]->(c:Character) RETURN f.text, c.name, r.storyFrom, r.retractedPos", null, TestContext.Current.CancellationToken)));

            // The connection Load reads through is read-only: a write fails.
            using (var ro = BookGraph.Open(path))
            {
                var ex = Assert.Throws<SqliteException>(() => ro.Database.ExecuteSqlRaw("INSERT INTO node_labels (node_id, label) VALUES (1, 'Location')"));
                Assert.Equal(8, ex.SqliteErrorCode); // SQLITE_READONLY
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dir, recursive: true);
        }
    }
}

// The engine additions search-facts.cypher needs: label predicates in expressions, toLower/toUpper, min/max.
public class SearchAdditionsTests
{
    [Fact]
    public void Label_predicates_in_where_and_return()
    {
        Assert.Equal(["Holmes is late in the mornings.", "Mortimer's stick dates from 1884.", "Mortimer visits Baker Street."],
            Rows(Run("MATCH (n) WHERE n:Fact OR n:Event RETURN n.text")));
        Assert.Equal(["Mortimer visits Baker Street."], Rows(Run("MATCH (n) WHERE n:Event AND NOT n:Fact RETURN n.text")));
        Assert.Equal(["true", "false"], Rows(Run("MATCH (n) WHERE id(n) IN [1, 4] RETURN n:Character AS c")));
        Assert.Equal(["false"], Rows(Run("MATCH (n) WHERE id(n) = 1 RETURN n:Character:Location AS both")));
        Assert.Equal(["null"], Rows(Run("OPTIONAL MATCH (n:Nothing) RETURN n:Character AS c")));
        var ex = Assert.Throws<CypherException>(() => Run("MATCH (n) WHERE n.name:Character RETURN n"));
        Assert.Contains("label predicate needs a node", ex.Reason);
    }

    [Fact]
    public void ToLower_and_toUpper()
    {
        Assert.Equal(["sherlock holmes | SHERLOCK HOLMES"], Rows(Run("MATCH (c:Character {name: 'Sherlock Holmes'}) RETURN toLower(c.name), toUpper(c.name)")));
        Assert.Equal(["Sherlock Holmes"], Rows(Run("MATCH (c:Character) WHERE toLower(c.name) CONTAINS toLower($t) RETURN c.name", ("t", "HOLMES"))));
        Assert.Equal(["null"], Rows(Run("MATCH (c:Character {name: 'Selden'}) RETURN toLower(c.text) AS t")));
        Assert.Contains("toLower() needs a string", Assert.Throws<CypherException>(() => Run("MATCH (c:Character) RETURN toLower(c.evidenceStart)")).Reason);
    }

    [Fact]
    public void Min_and_max_aggregate_skipping_nulls()
    {
        Assert.Equal(["Mortimer's stick dates from 1884. | 510 | 510", "Holmes is late in the mornings. | 12 | 12"],
            Rows(Run("MATCH (f:Fact)-[r:ABOUT]->(x) WITH f, min(r.revealedPos) AS lo, max(r.revealedPos) AS hi RETURN f.text, lo, hi ORDER BY f.text DESC")));
        Assert.Equal(["20 | 2000"], Rows(Run("MATCH (c:Character) WHERE c.name <> 'Sherlock Holmes' RETURN min(c.evidenceStart), max(c.evidenceStart)")));
        Assert.Equal(["null"], Rows(Run("MATCH (c:Character) RETURN min(c.text) AS m")));
        Assert.Contains("aggregate inside an aggregate", Assert.Throws<CypherException>(() => Run("MATCH (c) RETURN min(count(c))")).Reason);
    }

    // No book is of the earlier shape any more (every book's samples are run in FullSchemaQueryTests); the earlier shape's
    // search files are the fixture copies (tests/fixtures/queries-earlier).
    [Fact]
    public void Search_files_of_the_earlier_shape_find_by_text_or_name()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "queries");
        var facts = File.ReadAllText(Path.Combine(dir, "search-facts.cypher"));
        var events = File.ReadAllText(Path.Combine(dir, "search-events.cypher"));
        // By fact text ("stick") and by the name of what it is about ("holmes": both facts are about Holmes).
        Assert.Equal(["Mortimer's stick dates from 1884. | Dr. Mortimer, Sherlock Holmes | 510"],
            Rows(Run(facts, ("term", "STICK"), ("pos", 3000L), ("spoilers", false))).Select(r => string.Join(" | ", r.Split(" | ").Take(3))));
        Assert.Equal(2, Run(facts, ("term", "holmes"), ("pos", 3000L), ("spoilers", false)).Rows.Count);
        Assert.DoesNotContain(Run(facts, ("term", "holmes"), ("pos", 100L), ("spoilers", false)).Rows, r => (string)r[0]! == "Mortimer's stick dates from 1884.");
        // Events through PARTICIPATED_IN / LOCATED_AT: the participant's name or the place.
        Assert.Equal(["Mortimer visits Baker Street."], Run(events, ("term", "baker"), ("pos", 3000L), ("spoilers", false)).Rows.Select(r => (string)r[0]!));
        Assert.Equal(["Mortimer visits Baker Street."], Run(events, ("term", "sherlock"), ("pos", 3000L), ("spoilers", false)).Rows.Select(r => (string)r[0]!));
        Assert.Empty(Run(events, ("term", "baker"), ("pos", 500L), ("spoilers", false)).Rows);
        Assert.Single(Run(events, ("term", "baker"), ("pos", 500L), ("spoilers", true)).Rows);
    }
}

public static class SearchFiles
{
    /// <summary>The repo root (the folder with TemporalGraph.slnx above the test binaries).</summary>
    public static string Root()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "TemporalGraph.slnx"))) return d.FullName;
        throw new DirectoryNotFoundException("TemporalGraph.slnx");
    }
}
