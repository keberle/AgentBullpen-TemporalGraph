using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using Spectre.Console.Cli;
using TemporalGraph.Cli.Indexing;
using TemporalGraph.Llm;
using TemporalGraph.Cli.Runs;
using TemporalGraph.Cli.Telemetry;
using TemporalGraph.Data;

namespace TemporalGraph.Cli;

/// <summary>
/// <c>index prepare --book &lt;id&gt; | --all</c>: Stage 0. Splits each book's source text (data/books/source) into chapters,
/// paragraphs and a chunk plan, runs the Layer 1 checks, writes the run folder and — when every check
/// passes — rebuilds the book database with its chapters and positions (graph tables stay empty).
/// A book with a promoted graph (data/books/provenance/&lt;id&gt;.json, from <c>index promote</c>) is not rebuilt unless
/// <c>--force</c> is given; with <c>--force</c> the graph is dropped and so is its provenance file.
/// </summary>
public sealed class IndexPrepareCommand : Command<IndexPrepareCommand.Settings>
{
    public sealed class Settings : GlobalSettings
    {
        [CommandOption("--book <ID>")]
        [Description("Book id from data/books/books.json.")]
        public string? Book { get; init; }

        [CommandOption("--all")]
        [Description("Every book in data/books/books.json.")]
        public bool All { get; init; }

        [CommandOption("--force")]
        [Description("Also rebuild books with a promoted graph (data/books/provenance/<id>.json): drops the graph and the provenance file.")]
        public bool Force { get; init; }

        public override ValidationResult Validate() =>
            (Book is null) == All ? ValidationResult.Success() : ValidationResult.Error("Give exactly one of --book <id> or --all.");
    }

    public override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var root = RepoPaths.FindRoot();
        var manifest = BookManifest.Read(Path.Combine(RepoPaths.Books(root), BookManifest.FileName));
        var ids = settings.All ? manifest.Select(b => b.Id).ToList() : [settings.Book!];

        foreach (var id in ids)
        {
            if (manifest.All(b => b.Id != id)) return Fail($"Book '{id}' is not in data/books/books.json.");
            if (!BookProfiles.Has(root, id)) return Fail($"Book '{id}' has no source text (data/books/source/{id}.txt); add it with `books add`.");
        }

        var command = "index prepare " + (settings.All ? "--all" : $"--book {settings.Book}") + (settings.Force ? " --force" : "");
        var outcome = Run(root, ids, command, writeDatabase: true, force: settings.Force);

        var table = new Table().AddColumns("Book", "Chapters", "First chapter", "Last chapter", "Paragraphs", "Chunks", "Checks", "Text SHA-256");
        foreach (var r in outcome.Results)
            table.AddRow(
                Markup.Escape(r.BookId),
                r.Chapters.Count.ToString(),
                Markup.Escape(r.Chapters.FirstOrDefault()?.Title ?? "-"),
                Markup.Escape(r.Chapters.LastOrDefault()?.Title ?? "-"),
                r.ParagraphCount.ToString(),
                r.Chunks.Count.ToString(),
                r.Passed ? "[green]pass[/]" : "[red]FAIL[/]",
                r.TextSha256[..16] + "…");
        AnsiConsole.Write(table);
        foreach (var c in outcome.Results.SelectMany(r => r.Checks).Where(c => !c.Passed))
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(c.Book)} {Markup.Escape(c.Name)}:[/] {Markup.Escape(c.Details)}");
        foreach (var id in outcome.Protected)
            AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(id)}: database not rebuilt: it holds a promoted graph ({Markup.Escape(Path.Combine("data", "books", "provenance", id + ".json"))}). Use --force to rebuild it and drop the graph.[/]");
        foreach (var id in outcome.Dropped)
            AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(id)}: --force: rebuilt; the promoted graph and its provenance file were dropped.[/]");
        AnsiConsole.MarkupLine($"Run [bold]{outcome.Run.RunId}[/]: {Markup.Escape(outcome.Run.Path)}");
        // The work ran and a Stage 0 check is bad.
        return outcome.Passed ? Exit.Ok : CliContext.Problem(Exit.CheckFailed, string.Join("; ", outcome.Results.SelectMany(r => r.Checks).Where(c => !c.Passed).Select(c => $"{c.Book} {c.Name}: {c.Details}")), "stage0-check");
    }

    private static int Fail(string message)
    {
        return CliContext.Fail(Exit.BadInput, message);
    }

    /// <param name="Protected">Books whose database was not rebuilt because they hold a promoted graph (no --force).</param>
    /// <param name="Dropped">Books rebuilt with --force although they held a promoted graph (graph and provenance dropped).</param>
    public sealed record Outcome(RunFolder Run, IReadOnlyList<Stage0Result> Results, IReadOnlyList<string> Protected, IReadOnlyList<string> Dropped)
    {
        public bool Passed => Results.All(r => r.Passed);
    }

    /// <summary>data/books/provenance/&lt;id&gt;.json: present when a run's graph was promoted into the book database.</summary>
    public static string ProvenancePath(string root, string bookId) => Path.Combine(RepoPaths.Books(root), "provenance", bookId + ".json");

    /// <summary>
    /// Stage 0 for <paramref name="bookIds"/> into a new run folder under <paramref name="root"/>/runs. With
    /// <paramref name="writeDatabase"/>, a book with a provenance file is skipped (its promoted graph kept) unless
    /// <paramref name="force"/>; forced, the database is rebuilt and the provenance file deleted.
    /// </summary>
    public static Outcome Run(string root, IReadOnlyList<string> bookIds, string command, bool writeDatabase, bool force = false)
    {
        var protectedBooks = new List<string>();
        var dropped = new List<string>();
        var logger = CliTelemetry.Logger("TemporalGraph.Indexing");
        var config = LlmSettings.LoadConfiguration();
        var run = RunFolder.Create(RepoPaths.Runs(root));
        var startedAt = DateTimeOffset.UtcNow;
        var chunkSettings = new ChunkSettings();

        using var commandSpan = IndexingTelemetry.Source.StartActivity(command);
        commandSpan?.SetTag(IndexingTelemetry.RunId, run.RunId);
        if (bookIds.Count == 1) commandSpan?.SetTag(IndexingTelemetry.BookId, bookIds[0]);
        logger.LogInformation("Run {RunId}: {Command} for {Books}", run.RunId, command, string.Join(", ", bookIds));

        var results = new List<Stage0Result>();
        foreach (var id in bookIds)
        {
            using var stageSpan = IndexingTelemetry.Source.StartActivity($"{Stage0.StageName} {id}");
            stageSpan?.SetTag(IndexingTelemetry.RunId, run.RunId);
            stageSpan?.SetTag(IndexingTelemetry.Stage, Stage0.StageName);
            stageSpan?.SetTag(IndexingTelemetry.BookId, id);

            var bytes = SourceTexts.Read(root, id);
            var result = Stage0.Prepare(BookProfiles.For(root, id), bytes, SourceTexts.Sha256(root, id), chunkSettings, run.RunId);
            results.Add(result);

            stageSpan?.SetTag("book.chapters", result.Chapters.Count);
            stageSpan?.SetTag("book.paragraphs", result.ParagraphCount);
            stageSpan?.SetTag("book.chunks", result.Chunks.Count);
            stageSpan?.SetTag("checks.passed", result.Passed);
            foreach (var c in result.Checks.Where(c => !c.Passed))
                logger.LogError("{Book} check {Check} failed: {Details}", id, c.Name, c.Details);

            var promoted = File.Exists(ProvenancePath(root, id));
            if (result.Passed && writeDatabase && promoted && !force)
            {
                protectedBooks.Add(id);
                stageSpan?.SetTag("database.skipped", "promoted graph");
                logger.LogWarning("{Book}: database not rebuilt: it holds a promoted graph (use --force)", id);
            }
            else if (result.Passed && writeDatabase)
            {
                WriteDatabase(Path.Combine(RepoPaths.Books(root), $"{id}.sqlite"), result);
                if (promoted)
                {
                    File.Delete(ProvenancePath(root, id));
                    dropped.Add(id);
                    logger.LogWarning("{Book}: --force: promoted graph and provenance file dropped", id);
                }
                logger.LogInformation("{Book}: wrote {Chapters} chapters and {Paragraphs} positions", id, result.Chapters.Count, result.ParagraphCount);
            }
            else if (!result.Passed)
            {
                stageSpan?.SetStatus(ActivityStatusCode.Error, "Layer 1 checks failed");
                logger.LogWarning("{Book}: checks failed, database not written", id);
            }
        }

        var git = GitInfo.Current(root);
        run.Write(RunFolder.RunJson, new
        {
            runId = run.RunId,
            command,
            startedAt,
            finishedAt = DateTimeOffset.UtcNow,
            books = results.Select(r => new { id = r.BookId, textSha256 = r.TextSha256 }),
            git = new { commit = git.Sha, dirty = git.Dirty },
            settings = new
            {
                chunkTokens = chunkSettings.ChunkTokens,
                overlapTokens = chunkSettings.OverlapTokens,
                charsPerToken = chunkSettings.CharsPerToken,
                tokenEstimate = chunkSettings.TokenEstimate,
                offsets = "UTF-16 code units of the UTF-8-decoded source text (CRLF = 2); ranges [start, end)",
                databaseWritten = writeDatabase,
                databaseSkipped = protectedBooks,
                promotedGraphDropped = dropped,
            },
            model = (string?)null,
            passed = results.All(r => r.Passed),
        });
        foreach (var r in results)   // Run.log
            StageLogs.Line(run, $"stage-0 {r.BookId}: {r.Chapters.Count} chapters, {r.Chunks.Count} chunks; checks {(r.Passed ? "pass" : "FAIL: " + string.Join("; ", r.Checks.Where(c => !c.Passed).Select(c => $"{c.Name}: {c.Details}")))}");
        run.Write(RunFolder.Stage0Json, new { stage = Stage0.StageName, books = results.Select(Stage0Report) });
        run.Write(RunFolder.ChecksJson, results.SelectMany(r => r.Checks));
        run.Write(RunFolder.CostJson, Pricing.From(config).CostReport(0, 0, 0));

        commandSpan?.SetTag("checks.passed", results.All(r => r.Passed));
        if (!results.All(r => r.Passed)) commandSpan?.SetStatus(ActivityStatusCode.Error, "Layer 1 checks failed");
        return new Outcome(run, results, protectedBooks, dropped);
    }

    public static object Stage0Report(Stage0Result r) => new
    {
        id = r.BookId,
        textSha256 = r.TextSha256,
        textLength = r.TextLength,
        body = new { charStart = r.Body.CharStart, charEnd = r.Body.CharEnd },
        skipped = r.Skipped.Select(p => new { name = p.Name, charStart = p.CharStart, charEnd = p.CharEnd }),
        contents = r.Contents,
        chapterCount = r.Chapters.Count,
        paragraphCount = r.ParagraphCount,
        chunkCount = r.Chunks.Count,
        chapters = r.Chapters.Select(c => new
        {
            number = c.Number,
            heading = c.Heading,
            title = c.Title,
            charStart = c.CharStart,
            charEnd = c.CharEnd,
            narrator = c.NarratorColumn,
            paragraphs = c.Paragraphs.Count,
        }),
        chunkPlan = new
        {
            chunkTokens = r.ChunkSettings.ChunkTokens,
            overlapTokens = r.ChunkSettings.OverlapTokens,
            charsPerToken = r.ChunkSettings.CharsPerToken,
            tokenEstimate = r.ChunkSettings.TokenEstimate,
            chunks = r.Chunks,
        },
    };

    /// <summary>Rebuilds the book database (create path, EnsureCreated) and writes chapters and positions.</summary>
    private static void WriteDatabase(string path, Stage0Result result)
    {
        if (File.Exists(path)) File.Delete(path);
        BookDatabase.Create(path);
        using var db = BookDatabase.OpenReadWrite(path);
        db.Chapters.AddRange(result.Chapters.Select(c => new Chapter { Number = c.Number, Title = c.Title, CharStart = c.CharStart, CharEnd = c.CharEnd, Narrator = c.NarratorColumn }));
        db.Positions.AddRange(result.Chapters.SelectMany(c => c.Paragraphs).Select(p => new Position
        {
            Chapter = p.Chapter,
            Paragraph = p.Paragraph,
            CharStart = p.CharStart,
            CharEnd = p.CharEnd,
        }));
        db.SaveChanges();
    }
}

/// <summary>
/// The source texts: <c>data/books/source/&lt;id&gt;.txt</c> under the repo root, byte for byte as downloaded. Read from disk (a
/// book added with <c>books add</c> cannot be compiled in); before, they were embedded resources.
/// </summary>
public static class SourceTexts
{
    public static string PathOf(string root, string bookId) => Path.Combine(RepoPaths.Books(root), "source", bookId + ".txt");

    public static byte[] Read(string root, string bookId) =>
        File.Exists(PathOf(root, bookId)) ? File.ReadAllBytes(PathOf(root, bookId))
        : throw new FileNotFoundException($"Source text not found: data/books/source/{bookId}.txt (add the book with `books add`).");

    public static byte[] Read(string bookId) => Read(RepoPaths.FindRoot(), bookId);

    /// <summary>SHA-256 of the file, hashed straight from its stream (Stage 0's text-hash check compares it with the bytes it read).</summary>
    public static string Sha256(string root, string bookId)
    {
        using var stream = File.OpenRead(PathOf(root, bookId));
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream));
    }

    public static string Sha256(string bookId) => Sha256(RepoPaths.FindRoot(), bookId);
}