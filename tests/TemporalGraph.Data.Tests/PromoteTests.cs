using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using TemporalGraph.Cli;
using TemporalGraph.Cli.Runs;

namespace TemporalGraph.Data.Tests;

// Index promote copies a run's graph into a committed book database.
public sealed class PromoteTests : IDisposable
{
    private const string Book = "hound-of-the-baskervilles";
    private const string Hash = "abc123";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TemporalGraph.Data.Tests", Guid.NewGuid().ToString("N"));

    public PromoteTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static void Stage0Rows(BookDbContext db, int lastChapterEnd = 200)
    {
        db.Chapters.AddRange(new Chapter { Number = 1, Title = "One", CharStart = 0, CharEnd = 100 }, new Chapter { Number = 2, Title = "Two", CharStart = 100, CharEnd = lastChapterEnd });
        db.Positions.AddRange(new Position { Chapter = 1, Paragraph = 1, CharStart = 0, CharEnd = 100 }, new Position { Chapter = 2, Paragraph = 1, CharStart = 100, CharEnd = 200 });
    }

    private string Target(bool withOldGraph = true)
    {
        var path = Path.Combine(_dir, $"target-{Guid.NewGuid():N}.sqlite");
        BookDatabase.Create(path);
        using var db = BookDatabase.OpenReadWrite(path);
        Stage0Rows(db);
        if (withOldGraph) db.Nodes.Add(new Node { Id = 77, Properties = "{\"name\":\"old\"}", Labels = [new NodeLabel { Label = "Character" }] });
        db.SaveChanges();
        return path;
    }

    private RunFolder Run(string book = Book, string hash = Hash, bool withDb = true, int lastChapterEnd = 200)
    {
        var run = RunFolder.Create(Path.Combine(_dir, "runs"));
        run.RecordCommand(new { command = "test" }, new Dictionary<string, object?>
        {
            ["book"] = book, ["textSha256"] = hash, ["chapters"] = new { first = 1, last = 2 },
            ["git"] = new { commit = "0123abc", dirty = false },
            ["skills"] = new Dictionary<string, object> { ["extract-mentions"] = new { name = "extract-mentions", version = 1, sha256 = "x" }, ["extract-facts"] = new { name = "extract-facts", version = 2, sha256 = "y" } },
            ["model"] = new { type = "OpenAI", model = "secret-deployment-name" },
            ["settings"] = new { llm = new { profile = "haiku55", type = "Anthropic", model = "claude-haiku-5-5", endpoint = "https://secret-host.example/", policyId = (string?)null } },
        });
        if (!withDb) return run;
        BookDatabase.Create(run.File(RunFolder.BookSqlite));
        using var db = BookDatabase.OpenReadWrite(run.File(RunFolder.BookSqlite));
        Stage0Rows(db, lastChapterEnd);
        var holmes = new Node { Id = 5, Properties = "{\"name\":\"Holmes\"}", EvidenceStart = 3, Labels = [new NodeLabel { Label = "Character" }] };
        var street = new Node { Id = 9, Properties = "{\"name\":\"Baker Street\"}", Labels = [new NodeLabel { Label = "Location" }] };
        var fact = new Node { Id = 12, Properties = "{\"text\":\"x\"}", Labels = [new NodeLabel { Label = "Fact" }] };
        db.Nodes.AddRange(holmes, street, fact);
        var e1 = new Edge { Id = 40, Source = holmes, Target = street, Type = "LOCATED_AT", RevealedPos = 3 };
        var e2 = new Edge { Id = 41, Source = fact, Target = holmes, Type = "ABOUT", RevealedPos = 4, StoryFromEdtf = "1884" };
        db.Edges.AddRange(e1, e2);
        db.EventOrder.Add(new EventOrder { AEdge = e1, BEdge = e2, Relation = "BEFORE" });
        db.Characters.Add(new Character { Name = "Holmes", Node = holmes });
        db.Locations.Add(new Location { Name = "Baker Street", Node = street });
        db.SaveChanges();
        return run;
    }

    [Fact]
    public void Copies_the_graph_with_its_ids_replaces_the_old_one_and_running_twice_gives_the_same()
    {
        var target = Target();
        var run = Run();
        var provenance = Path.Combine(_dir, "provenance", Book + ".json");

        var first = Promotion.Promote(run, Book, target, Hash, provenance);
        var second = Promotion.Promote(run, Book, target, Hash, provenance);

        var expected = new Dictionary<string, long> { ["nodes"] = 3, ["node_labels"] = 3, ["edges"] = 2, ["event_order"] = 1, ["characters"] = 1, ["locations"] = 1, ["timeline"] = 0, ["chapters"] = 2, ["positions"] = 2 };
        Assert.Equal(expected, first.Rows);
        Assert.Equal(expected, second.Rows);
        using (var db = BookDatabase.OpenReadOnlyFile(target))
        {
            Assert.Equal([5L, 9L, 12L], db.Nodes.OrderBy(n => n.Id).Select(n => n.Id).ToList());   // ids kept, old node 77 gone
            Assert.Equal([40L, 41L], db.Edges.OrderBy(e => e.Id).Select(e => e.Id).ToList());
            Assert.Equal("1884", db.Edges.Single(e => e.Id == 41).StoryFromEdtf);
            Assert.Equal("One", db.Chapters.Single(c => c.Number == 1).Title);
        }
        var p = JsonNode.Parse(File.ReadAllText(provenance))!;
        Assert.Equal(run.RunId, p["runId"]!.GetValue<string>());
        Assert.Equal("0123abc", p["git"]!["commit"]!.GetValue<string>());
        Assert.Equal(2, p["skills"]!["extract-facts"]!.GetValue<int>());
        Assert.Equal(1, p["counts"]!["labels"]!["Fact"]!.GetValue<long>());
        Assert.Equal(1, p["counts"]!["edgeTypes"]!["ABOUT"]!.GetValue<long>());
        Assert.EndsWith("Z", p["promotedAt"]!.GetValue<string>());
        // The run's LLM profile, type and model id (from settings.llm); never the endpoint, nor run.json's other model fields.
        Assert.Equal(("haiku55", "Anthropic", "claude-haiku-5-5"), (p["llm"]!["profile"]!.GetValue<string>(), p["llm"]!["type"]!.GetValue<string>(), p["llm"]!["model"]!.GetValue<string>()));
        var text = File.ReadAllText(provenance);
        Assert.DoesNotContain("secret-host", text);
        Assert.DoesNotContain("endpoint", text);
        Assert.DoesNotContain("secret-deployment-name", text);
    }

    [Fact]
    public void Refuses_a_text_hash_mismatch() =>
        Assert.Contains("text hash", Assert.Throws<InvalidOperationException>(() => Promotion.Promote(Run(hash: "other"), Book, Target(), Hash, Path.Combine(_dir, "p.json"))).Message);

    [Fact]
    public void Refuses_a_wrong_book() =>
        Assert.Contains("is of book 'three-musketeers'", Assert.Throws<InvalidOperationException>(() => Promotion.Promote(Run(book: "three-musketeers"), Book, Target(), Hash, Path.Combine(_dir, "p.json"))).Message);

    [Fact]
    public void Refuses_a_run_without_book_sqlite() =>
        Assert.Contains("no book.sqlite", Assert.Throws<InvalidOperationException>(() => Promotion.Promote(Run(withDb: false), Book, Target(), Hash, Path.Combine(_dir, "p.json"))).Message);

    [Fact]
    public void Refuses_different_chapters_and_leaves_the_target_alone()
    {
        var target = Target();
        var ex = Assert.Throws<InvalidOperationException>(() => Promotion.Promote(Run(lastChapterEnd: 250), Book, target, Hash, Path.Combine(_dir, "p.json")));
        Assert.Contains("'chapters' differs", ex.Message);
        using var db = BookDatabase.OpenReadOnlyFile(target);
        Assert.Equal(77L, db.Nodes.Single().Id);
        Assert.False(File.Exists(Path.Combine(_dir, "p.json")));
    }
}
