using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace TemporalGraph.Data;

/// <summary>Creates book databases (CLI) and opens embedded ones read-only (API).</summary>
public static class BookDatabase
{
    /// <summary>
    /// Creates a new book database at <paramref name="path"/> with the full schema.
    /// The file must not exist yet. Uses EnsureCreated, not Migrations: every file is built from scratch.
    /// </summary>
    public static void Create(string path)
    {
        if (File.Exists(path))
            throw new IOException($"Book database already exists: {path}");

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false, // release the file as soon as we are done, so it can be committed or deleted
        }.ToString();

        using var db = new BookDbContext(new DbContextOptionsBuilder<BookDbContext>().UseSqlite(connectionString).Options);
        db.Database.EnsureCreated();
        // EF Core creates SQLite files in WAL mode. A WAL file needs -wal/-shm side files even to
        // read, so switch to the rollback journal: the file is then complete on its own (embeddable).
        db.Database.ExecuteSqlRaw("PRAGMA journal_mode=DELETE;");
    }

    /// <summary>Opens an existing book database for writing (CLI indexing).</summary>
    public static BookDbContext OpenReadWrite(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Book database not found: {path}", path);

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false, // release the file when the context is disposed
        }.ToString();

        return new BookDbContext(new DbContextOptionsBuilder<BookDbContext>().UseSqlite(connectionString).Options);
    }

    /// <summary>
    /// Opens a book database file read-only (the SQLite connection itself is read-only, so any write fails), with
    /// no-tracking queries. Used by the query engine (TemporalGraph.Query) for <c>data/books/*.sqlite</c> or a run's book.sqlite.
    /// </summary>
    public static BookDbContext OpenReadOnlyFile(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Book database not found: {path}", path);

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Readable(path),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false, // release the file when the context is disposed
        }.ToString();

        return new BookDbContext(new DbContextOptionsBuilder<BookDbContext>()
            .UseSqlite(connectionString)
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
            .Options);
    }

    // The columns and the table a book file built before the full schema lacks. All nullable, so such a file reads as before:
    // its nodes have no clocks, its chapters no narrator, and its timeline is empty.
    private static readonly string[] NodeClockColumns =
    [
        "revealed_pos INTEGER", "retracted_pos INTEGER", "story_from_edtf TEXT", "story_until_edtf TEXT", "story_from_lo TEXT",
        "story_from_hi TEXT", "story_until_lo TEXT", "story_until_hi TEXT", "fabula_seq REAL",
    ];

    // The layout columns (`index layout`). A file without them reads as one with no layout computed.
    private static readonly string[] NodeLayoutColumns = ["layout_x REAL", "layout_y REAL", "layout_z REAL", "layout2_x REAL", "layout2_y REAL"];

    private const string TimelineDdl =
        "CREATE TABLE timeline (id INTEGER NOT NULL CONSTRAINT PK_timeline PRIMARY KEY AUTOINCREMENT, kind TEXT NOT NULL, ref_id INTEGER NULL, " +
        "label TEXT NULL, revealed_pos INTEGER NULL, revealed_pos_end INTEGER NULL, story_from_edtf TEXT NULL, story_until_edtf TEXT NULL, " +
        "story_from_lo TEXT NULL, story_from_hi TEXT NULL, story_until_lo TEXT NULL, story_until_hi TEXT NULL, fabula_seq REAL NULL, note TEXT NULL)";

    // Book file (path, size, last write) → the upgraded temp copy, once per process.
    private static readonly ConcurrentDictionary<string, Lazy<string>> UpgradedCopies = new();

    /// <summary>True when the file has the full schema (clocks on nodes, <c>chapters.narrator</c>, the <c>timeline</c> table).</summary>
    public static bool HasCurrentSchema(string path)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        db.Open();
        return MissingSchema(db, layout: false).Count == 0;
    }

    /// <summary>True when the file's <c>nodes</c> table has the layout columns (filled or not).</summary>
    public static bool HasLayoutColumns(string path)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        db.Open();
        return MissingSchema(db, layout: true).Count == MissingSchema(db, layout: false).Count;
    }

    /// <summary>
    /// The file to read <paramref name="path"/> through: the file itself when it has the full schema; otherwise (a book indexed
    /// before the full schema) a temp copy with the missing columns and table added, empty. The committed file is never changed.
    /// </summary>
    public static string Readable(string path)
    {
        if (HasCurrentSchema(path) && HasLayoutColumns(path)) return path;
        var info = new FileInfo(path);
        return UpgradedCopies.GetOrAdd($"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}", _ => new Lazy<string>(() =>
        {
            Directory.CreateDirectory(TempDir);
            var copy = Path.Combine(TempDir, $"{Guid.NewGuid():N}-{info.Name}");
            File.Copy(info.FullName, copy);
            Upgrade(copy);
            return copy;
        })).Value;
    }

    private static List<string> MissingSchema(SqliteConnection db, bool layout = true)
    {
        HashSet<string> Columns(string table)
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = $"PRAGMA table_info({table})";
            using var r = cmd.ExecuteReader();
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (r.Read()) set.Add(r.GetString(1));
            return set;
        }
        var statements = new List<string>();
        var nodes = Columns("nodes");
        statements.AddRange(NodeClockColumns.Concat(layout ? NodeLayoutColumns : []).Where(c => !nodes.Contains(c.Split(' ')[0])).Select(c => $"ALTER TABLE nodes ADD COLUMN {c} NULL"));
        if (!Columns("chapters").Contains("narrator")) statements.Add("ALTER TABLE chapters ADD COLUMN narrator TEXT NULL");
        if (Columns("timeline").Count == 0) statements.Add(TimelineDdl);
        return statements;
    }

    /// <summary>Brings a book file up to the full schema in place (nullable columns and an empty <c>timeline</c> added; no row changes).
    /// <c>index promote</c> does this to the book it is about to replace the graph of; a book that is only read is never touched.</summary>
    public static void UpgradeInPlace(string path) => Upgrade(path);

    /// <summary>Adds what <paramref name="path"/> (a temp copy) lacks of the full schema.</summary>
    private static void Upgrade(string path)
    {
        File.SetAttributes(path, FileAttributes.Normal);
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        db.Open();
        foreach (var sql in MissingSchema(db))
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
    }

    // Embedded resource -> temp file path, copied once per process.
    private static readonly ConcurrentDictionary<string, Lazy<string>> TempCopies = new();
    private static readonly string TempDir = Path.Combine(Path.GetTempPath(), "TemporalGraph", Guid.NewGuid().ToString("N"));

    static BookDatabase()
    {
        // Best effort: drop this process's temp copies on exit.
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                SqliteConnection.ClearAllPools();
                if (Directory.Exists(TempDir)) Directory.Delete(TempDir, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        };
    }

    /// <summary>
    /// Opens a book database embedded in <paramref name="assembly"/> as <paramref name="resourceName"/>, read-only.
    /// SQLite can't open a .NET stream, so the resource is copied to a temp file once per process.
    /// </summary>
    public static BookDbContext OpenReadOnly(Assembly assembly, string resourceName)
    {
        var path = EmbeddedCopy(assembly, resourceName);

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString();

        return new BookDbContext(new DbContextOptionsBuilder<BookDbContext>()
            .UseSqlite(connectionString)
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
            .Options);
    }

    /// <summary>
    /// The temp file holding the embedded resource <paramref name="resourceName"/> of <paramref name="assembly"/>, copied
    /// once per process (SQLite can't open a .NET stream). Open it read-only, e.g. with <see cref="OpenReadOnlyFile"/>.
    /// </summary>
    public static string EmbeddedCopy(Assembly assembly, string resourceName) =>
        TempCopies.GetOrAdd($"{assembly.FullName}|{resourceName}", _ => new Lazy<string>(() => CopyToTemp(assembly, resourceName))).Value;

    private static string CopyToTemp(Assembly assembly, string resourceName)
    {
        using var resource = assembly.GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException($"Embedded resource not found: {resourceName} in {assembly.GetName().Name}");

        Directory.CreateDirectory(TempDir);
        // A fresh name per copy: two assemblies may embed the same resource name.
        var path = Path.Combine(TempDir, $"{Guid.NewGuid():N}-{Path.GetFileName(resourceName)}");
        using (var file = File.Create(path))
            resource.CopyTo(file);
        // A book embedded with the earlier schema is brought up to the current one here (its own temp copy).
        if (!HasCurrentSchema(path) || !HasLayoutColumns(path)) Upgrade(path);
        return path;
    }
}
