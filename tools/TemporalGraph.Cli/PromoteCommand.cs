using System.ComponentModel;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Spectre.Console;
using Spectre.Console.Cli;
using TemporalGraph.Cli.Runs;
using TemporalGraph.Cli.Telemetry;
using TemporalGraph.Data;
using TemporalGraph.Query;

namespace TemporalGraph.Cli;

/// <summary>
/// <c>index promote --run &lt;runId&gt; --book &lt;id&gt;</c>: copies a run's graph (runs/&lt;runId&gt;/book.sqlite) into the committed
/// book database data/books/&lt;id&gt;.sqlite, which the API embeds, and writes data/books/provenance/&lt;id&gt;.json.
/// </summary>
public sealed class PromoteCommand : Command<PromoteCommand.Settings>
{
    public sealed class Settings : GlobalSettings
    {
        [CommandOption("--run <RUN>")] public string Run { get; init; } = "";
        [CommandOption("--book <ID>")][Description("The book id; must match the run's book. Default: the run's book.")] public string? Book { get; init; }
    }

    public override int Execute(CommandContext context, Settings given, CancellationToken ct)
    {
        var root = RepoPaths.FindRoot();
        RunFolder run;
        try { run = RunFolder.Open(RepoPaths.Runs(root), given.Run); }
        catch (DirectoryNotFoundException ex) { return IndexingShared.Fail(ex.Message); }
        // `index promote --run <id>` is enough: the book is the one the run recorded.
        if ((string.IsNullOrWhiteSpace(given.Book) ? Books.BookShelf.RunBook(run.Path) : given.Book) is not { Length: > 0 } book)
            return IndexingShared.Fail($"{run.RunId} does not record its book; give --book <id>.");
        var settings = new { Book = book, given.Run };
        var target = Path.Combine(RepoPaths.Books(root), settings.Book + ".sqlite");
        var text = Path.Combine(RepoPaths.Books(root), "source", settings.Book + ".txt");
        var provenance = Path.Combine(RepoPaths.Books(root), "provenance", settings.Book + ".json");
        if (!File.Exists(text)) return IndexingShared.Fail($"No committed source text for '{settings.Book}': {Path.GetRelativePath(root, text)}");

        using var span = IndexingTelemetry.Source.StartActivity($"index promote {settings.Book}");
        span?.SetTag(IndexingTelemetry.RunId, run.RunId);
        span?.SetTag(IndexingTelemetry.BookId, settings.Book);
        // Promoting the run that is already promoted changes nothing, and says so.
        if (Promotion.AlreadyPromoted(run, target, provenance))
        {
            CliContext.Result = new { runId = run.RunId, book = settings.Book, promoted = true, unchanged = true };
            AnsiConsole.MarkupLine($"[bold]{run.RunId}[/] is already promoted to {Markup.Escape(Path.GetRelativePath(root, target))}: unchanged.");
            return 0;
        }
        Promotion.Outcome outcome;
        try { outcome = Promotion.Promote(run, settings.Book, target, Promotion.Sha256(text), provenance, Path.Combine(RepoPaths.Books(root), BookManifest.FileName), PromoteRule.Score(root, run, settings.Book)); }
        catch (InvalidOperationException ex) { return IndexingShared.Fail("Not promoted: " + ex.Message); }

        var table = new Table().AddColumns("Table", "Rows");
        foreach (var (k, v) in outcome.Rows) table.AddRow(k, v.ToString());
        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"Promoted [bold]{run.RunId}[/] → {Markup.Escape(Path.GetRelativePath(root, target))} ({new FileInfo(target).Length:N0} bytes); provenance: {Markup.Escape(Path.GetRelativePath(root, provenance))}");
        return 0;
    }
}

/// <summary>The promotion itself (also used by the tests).</summary>
public static class Promotion
{
    /// <summary>The graph tables, in copy order (parents before children). chapters and positions are never touched.</summary>
    public static readonly IReadOnlyList<string> GraphTables = ["nodes", "node_labels", "edges", "event_order", "characters", "locations", "timeline"];

    public sealed record Outcome(IReadOnlyDictionary<string, long> Rows, JsonObject Provenance);

    public static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>
    /// True when the provenance file names this run and every graph table of <paramref name="targetDb"/> holds exactly the
    /// rows of the run's book.sqlite: promoting it again would change nothing.
    /// </summary>
    public static bool AlreadyPromoted(RunFolder run, string targetDb, string provenancePath)
    {
        var source = run.File(RunFolder.BookSqlite);
        if (!File.Exists(source) || !File.Exists(targetDb) || !File.Exists(provenancePath)) return false;
        try
        {
            if (JsonNode.Parse(File.ReadAllText(provenancePath))?["runId"]?.GetValue<string>() != run.RunId) return false;
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = targetDb, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            db.Open();
            Exec(db, "ATTACH DATABASE $src AS src", ("$src", Path.GetFullPath(source)));
            foreach (var t in GraphTables)
            {
                if (!Columns(db, "main", t).SequenceEqual(Columns(db, "src", t))) return false;
                var differ = Scalar(db, $"SELECT (SELECT COUNT(*) FROM (SELECT * FROM main.{t} EXCEPT SELECT * FROM src.{t})) + (SELECT COUNT(*) FROM (SELECT * FROM src.{t} EXCEPT SELECT * FROM main.{t}))");
                if (differ != 0) return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is SqliteException or System.Text.Json.JsonException or InvalidOperationException or IOException) { return false; }
    }

    /// <summary>
    /// Refuses (InvalidOperationException) unless the run's run.json book is <paramref name="bookId"/>, its text hash is
    /// <paramref name="committedTextSha256"/>, it has book.sqlite, and both files have the same chapters and positions.
    /// Then, in one transaction, deletes every row of the graph tables in <paramref name="targetDb"/> and copies them
    /// from the run's book.sqlite with their ids; then VACUUM. Writes the provenance file: run id, time, git commit, chapters,
    /// skills, counts, and the LLM profile, type and model the run recorded in <c>settings.llm</c>. Never the endpoint.
    /// </summary>
    /// <param name="manifestPath">books.json: with a full-schema graph, the book's entry gets the anchor (<c>anchorEdtf</c>, <c>anchorNote</c>). Null: not written.</param>
    /// <param name="scores">The run's scores against the book's gold (<see cref="PromoteRule.Score"/>), recorded as <c>scores</c>; null: none.</param>
    public static Outcome Promote(RunFolder run, string bookId, string targetDb, string committedTextSha256, string provenancePath, string? manifestPath = null, GoldScores? scores = null)
    {
        if (!File.Exists(run.File(RunFolder.RunJson))) throw new InvalidOperationException($"{run.RunId} has no run.json.");
        var runJson = JsonNode.Parse(File.ReadAllText(run.File(RunFolder.RunJson)))!.AsObject();
        var runBook = runJson["book"]?.GetValue<string>();
        if (runBook != bookId) throw new InvalidOperationException($"run {run.RunId} is of book '{runBook}', not '{bookId}'.");
        var runHash = runJson["textSha256"]?.GetValue<string>();
        if (!string.Equals(runHash, committedTextSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"the run's text hash ({runHash ?? "none"}) differs from the committed text's ({committedTextSha256}).");
        var source = run.File(RunFolder.BookSqlite);
        if (!File.Exists(source)) throw new InvalidOperationException($"{run.RunId} has no book.sqlite; run index build-db first.");
        if (!File.Exists(targetDb)) throw new InvalidOperationException($"the committed book database is missing: {targetDb}");

        // A graph of the full schema (it has a timeline) is leak-tested in both modes before it replaces
        // the committed one; a failing test refuses the promotion. The committed file is brought up to the schema first.
        IReadOnlyList<LeakCheck>? leaks = null;
        if (BookDatabase.HasCurrentSchema(source))
        {
            var graph = BookGraph.Load(source);
            if (graph.Timeline.Count > 0)
            {
                leaks = LeakTests.Run(graph);
                if (leaks.FirstOrDefault(l => !l.Passed) is { } failed)
                    throw new InvalidOperationException($"leak test '{failed.Name}' failed ({failed.Failures.Count}): {failed.Failures[0]}");
            }
            // The layout columns too, so the positions are copied with the rows.
            if (!BookDatabase.HasCurrentSchema(targetDb) || (BookDatabase.HasLayoutColumns(source) && !BookDatabase.HasLayoutColumns(targetDb))) BookDatabase.UpgradeInPlace(targetDb);
            if (!BookDatabase.HasLayoutColumns(source) && BookDatabase.HasLayoutColumns(targetDb)) BookDatabase.UpgradeInPlace(source);
        }
        else if (BookDatabase.HasCurrentSchema(targetDb))
            throw new InvalidOperationException("the run's book.sqlite was built before the full schema (no clocks on nodes, no timeline); rebuild it with index build-db --force.");

        var connection = new SqliteConnectionStringBuilder { DataSource = targetDb, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString();
        var rows = new Dictionary<string, long>();
        using (var db = new SqliteConnection(connection))
        {
            db.Open();
            Exec(db, "ATTACH DATABASE $src AS src", ("$src", Path.GetFullPath(source))); // only read from
            // The text split must be the same. chapters.narrator is derived from the titles and is copied below, not compared.
            foreach (var (t, columns) in new[] { ("chapters", "number, title, char_start, char_end"), ("positions", "*") })
            {
                var differ = Scalar(db, $"SELECT (SELECT COUNT(*) FROM (SELECT {columns} FROM main.{t} EXCEPT SELECT {columns} FROM src.{t})) + (SELECT COUNT(*) FROM (SELECT {columns} FROM src.{t} EXCEPT SELECT {columns} FROM main.{t}))");
                if (differ != 0) throw new InvalidOperationException($"'{t}' differs between the run's book.sqlite and the committed file ({differ} rows).");
            }
            using (var tx = db.BeginTransaction())
            {
                foreach (var t in GraphTables.Reverse()) Exec(db, $"DELETE FROM main.{t}", tx: tx);
                foreach (var t in GraphTables)
                {
                    var columns = Columns(db, "main", t);
                    if (!columns.SequenceEqual(Columns(db, "src", t))) throw new InvalidOperationException($"'{t}' has different columns in the run's book.sqlite.");
                    var list = string.Join(", ", columns.Select(c => $"\"{c}\""));
                    Exec(db, $"INSERT INTO main.{t} ({list}) SELECT {list} FROM src.{t}", tx: tx);
                }
                if (Columns(db, "main", "chapters").Contains("narrator") && Columns(db, "src", "chapters").Contains("narrator"))
                    Exec(db, "UPDATE main.chapters SET narrator = (SELECT s.narrator FROM src.chapters s WHERE s.number = main.chapters.number)", tx: tx);
                tx.Commit();
            }
            Exec(db, "DETACH DATABASE src");
            Exec(db, "VACUUM");
            foreach (var t in GraphTables.Concat(["chapters", "positions"])) rows[t] = Scalar(db, $"SELECT COUNT(*) FROM {t}");

            var labels = Pairs(db, "SELECT label, COUNT(*) FROM node_labels GROUP BY label ORDER BY label");
            // The anchor (the one assumed date) from the timeline, for the provenance file and the manifest.
            var anchor = Columns(db, "main", "timeline").Count == 0 ? null : Row(db, "SELECT story_from_edtf, note FROM timeline WHERE kind = 'anchor' LIMIT 1");
            var edgeTypes = Pairs(db, "SELECT type, COUNT(*) FROM edges GROUP BY type ORDER BY type");
            var provenance = new JsonObject
            {
                ["book"] = bookId,
                ["runId"] = run.RunId,
                ["promotedAt"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
                ["git"] = runJson["git"]?.DeepClone(),
                ["chapters"] = runJson["chapters"]?.DeepClone(),
                ["textSha256"] = runHash,
                ["llm"] = runJson["settings"]?["llm"] is JsonObject llm
                    ? new JsonObject { ["profile"] = llm["profile"]?.DeepClone(), ["type"] = llm["type"]?.DeepClone(), ["model"] = llm["model"]?.DeepClone() }
                    : null,
                ["anchor"] = anchor is null ? null : new JsonObject { ["edtf"] = anchor.Value.A, ["note"] = anchor.Value.B },
                ["scores"] = scores?.ToJson(),
                // The layout build-db computed for this graph (algorithm, seed, iterations, computedAt); null when it had none.
                ["layout"] = File.Exists(run.File(GraphLayoutStep.FileName)) ? JsonNode.Parse(File.ReadAllText(run.File(GraphLayoutStep.FileName))) : null,
                ["leakTests"] = leaks is null ? null : new JsonObject(leaks.Select(l => KeyValuePair.Create(l.Name, (JsonNode?)new JsonObject { ["checked"] = l.Checked, ["passed"] = l.Passed }))),
                ["skills"] = new JsonObject((runJson["skills"] as JsonObject ?? []).Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)(kv.Value?["version"]?.GetValue<int>() is { } v ? JsonValue.Create(v) : null)))),
                ["counts"] = new JsonObject
                {
                    ["rows"] = new JsonObject(rows.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))),
                    ["labels"] = new JsonObject(labels.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))),
                    ["edgeTypes"] = new JsonObject(edgeTypes.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))),
                },
            };
            StageLogs.Line(run, $"promoted to {Path.GetFileName(targetDb)}");   // Run.log
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(provenancePath))!);
            File.WriteAllText(provenancePath, provenance.ToJsonString(new JsonSerializerOptions(RunFolder.Json) { WriteIndented = true }) + "\n");
            // The anchor also goes on the book's manifest entry (anchorEdtf, anchorNote).
            if (anchor is not null && manifestPath is not null && File.Exists(manifestPath))
                BookManifest.Write(manifestPath, BookManifest.Read(manifestPath).Select(b => b.Id == bookId ? b with { AnchorEdtf = anchor.Value.A, AnchorNote = anchor.Value.B } : b).ToList());
            return new Outcome(rows, provenance);
        }
    }

    private static void Exec(SqliteConnection db, string sql, (string, object)? param = null, SqliteTransaction? tx = null)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        if (param is { } p) cmd.Parameters.AddWithValue(p.Item1, p.Item2);
        cmd.ExecuteNonQuery();
    }

    private static long Scalar(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        return (long)cmd.ExecuteScalar()!;
    }

    private static (string? A, string? B)? Row(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        using var r = cmd.ExecuteReader();
        return r.Read() ? (r.IsDBNull(0) ? null : r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1)) : null;
    }

    private static List<string> Columns(SqliteConnection db, string schema, string table)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = $"PRAGMA {schema}.table_info({table})";
        using var r = cmd.ExecuteReader();
        var list = new List<string>();
        while (r.Read()) list.Add(r.GetString(1));
        return list;
    }

    private static List<KeyValuePair<string, long>> Pairs(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        using var r = cmd.ExecuteReader();
        var list = new List<KeyValuePair<string, long>>();
        while (r.Read()) list.Add(KeyValuePair.Create(r.GetString(0), r.GetInt64(1)));
        return list;
    }
}
