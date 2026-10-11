using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;

namespace TemporalGraph.Api.Tests;

// The API over HTTP (WebApplicationFactory), on the committed (embedded) Hound data.
public sealed class GraphQlTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private const string Hound = "hound-of-the-baskervilles";

    private static async Task<JsonNode> Post(HttpClient client, string query, object? variables = null)
    {
        var response = await client.PostAsJsonAsync("/graphql", new { query, variables }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);   // errors are GraphQL errors, never a 500
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
    }

    private Task<JsonNode> Post(string query, object? variables = null) => Post(factory.CreateClient(), query, variables);

    private const string CypherQuery = """
        query($book: String!, $q: String!, $ch: Int!, $c: String, $s: Boolean!) {
          cypher(bookId: $book, query: $q, chapter: $ch, character: $c, spoilers: $s) { columns rows truncated elapsedMs }
        }
        """;

    [Fact]
    public async Task Books_have_ids_and_hound_has_chapters_and_characters()
    {
        var r = await Post($"{{ books {{ id title author hero }} chapters(bookId: \"{Hound}\") {{ number title }} characters(bookId: \"{Hound}\") }}");
        Assert.Null(r["errors"]);
        Assert.Contains(r["data"]!["books"]!.AsArray(), b => b!["id"]!.GetValue<string>() == Hound);
        // Each book's hero, relative to web/public; the file exists there.
        var hero = r["data"]!["books"]!.AsArray().Single(b => b!["id"]!.GetValue<string>() == Hound)!["hero"]!.GetValue<string>();
        Assert.Equal("books/hound-of-the-baskervilles.webp", hero);
        Assert.True(File.Exists(Path.Combine(RepoRoot(), "web", "public", hero)));
        var chapters = r["data"]!["chapters"]!.AsArray();
        Assert.Equal(15, chapters.Count);
        Assert.Equal(1, chapters[0]!["number"]!.GetValue<int>());
        Assert.False(string.IsNullOrEmpty(chapters[0]!["title"]?.GetValue<string>()));
        var characters = r["data"]!["characters"]!.AsArray().Select(c => c!.GetValue<string>()).ToList();
        Assert.Equal("Sherlock Holmes", characters[0]);        // first mention
        Assert.Contains("Dr. Watson", characters);
        Assert.Equal(characters.Count, characters.Distinct().Count());
    }

    [Fact]
    public async Task Graph_payload_is_the_whole_promoted_graph_matching_the_provenance_counts()
    {
        var r = await Post("{ graph(bookId: \"hound-of-the-baskervilles\") { nodes { id label name text pos aliasFirstPos { alias pos } } edges { id source target type pos } chapters { number charEnd } } }");
        Assert.Null(r["errors"]);
        var g = r["data"]!["graph"]!;
        var provenance = JsonNode.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "data", "books", "provenance", $"{Hound}.json")))!["counts"]!;
        var nodes = g["nodes"]!.AsArray();
        Assert.Equal(provenance["rows"]!["nodes"]!.GetValue<int>(), nodes.Count);
        Assert.Equal(provenance["rows"]!["edges"]!.GetValue<int>(), g["edges"]!.AsArray().Count);
        Assert.Equal(provenance["rows"]!["chapters"]!.GetValue<int>(), g["chapters"]!.AsArray().Count);
        foreach (var (label, count) in provenance["labels"]!.AsObject())
            Assert.Equal(count!.GetValue<int>(), nodes.Count(n => n!["label"]!.GetValue<string>() == label));

        Assert.Equal(16159, g["chapters"]![0]!["charEnd"]!.GetValue<int>());   // the end of chapter 1
        var holmes = nodes.Single(n => n!["name"]?.GetValue<string>() == "Sherlock Holmes")!;
        Assert.NotEmpty(holmes["aliasFirstPos"]!.AsArray());
        Assert.All(nodes.Where(n => n!["text"] is not null), n => Assert.True(n!["text"]!.GetValue<string>().Length <= 160));
        Assert.All(nodes.Where(n => n!["label"]!.GetValue<string>() is "Fact" or "Event"), n => Assert.Null(n!["name"]?.GetValue<string>()));
    }

    // The stored layout goes out with each node; a node of a book no layout has been computed for carries null.
    [Fact]
    public async Task A_node_carries_its_stored_layout_and_null_when_the_book_has_none()
    {
        var graph = new TemporalGraph.Query.BookGraph(
            [new(1, ["Character"], new Dictionary<string, object?> { ["name"] = "A", ["layoutX"] = 1.5, ["layoutY"] = -2.25, ["layoutZ"] = 30L, ["layout2X"] = 4.0, ["layout2Y"] = 5.5 }),
             new(2, ["Fact"], new Dictionary<string, object?> { ["text"] = "No position." }),
             new(3, ["Fact"], new Dictionary<string, object?> { ["text"] = "Half a position.", ["layoutX"] = 1.0, ["layout2X"] = 2.0 })],
            [], new Dictionary<int, (int, int)> { [1] = (0, 100) });
        var p = BookGraphPayload.From(graph);
        Assert.Equal((new GraphLayout3(1.5, -2.25, 30), new GraphLayout2(4, 5.5)), (p.Nodes[0].Layout, p.Nodes[0].Layout2));
        Assert.Equal((null, null), (p.Nodes[1].Layout, p.Nodes[1].Layout2));
        Assert.Equal((null, null), (p.Nodes[2].Layout, p.Nodes[2].Layout2));

        // Through GraphQL: the fields are there for every node, each either whole or null (a promoted book has a layout once
        // `index layout` has been run on it and the API rebuilt).
        var r = await Post("{ graph(bookId: \"hound-of-the-baskervilles\") { nodes { id layout { x y z } layout2 { x y } } } }");
        Assert.Null(r["errors"]);
        var nodes = r["data"]!["graph"]!["nodes"]!.AsArray();
        Assert.NotEmpty(nodes);
        Assert.All(nodes, n => Assert.Equal(n!["layout"] is null, n["layout2"] is null));
        Assert.True(nodes.All(n => n!["layout"] is null) || nodes.All(n => n!["layout"] is not null));
    }

    [Fact]
    public void Long_fact_text_is_cut_to_160_characters_with_an_ellipsis()
    {
        // The Hound graph's longest text is 155 characters, so the cap is checked on a hand-built graph.
        var graph = new TemporalGraph.Query.BookGraph(
            [new(1, ["Fact"], new Dictionary<string, object?> { ["text"] = new string('x', 200), ["evidenceStart"] = 5L }),
             new(2, ["Character"], new Dictionary<string, object?> { ["name"] = "A", ["aliasFirstPos"] = new Dictionary<string, object?> { ["A"] = 3.0, ["Mr. A"] = 9.0 } })],
            [new(10, "ABOUT", 1, 2, new Dictionary<string, object?> { ["revealedPos"] = 7L })],
            new Dictionary<int, (int, int)> { [1] = (0, 100) });
        var p = BookGraphPayload.From(graph);
        Assert.Equal(160, p.Nodes[0].Text!.Length);
        Assert.EndsWith("…", p.Nodes[0].Text);
        Assert.Equal(5, p.Nodes[0].Pos);
        Assert.Equal([new AliasPos("A", 3), new AliasPos("Mr. A", 9)], p.Nodes[1].AliasFirstPos);
        Assert.Equal(new GraphEdgeView(10, 1, 2, "ABOUT", 7), p.Edges.Single());
        Assert.Equal(new GraphChapterView(1, 100), p.Chapters.Single());
    }

    private static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "TemporalGraph.slnx"))) return d.FullName;
        throw new DirectoryNotFoundException("TemporalGraph.slnx");
    }

    [Fact]
    public async Task Sample_queries_come_from_the_committed_files()
    {
        var r = await Post($"{{ sampleQueries(bookId: \"{Hound}\") {{ name question text }} }}");
        Assert.Null(r["errors"]);
        var samples = r["data"]!["sampleQueries"]!.AsArray();
        Assert.Equal(10 + 16, samples.Count);   // The book's 10 samples; the 16 tool queries (+ events-of, facts-discovered-by, perceptions-of, referenced-events)
        var who = samples.Single(s => s!["name"]!.GetValue<string>() == "who-met")!;
        Assert.Equal("Whom has this character met? A meeting is an Event both took part in (there is no MET edge).", who["question"]!.GetValue<string>());
        Assert.StartsWith("MATCH (a:Character)-[p:PARTICIPATED_IN]->(e:Event)<-[q:PARTICIPATED_IN]-(b:Character)", who["text"]!.GetValue<string>());
        Assert.All(samples, s => Assert.DoesNotContain("//", s!["text"]!.GetValue<string>()));
    }

    public static TheoryData<string> Books => new("hound-of-the-baskervilles", "three-musketeers", "treasure-island");

    [Theory]
    [MemberData(nameof(Books))]
    public async Task The_books_own_samples_come_first_then_the_tool_queries(string book)
    {
        var samples = (await Post($"{{ sampleQueries(bookId: \"{book}\") {{ name question group }} }}"))["data"]!["sampleQueries"]!.AsArray();
        var groups = samples.Select(s => s!["group"]!.GetValue<string>()).ToList();
        // The three books are on the full schema: 10 samples and 16 tool queries each.
        var (own, tools) = (10, 16);
        Assert.Equal([.. Enumerable.Repeat("sample", own), .. Enumerable.Repeat("tool", tools)], groups);
        // Samples by NN (01-…); the tool queries by name, as before.
        var names = samples.Select(s => s!["name"]!.GetValue<string>()).ToList();
        Assert.Equal(Enumerable.Range(1, own).Select(i => $"{i:00}-"), names.Take(own).Select(n => n[..3]));
        Assert.Equal(names.Skip(own).Order(StringComparer.Ordinal), names.Skip(own));
        Assert.Equal("Who do the facts talk about most?", samples[7]!["question"]!.GetValue<string>());
    }

    [Fact]
    public void A_samples_header_gives_its_question_and_the_text_without_comments()
    {
        var q = SampleQueries.Parse("01-who-watson-has-met",
            "// Whom has Dr. Watson met so far?\n// MET edges of Dr. Watson.\n// Parameters: $pos, $spoilers.\nMATCH (a) RETURN a\n");
        Assert.Equal(("01-who-watson-has-met", "Whom has Dr. Watson met so far?", "MATCH (a) RETURN a", "tool"), (q.Name, q.Question, q.Text, q.Group));
    }

    public static TheoryData<string, string, int> AllSamples()
    {
        var data = new TheoryData<string, string, int>();
        foreach (var (book, last) in new[] { ("hound-of-the-baskervilles", 15), ("three-musketeers", 67), ("treasure-island", 34) })
            foreach (var s in SampleQueries.For(book).Where(s => s.Group == SampleQueries.Sample)) data.Add(book, s.Name, last);
        return data;
    }

    [Theory]
    [MemberData(nameof(AllSamples))]
    public async Task Every_book_sample_returns_rows_at_the_last_chapter_with_spoilers(string book, string name, int lastChapter)
    {
        var text = SampleQueries.For(book).Single(s => s.Name == name).Text;

        var r = await Post(CypherQuery, new { book, q = text, ch = lastChapter, c = (string?)null, s = true });

        Assert.Null(r["errors"]);
        Assert.NotEmpty(r["data"]!["cypher"]!["rows"]!.AsArray());
        // The id columns stay for the highlight; the grid hides them.
        Assert.Contains(r["data"]!["cypher"]!["columns"]!.AsArray(), c => c!.GetValue<string>().EndsWith("Id", StringComparison.Ordinal));
    }

    [Fact]
    public void Every_book_has_its_samples()
    {
        Assert.Equal(30, AllSamples().Count());   // ten a book
    }

    public static TheoryData<string> SampleNames => new(
        "characters-so-far", "events-for-character", "facts-about", "most-discussed", "relations", "true-in-story-by",
        "unmentioned-optional", "where", "who-met");

    [Fact]
    public async Task At_location_sample_needs_a_location_the_api_does_not_bind()
    {
        // at-location takes $location; the API binds only $pos, $chapter, $character and $spoilers (open: a $location input).
        var samples = (await Post($"{{ sampleQueries(bookId: \"{Hound}\") {{ name text }} }}"))["data"]!["sampleQueries"]!.AsArray();
        var text = samples.Single(s => s!["name"]!.GetValue<string>() == "at-location")!["text"]!.GetValue<string>();

        var r = await Post(CypherQuery, new { book = Hound, q = text, ch = 3, c = "Sherlock Holmes", s = false });

        Assert.Contains("location", r["errors"]![0]!["message"]!.GetValue<string>());
    }

    [Theory]
    [MemberData(nameof(SampleNames))]
    public async Task Each_sample_query_runs_on_the_committed_hound_data_at_chapter_3(string name)
    {
        var samples = (await Post($"{{ sampleQueries(bookId: \"{Hound}\") {{ name text }} }}"))["data"]!["sampleQueries"]!.AsArray();
        var text = samples.Single(s => s!["name"]!.GetValue<string>() == name)!["text"]!.GetValue<string>();

        var r = await Post(CypherQuery, new { book = Hound, q = text, ch = 3, c = "Sherlock Holmes", s = false });

        Assert.Null(r["errors"]);
        var result = r["data"]!["cypher"]!;
        Assert.NotEmpty(result["columns"]!.AsArray());
        Assert.False(result["truncated"]!.GetValue<bool>());
        Assert.True(result["elapsedMs"]!.GetValue<int>() >= 0);
        // Relations has rows too (Holmes – Watson "friend", "companion" by ch. 3).
        Assert.NotEmpty(result["rows"]!.AsArray());
    }

    [Fact]
    public async Task Parameters_come_from_chapter_character_and_spoilers()
    {
        var r = await Post(CypherQuery, new { book = Hound, q = "MATCH (n:Character) RETURN $pos AS pos, $chapter AS chapter, $character AS c, $spoilers AS s LIMIT 1", ch = 1, c = (string?)null, s = true });
        Assert.Null(r["errors"]);
        var row = r["data"]!["cypher"]!["rows"]![0]!.AsArray();
        Assert.Equal("16159", row[0]!.GetValue<string>());   // the end of chapter 1
        Assert.Equal("1", row[1]!.GetValue<string>());
        Assert.Null(row[2]);
        Assert.Equal("true", row[3]!.GetValue<string>());

        var ch1 = await Post(CypherQuery, new { book = Hound, q = "MATCH (c:Character) WHERE c.evidenceStart < $pos RETURN c.name", ch = 1, c = (string?)null, s = false });
        var ch3 = await Post(CypherQuery, new { book = Hound, q = "MATCH (c:Character) WHERE c.evidenceStart < $pos RETURN c.name", ch = 3, c = (string?)null, s = false });
        Assert.True(ch1["data"]!["cypher"]!["rows"]!.AsArray().Count < ch3["data"]!["cypher"]!["rows"]!.AsArray().Count);
    }

    [Fact]
    public async Task A_syntax_error_is_a_graphql_error_with_its_position()
    {
        var r = await Post(CypherQuery, new { book = Hound, q = "MATCH (n:Character)\nRETURN n.name AS", ch = 3, c = (string?)null, s = false });
        var error = r["errors"]![0]!;
        Assert.StartsWith("syntax error", error["extensions"]!["reason"]!.GetValue<string>());
        Assert.Equal("SYNTAX_ERROR", error["extensions"]!["code"]!.GetValue<string>());
        Assert.Equal(2, error["extensions"]!["line"]!.GetValue<int>());
        Assert.True(error["extensions"]!["column"]!.GetValue<int>() > 1);
        Assert.Contains("line 2", error["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_unsupported_feature_is_a_graphql_error_with_its_position()
    {
        var r = await Post(CypherQuery, new { book = Hound, q = "MATCH (a)-[r:MET*1..2]->(b) RETURN b", ch = 3, c = (string?)null, s = false });
        var error = r["errors"]![0]!;
        Assert.Equal("NOT_SUPPORTED", error["extensions"]!["code"]!.GetValue<string>());
        Assert.Equal(17, error["extensions"]!["column"]!.GetValue<int>());
        Assert.Equal(16, error["extensions"]!["offset"]!.GetValue<int>());
    }

    // A query nested deep enough to overflow the parser's stack would end the API for everyone: it is refused as any other Cypher
    // error, and the API goes on answering.
    [Fact]
    public async Task A_query_nested_a_thousand_deep_or_too_long_is_a_cypher_error_and_the_api_stays_up()
    {
        var client = factory.CreateClient();
        var deep = "RETURN " + new string('(', 1000) + "1" + new string(')', 1000);
        var lists = "RETURN " + new string('[', 1000) + "1" + new string(']', 1000);
        var longOne = "RETURN '" + new string('x', 50_000) + "'";

        foreach (var (query, message) in new[] { (deep, "nested too deeply"), (lists, "nested too deeply"), (longOne, "too long") })
        {
            var r = await Post(client, CypherQuery, new { book = Hound, q = query, ch = 3, c = (string?)null, s = false });
            var error = r["errors"]![0]!;
            Assert.Equal("QUERY_ERROR", error["extensions"]!["code"]!.GetValue<string>());
            Assert.Contains(message, error["message"]!.GetValue<string>());
        }

        var health = await client.GetAsync("/health", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        var after = await Post(client, CypherQuery, new { book = Hound, q = "RETURN 1 AS one", ch = 3, c = (string?)null, s = false });
        Assert.Single(after["data"]!["cypher"]!["rows"]!.AsArray());
    }

    [Fact]
    public async Task An_unknown_book_or_chapter_is_an_error()
    {
        var book = await Post("{ chapters(bookId: \"no-such-book\") { number } }");
        Assert.Equal("UNKNOWN_BOOK", book["errors"]![0]!["extensions"]!["code"]!.GetValue<string>());
        var cypher = await Post(CypherQuery, new { book = "no-such-book", q = "MATCH (n) RETURN n", ch = 1, c = (string?)null, s = false });
        Assert.Equal("UNKNOWN_BOOK", cypher["errors"]![0]!["extensions"]!["code"]!.GetValue<string>());
        var chapter = await Post(CypherQuery, new { book = Hound, q = "MATCH (n) RETURN n", ch = 99, c = (string?)null, s = false });
        Assert.Equal("UNKNOWN_CHAPTER", chapter["errors"]![0]!["extensions"]!["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task Rows_beyond_max_rows_are_cut_and_flagged()
    {
        using var small = factory.WithWebHostBuilder(b => b.UseSetting("Query:MaxRows", "2"));
        var r = await Post(small.CreateClient(), CypherQuery, new { book = Hound, q = "MATCH (n) RETURN id(n) AS id", ch = 3, c = (string?)null, s = false });
        Assert.Equal(2, r["data"]!["cypher"]!["rows"]!.AsArray().Count);
        Assert.True(r["data"]!["cypher"]!["truncated"]!.GetValue<bool>());
    }

    [Fact]
    public async Task A_query_past_the_timeout_is_stopped_with_an_error()
    {
        using var instant = factory.WithWebHostBuilder(b => b.UseSetting("Query:TimeoutSeconds", "0"));
        var r = await Post(instant.CreateClient(), CypherQuery, new { book = Hound, q = "MATCH (a), (b) RETURN count(*)", ch = 3, c = (string?)null, s = false });
        Assert.Equal("TIMEOUT", r["errors"]![0]!["extensions"]!["code"]!.GetValue<string>());
    }
}
