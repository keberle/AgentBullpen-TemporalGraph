using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TemporalGraph.Api;
using TemporalGraph.Cli;
using TemporalGraph.Cli.Indexing;
using TemporalGraph.Data;

namespace TemporalGraph.Data.Tests;

public class BookDatabaseTests
{
    private static readonly string[] Tables = ["nodes", "node_labels", "edges", "event_order", "chapters", "positions", "characters", "locations", "timeline"];

    // The graph and metadata tables Stage 0 leaves empty.
    private static readonly string[] EmptyAfterStage0 = ["nodes", "node_labels", "edges", "event_order", "characters", "locations"];

    // Table -> every column.
    private static readonly Dictionary<string, string[]> Columns = new()
    {
        // Both clocks on every node, the same columns as on an edge.
        ["nodes"] =
        [
            "id", "properties", "evidence_start", "evidence_end", "revealed_pos", "retracted_pos",
            "story_from_edtf", "story_until_edtf", "story_from_lo", "story_from_hi", "story_until_lo", "story_until_hi", "fabula_seq",
            "layout_x", "layout_y", "layout_z", "layout2_x", "layout2_y",   // The stored layout
        ],
        ["node_labels"] = ["node_id", "label"],
        ["edges"] =
        [
            "id", "source_id", "target_id", "type", "properties",
            "revealed_pos", "retracted_pos", "evidence_start", "evidence_end",
            "story_from_edtf", "story_until_edtf", "story_from_lo", "story_from_hi", "story_until_lo", "story_until_hi",
            "fabula_seq",
        ],
        ["event_order"] = ["id", "a_edge_id", "b_edge_id", "relation"],
        ["chapters"] = ["number", "title", "char_start", "char_end", "narrator"],
        ["timeline"] =
        [
            "id", "kind", "ref_id", "label", "revealed_pos", "revealed_pos_end",
            "story_from_edtf", "story_until_edtf", "story_from_lo", "story_from_hi", "story_until_lo", "story_until_hi", "fabula_seq", "note",
        ],
        ["positions"] = ["id", "chapter", "paragraph", "char_start", "char_end", "scene_id"],
        ["characters"] = ["id", "node_id", "name", "short_name", "initials"],
        ["locations"] = ["id", "node_id", "name"],
    };

    // Index name -> its columns, in order.
    private static readonly Dictionary<string, string[]> Indexes = new()
    {
        ["ix_node_labels_label_node_id"] = ["label", "node_id"],
        ["ix_edges_source_id_type"] = ["source_id", "type"],
        ["ix_edges_target_id_type"] = ["target_id", "type"],
        ["ix_edges_type"] = ["type"],
        ["ix_positions_char_start"] = ["char_start"],
    };

    [Fact]
    public void Create_builds_all_tables_and_indexes()
    {
        var dir = Path.Combine(Path.GetTempPath(), "TemporalGraph.Data.Tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "book.sqlite");
        try
        {
            BookDatabase.Create(path);

            using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
            conn.Open();

            var tables = Names(conn, "table");
            foreach (var t in Tables) Assert.Contains(t, tables);

            // Every column, exactly (the chapter-integer columns on edges are gone).
            foreach (var (table, columns) in Columns)
                Assert.Equal(columns.Order(), TableColumns(conn, table).Order());

            var indexes = Names(conn, "index");
            foreach (var (name, columns) in Indexes)
            {
                Assert.Contains(name, indexes);
                Assert.Equal(columns, IndexColumns(conn, name));
            }

            // Primary key of node_labels is (node_id, label).
            Assert.Equal(["node_id", "label"], PrimaryKey(conn, "node_labels"));

            // Rollback journal, not WAL: the file must open on its own once embedded.
            using var mode = conn.CreateCommand();
            mode.CommandText = "PRAGMA journal_mode";
            Assert.Equal("delete", (string)mode.ExecuteScalar()!);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Event_order_accepts_only_TimeML_relations_and_VAGUE()
    {
        var dir = Path.Combine(Path.GetTempPath(), "TemporalGraph.Data.Tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "book.sqlite");
        try
        {
            BookDatabase.Create(path);
            using (var db = BookDatabase.OpenReadWrite(path))
            {
                var a = new Node();
                var b = new Node();
                var e1 = new Edge { Source = a, Target = b, Type = "MET" };
                var e2 = new Edge { Source = b, Target = a, Type = "KNOWS" };
                db.Edges.AddRange(e1, e2);
                foreach (var relation in TemporalRelations.All)
                    db.EventOrder.Add(new EventOrder { AEdge = e1, BEdge = e2, Relation = relation });
                db.SaveChanges();
                Assert.Equal(15, db.EventOrder.Count());
            }

            using (var db = BookDatabase.OpenReadWrite(path))
            {
                db.EventOrder.Add(new EventOrder { AEdgeId = 1, BEdgeId = 2, Relation = "SOMETIME" });
                var ex = Assert.ThrowsAny<DbUpdateException>(() => db.SaveChanges());
                Assert.Contains("CHECK constraint failed", ex.InnerException!.Message);
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Create_refuses_an_existing_file()
    {
        var path = Path.Combine(Path.GetTempPath(), $"TemporalGraph.Data.Tests-{Guid.NewGuid():N}.sqlite");
        File.WriteAllText(path, "");
        try
        {
            Assert.Throws<IOException>(() => BookDatabase.Create(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    public static TheoryData<string> CommittedBooks() => new(BookResources.ReadManifest().Select(b => b.Id));

    [Theory]
    [MemberData(nameof(CommittedBooks))]
    public void Embedded_book_opens_read_only_with_stage0_data_and_an_empty_graph(string id)
    {
        var book = BookResources.ReadManifest().Single(b => b.Id == id);
        using var db = BookDatabase.OpenReadOnly(BookResources.Assembly, BookResources.DatabaseResourceName(book));

        var conn = db.Database.GetDbConnection();
        conn.Open();
        var tables = Names((SqliteConnection)conn, "table");
        foreach (var t in Tables)
        {
            Assert.Contains(t, tables);
            Assert.Equal(Columns[t].Order(), TableColumns((SqliteConnection)conn, t).Order());
        }
        // The graph tables are empty after Stage 0, unless a run was promoted into the file (index promote):
        // then they hold exactly the counts its provenance file records.
        var provenancePath = Path.Combine(TemporalGraph.Cli.Runs.RepoPaths.Books(TemporalGraph.Cli.Runs.RepoPaths.FindRoot()), "provenance", id + ".json");
        var promoted = File.Exists(provenancePath) ? System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(provenancePath))!["counts"]!["rows"] : null;
        foreach (var t in EmptyAfterStage0)
        {
            using var count = conn.CreateCommand();
            count.CommandText = $"SELECT COUNT(*) FROM {t}";
            Assert.Equal(promoted?[t]?.GetValue<long>() ?? 0L, (long)count.ExecuteScalar()!);
        }

        // The committed database holds exactly what Stage 0 produces from the committed text.
        var stage0 = Stage0.Prepare(BookProfiles.For(id), SourceTexts.Read(id), SourceTexts.Sha256(id));
        Assert.Equal(
            stage0.Chapters.Select(c => (c.Number, c.Title, c.CharStart, c.CharEnd)),
            db.Chapters.OrderBy(c => c.Number).AsEnumerable().Select(c => (c.Number, c.Title!, c.CharStart, c.CharEnd)));
        Assert.Equal(
            stage0.Chapters.SelectMany(c => c.Paragraphs).Select(p => (p.Chapter, p.Paragraph, p.CharStart, p.CharEnd)),
            db.Positions.OrderBy(p => p.CharStart).AsEnumerable().Select(p => (p.Chapter, p.Paragraph, p.CharStart, p.CharEnd)));

        db.Nodes.Add(new Node());
        var ex = Assert.ThrowsAny<DbUpdateException>(() => db.SaveChanges());
        var sqlite = Assert.IsType<SqliteException>(ex.InnerException);
        Assert.Equal(8, sqlite.SqliteErrorCode); // SQLITE_READONLY
    }

    [Fact]
    public void Manifest_reads_back_every_book()
    {
        var books = BookResources.ReadManifest();
        // Chat is a list (compared by reference in the record), so it is checked on its own.
        Assert.Equal(
            [
                new BookEntry("hound-of-the-baskervilles", "The Hound of the Baskervilles", "Arthur Conan Doyle"),
                new BookEntry("three-musketeers", "The Three Musketeers", "Alexandre Dumas"),
                new BookEntry("treasure-island", "Treasure Island", "Robert Louis Stevenson"),
            ],
            books.Select(b => b with { Chat = null, Hero = null, Avatars = null, Voices = null, AnchorEdtf = null, AnchorNote = null, Gutenberg = null, Sha256 = null }));
        // Where each text came from (Project Gutenberg's number) and the SHA-256 of data/books/source/<id>.txt.
        Assert.Equal([2852, 1257, 120], books.Select(b => b.Gutenberg));
        Assert.All(books, b => Assert.Equal(TemporalGraph.Cli.SourceTexts.Sha256(b.Id), b.Sha256));
        // A book's anchor date (an assumption, shown as one) comes from its promoted run. The date is made up by the run, so
        // it is compared with the provenance file of that run, not written here (the three books are re-indexed).
        Assert.All(books, b =>
        {
            var provenance = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(TemporalGraph.Cli.Runs.RepoPaths.FindRoot(), "data", "books", "provenance", b.Id + ".json")))!;
            Assert.Equal(provenance["anchor"]!["edtf"]!.GetValue<string>(), b.AnchorEdtf);
            Assert.StartsWith("Assumption:", b.AnchorNote);
        });
        // One voice line per roster name (Musketeers and Treasure Island too).
        Assert.Equal(8, books[0].Voices!.Count);
        Assert.Equal(books[1].Chat!, books[1].Voices!.Keys);
        Assert.Equal(books[2].Chat!, books[2].Voices!.Keys);
        // The avatars folder (Hound only).
        Assert.Equal(["books/hound-of-the-baskervilles/characters", "books/three-musketeers/characters", "books/treasure-island/characters"], books.Select(b => b.Avatars));
        // The hero images (web/public/books/<id>.webp).
        Assert.Equal(["books/hound-of-the-baskervilles.webp", "books/three-musketeers.webp", "books/treasure-island.webp"], books.Select(b => b.Hero));
        // The chat rosters.
        Assert.Equal(["Sherlock Holmes", "Dr. Watson", "Sir Henry Baskerville", "Dr. James Mortimer", "Mr. Stapleton", "Miss Stapleton", "Barrymore", "Laura Lyons"], books[0].Chat!);
        Assert.Equal(["d'Artagnan", "Athos", "Porthos", "Aramis", "Milady", "Cardinal Richelieu", "Mme. Bonacieux", "Planchet"], books[1].Chat!);
        Assert.Equal(["Jim Hawkins", "Long John Silver", "Dr. Livesey", "Squire Trelawney", "Captain Smollett", "Ben Gunn", "Billy Bones", "Israel Hands"], books[2].Chat!);
    }

    [Fact]
    public void Manifest_write_keeps_the_chat_roster_and_add_or_update_keeps_it_when_the_update_has_none()
    {
        var path = Path.Combine(Path.GetTempPath(), $"books-{Guid.NewGuid():N}.json");
        try
        {
            var books = BookManifest.AddOrUpdate([new BookEntry("a", "A", "X", ["Holmes"], "books/a.webp", "books/a/characters")], new BookEntry("a", "A2", "X"));
            Assert.Equal("books/a/characters", books.Single().Avatars);
            Assert.Equal(["Holmes"], books.Single().Chat!);
            Assert.Equal("books/a.webp", books.Single().Hero);
            BookManifest.Write(path, [.. books, new BookEntry("b", "B", "Y")]);
            var back = BookManifest.Read(path);
            Assert.Equal(["Holmes"], back[0].Chat!);
            Assert.Equal("books/a.webp", back[0].Hero);
            Assert.Equal("books/a/characters", back[0].Avatars);
            Assert.Null(back[1].Avatars);
            Assert.Null(back[1].Chat);
            Assert.Null(back[1].Hero);
            Assert.DoesNotContain("null", File.ReadAllText(path));
        }
        finally { File.Delete(path); }
    }

    private static List<string> Names(SqliteConnection conn, string type)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = $type";
        cmd.Parameters.AddWithValue("$type", type);
        using var r = cmd.ExecuteReader();
        var names = new List<string>();
        while (r.Read()) names.Add(r.GetString(0));
        return names;
    }

    private static string[] IndexColumns(SqliteConnection conn, string index)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT name FROM pragma_index_info('{index}') ORDER BY seqno";
        using var r = cmd.ExecuteReader();
        var cols = new List<string>();
        while (r.Read()) cols.Add(r.GetString(0));
        return [.. cols];
    }

    private static string[] TableColumns(SqliteConnection conn, string table)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT name FROM pragma_table_info('{table}')";
        using var r = cmd.ExecuteReader();
        var cols = new List<string>();
        while (r.Read()) cols.Add(r.GetString(0));
        return [.. cols];
    }

    private static string[] PrimaryKey(SqliteConnection conn, string table)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT name FROM pragma_table_info('{table}') WHERE pk > 0 ORDER BY pk";
        using var r = cmd.ExecuteReader();
        var cols = new List<string>();
        while (r.Read()) cols.Add(r.GetString(0));
        return [.. cols];
    }
}
