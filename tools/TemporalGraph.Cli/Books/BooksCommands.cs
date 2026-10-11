using System.ComponentModel;
using System.Text.Json.Nodes;
using Spectre.Console;
using Spectre.Console.Cli;
using TemporalGraph.Cli.Indexing;
using TemporalGraph.Cli.Runs;
using TemporalGraph.Data;

namespace TemporalGraph.Cli.Books;

/// <summary>One book as <c>books list</c> shows it.</summary>
/// <param name="Chapters">Rows of the book database's chapters table; 0 when Stage 0 has not written it.</param>
/// <param name="IndexedRun">The run whose graph is promoted (the provenance file); null when the book is not indexed.</param>
/// <param name="LastRun">The newest run folder for the book.</param>
public sealed record BookRow(string Id, string Title, string Author, int? Gutenberg, bool SourceText, int Chapters, string? IndexedRun, string? LastRun,
    long? LastRunInputTokens, long? LastRunOutputTokens, decimal? LastRunCost);

/// <summary>What the books are and which runs they have, read from the repo's files.</summary>
public static class BookShelf
{
    public static IReadOnlyList<BookRow> List(string root)
    {
        var manifestPath = Path.Combine(RepoPaths.Books(root), BookManifest.FileName);
        var books = File.Exists(manifestPath) ? BookManifest.Read(manifestPath) : [];
        var runs = RunsByBook(root);
        return books.Select(b =>
        {
            var last = runs.GetValueOrDefault(b.Id)?.LastOrDefault();
            var cost = last is null ? null : Cost(Path.Combine(RepoPaths.Runs(root), last));
            return new BookRow(b.Id, b.Title, b.Author, b.Gutenberg, BookProfiles.Has(root, b.Id), Chapters(Path.Combine(RepoPaths.Books(root), b.DatabaseFileName)),
                IndexedRun(root, b.Id), last, cost?.In, cost?.Out, cost?.Cost);
        }).ToList();
    }

    public static string? IndexedRun(string root, string bookId)
    {
        var path = IndexPrepareCommand.ProvenancePath(root, bookId);
        try { return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))?["runId"]?.GetValue<string>() : null; }
        catch (System.Text.Json.JsonException) { return null; }
    }

    public static int Chapters(string dbPath)
    {
        if (!File.Exists(dbPath)) return 0;
        try
        {
            using var db = BookDatabase.OpenReadOnlyFile(BookDatabase.Readable(dbPath));
            return db.Chapters.Count();
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException) { return 0; }
    }

    /// <summary>Run ids (oldest first; the id starts with its time) per book, from each run's run.json.</summary>
    public static IReadOnlyDictionary<string, List<string>> RunsByBook(string root)
    {
        var byBook = new Dictionary<string, List<string>>();
        if (!Directory.Exists(RepoPaths.Runs(root))) return byBook;
        foreach (var dir in Directory.GetDirectories(RepoPaths.Runs(root)).Order(StringComparer.Ordinal))
        {
            if (RunBook(dir) is not { } book) continue;
            (byBook.TryGetValue(book, out var list) ? list : byBook[book] = []).Add(Path.GetFileName(dir));
        }
        return byBook;
    }

    /// <summary>The book of a run folder: run.json's <c>book</c>, else its only entry in <c>books</c>.</summary>
    public static string? RunBook(string runDir)
    {
        try
        {
            var path = Path.Combine(runDir, RunFolder.RunJson);
            if (!File.Exists(path)) return null;
            var json = JsonNode.Parse(RunFolder.ReadShared(path));
            return json?["book"]?.GetValue<string>() ?? ((json?["books"] as JsonArray) is { Count: 1 } one ? one[0]?["id"]?.GetValue<string>() : null);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or IOException) { return null; }
    }

    /// <summary>A run's cost.json totals; the cost is null when no price is configured.</summary>
    public static (long In, long Out, decimal? Cost)? Cost(string runDir)
    {
        var stages = IndexPlan.Stages(runDir);
        if (stages.Count == 0) return null;
        var total = IndexPlan.Total(stages);
        return (total.InputTokens, total.OutputTokens, total.Cost);
    }

    public static decimal? Money(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.Number ? v.GetValue<decimal>() : null;
}

/// <summary>
/// The <c>books</c> commands, apart from the console they run in so the tests can drive them. Exit codes: 0 ok, 1 a
/// download failed, 2 bad input (or a prompt was needed and there is no terminal), 4 the book was added and a Stage 0 check failed.
/// </summary>
public sealed class BooksRunner(BookIntake intake, string root, IAnsiConsole console, bool interactive)
{
    public static BooksRunner ForConsole() =>
        new(new BookIntake(BookIntake.CreateClient(TemporalGraph.Llm.LlmSettings.LoadConfiguration()), RepoPaths.FindRoot()), RepoPaths.FindRoot(), AnsiConsole.Console, CliContext.Interactive);

    /// <summary>Adds a book. Interactively: asks for the ref when none is given and lets the detected chapter count be corrected.</summary>
    /// <returns>The exit code and, when the book was added, its outcome.</returns>
    public async Task<(int Exit, AddOutcome? Outcome)> AddAsync(BookAddOptions options, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.Ref))
        {
            if (!interactive) return (CliContext.NeedsTerminal("books add needs a book", "books add <gutenberg-number | url | file.txt> [--id <slug>] [--index]"), null);
            var asked = await console.AskAsync(new TextPrompt<string>("A Project Gutenberg [bold]number[/] or [bold]URL[/], a .txt URL, or a .txt file (empty: back):").AllowEmpty()
                .Validate(s => s.Trim().Length == 0 || BookRefs.Parse(s) is not null ? ValidationResult.Success() : ValidationResult.Error("That is not a Gutenberg number, a gutenberg.org URL, a .txt URL or a .txt file.")), ct);
            if (asked.Trim().Length == 0)
            {
                console.MarkupLine("Nothing was added.");
                return (Exit.Ok, null);
            }
            options = options with { Ref = asked };
        }

        AddOutcome outcome;
        RunLog.Current = intake.Log;
        try
        {
            outcome = interactive
                ? await console.Status().StartAsync($"Fetching {Markup.Escape(BookRefs.Parse(options.Ref)?.Describe ?? options.Ref)}… [grey](Ctrl+C stops it)[/]", _ => intake.AddAsync(options, ct))
                : await intake.AddAsync(options, ct);
        }
        catch (BookInputException ex) { return (CliContext.Fail(Exit.BadInput, ex.Message), null); }
        catch (BookDownloadException ex)
        {
            // No run folder was made: the requests and what they answered go to the CLI's own log.
            intake.Log.AttachTo(RunLog.CliLogPath(root));
            return (CliContext.Fail(Exit.Failed, $"{ex.Message} Details: {Path.GetRelativePath(root, RunLog.CliLogPath(root)).Replace('\\', '/')}", "download-failed"), null);
        }

        // The same book with the same text again is not a failure.
        if (outcome.Unchanged)
        {
            CliContext.Result = new JsonObject { ["id"] = outcome.Id, ["title"] = outcome.Title, ["author"] = outcome.Author, ["sha256"] = outcome.Sha256, ["sourceText"] = outcome.SourceText, ["unchanged"] = true };
            console.MarkupLine($"[bold]{Markup.Escape(outcome.Id)}[/] is already present, unchanged (the text has the same SHA-256).");
            return (Exit.Ok, outcome);
        }

        if (interactive && outcome.Profile is { } detected && outcome.ProfileSource == "detected")
        {
            console.MarkupLine($"Detected [bold]{detected.ExpectedChapters}[/] chapters (style: {Markup.Escape(detected.StyleName)}).");
            if (!await console.ConfirmAsync("Correct?", true, ct))
            {
                var count = await console.AskAsync(new TextPrompt<int>("How many chapters does the book have? (0 to keep the detected count):")
                    .Validate(n => n >= 0 ? ValidationResult.Success() : ValidationResult.Error("0 or more.")), ct);
                if (count > 0)
                {
                    var again = intake.Prepare(outcome.Id, count);
                    outcome = outcome with { Profile = again.Profile, ProfileSource = again.Source, Chapters = again.Chapters, Checks = again.Checks, RunId = again.RunId };
                }
            }
        }

        CliContext.Result = Json(outcome);
        Render(outcome);
        if (outcome.RunId is { } logged) console.MarkupLine($"[grey]Log: runs/{logged}/{RunLog.FileName}[/]");
        if (outcome.ChecksPassed)
        {
            RunLog.Current = null;
            return (Exit.Ok, outcome);
        }
        var failed = string.Join("; ", outcome.Checks.Where(c => !c.Passed).Select(c => $"{c.Name}: {c.Details}"));
        console.MarkupLine($"[yellow]The book was added, and a Stage 0 check failed.[/] Fix it with [bold]books add {Markup.Escape(options.Ref)} --id {Markup.Escape(outcome.Id)} --force --expect-chapters <n>[/] or by editing data/books/profiles/{Markup.Escape(outcome.Id)}.json; `index run` refuses the book until the checks pass.");
        return (CliContext.Problem(Exit.CheckFailed, failed, "stage0-check"), outcome);
    }

    /// <summary><c>books add --dry-run</c>.</summary>
    public async Task<int> DryRunAsync(BookAddOptions options, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.Ref)) return CliContext.Fail(Exit.BadInput, "books add --dry-run needs the book: books add <gutenberg-number | url | file.txt> --dry-run");
        AddPlan plan;
        try { plan = await intake.PlanAsync(options, ct); }
        catch (BookInputException ex) { return CliContext.Fail(Exit.BadInput, ex.Message); }
        CliContext.Result = new { dryRun = true, plan.Ref, plan.Gutenberg, plan.Id, plan.Title, plan.Author, plan.Urls, plan.LocalPath, plan.Exists, plan.Note };
        console.MarkupLine($"Would add [bold]{Markup.Escape(plan.Id ?? "(the id made from the text's Title: line)")}[/]{(plan.Title is { } t ? $": {Markup.Escape(t)}{(plan.Author is { } a ? ", " + Markup.Escape(a) : "")}" : "")}");
        if (plan.LocalPath is { } file) console.MarkupLine($"  from the file {Markup.Escape(file)}");
        foreach (var url in plan.Urls) console.MarkupLine($"  from {Markup.Escape(url)}");
        console.MarkupLine($"  {Markup.Escape(plan.Note)}");
        if (plan.Id is { } id) console.MarkupLine($"  to   data/books/source/{Markup.Escape(id)}.txt, data/books/books.json and data/books/{Markup.Escape(id)}.sqlite");
        if (plan.Exists) console.MarkupLine($"  [yellow]{Markup.Escape(plan.Id!)} is already in data/books/books.json[/]: with the same text nothing would change; with another text it is refused without --force.");
        console.MarkupLine("Dry run: nothing was fetched or written.");
        return Exit.Ok;
    }

    public static JsonObject Json(AddOutcome o) => new()
    {
        ["id"] = o.Id,
        ["title"] = o.Title,
        ["author"] = o.Author,
        ["gutenberg"] = o.Gutenberg,
        ["sourceUrl"] = o.SourceUrl,
        ["sourceText"] = o.SourceText,
        ["sha256"] = o.Sha256,
        ["bytes"] = o.Bytes,
        ["replaced"] = o.Replaced,
        ["profile"] = o.Profile is null ? null : new JsonObject
        {
            ["source"] = o.ProfileSource,
            ["expectedChapters"] = o.Profile.ExpectedChapters,
            ["headings"] = o.Profile.Headings.ToString(),
            ["skipHeadings"] = new JsonArray(o.Profile.SkipHeadings.Select(s => (JsonNode?)s).ToArray()),
            ["numberFixes"] = o.Profile.NumberFixes is null ? null : new JsonObject(o.Profile.NumberFixes.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))),
        },
        ["chapters"] = new JsonArray(o.Chapters.Select(c => (JsonNode?)new JsonObject { ["number"] = c.Number, ["title"] = c.Title, ["firstLine"] = c.FirstLine, ["chars"] = c.Chars }).ToArray()),
        ["checks"] = new JsonArray(o.Checks.Select(c => (JsonNode?)new JsonObject { ["name"] = c.Name, ["passed"] = c.Passed, ["details"] = c.Details }).ToArray()),
        ["checksPassed"] = o.ChecksPassed,
        ["runId"] = o.RunId,
    };

    private void Render(AddOutcome o)
    {
        console.MarkupLine($"{(o.Replaced ? "Fetched again" : "Added")} [bold]{Markup.Escape(o.Id)}[/]: {Markup.Escape(o.Title)}, {Markup.Escape(o.Author)}");
        console.MarkupLine($"  text   {Markup.Escape(o.SourceText)} ({o.Bytes:N0} bytes) from {Markup.Escape(o.SourceUrl)}");
        console.MarkupLine($"  sha256 {o.Sha256}");
        if (o.Chapters.Count > 0)
        {
            var table = new Table().Border(TableBorder.Rounded).AddColumn(new TableColumn("Ch.").RightAligned()).AddColumn("Title").AddColumn("First line").AddColumn(new TableColumn("Chars").RightAligned());
            foreach (var c in o.Chapters) table.AddRow(c.Number.ToString(), Markup.Escape(c.Title), Markup.Escape(c.FirstLine), c.Chars.ToString("N0"));
            console.Write(table);
        }
        var checks = new Table().Border(TableBorder.Rounded).AddColumns("Stage 0 check", "Result", "Details");
        foreach (var c in o.Checks) checks.AddRow(Markup.Escape(c.Name), c.Passed ? "[green]pass[/]" : "[red]FAIL[/]", Markup.Escape(c.Details.Length <= 110 ? c.Details : c.Details[..110] + "…"));
        console.Write(checks);
    }

    /// <summary>What follows a book that was added.</summary>
    public static string NextAfterAdd(string id) => $"{CliName.Wrapper} index run --book {id}   (preview: --dry-run)";

    public int List()
    {
        var rows = BookShelf.List(root);
        CliContext.Result = new { books = rows };
        console.Write(Table(rows));
        return Exit.Ok;
    }

    public static Table Table(IReadOnlyList<BookRow> rows)
    {
        var table = new Table().Border(TableBorder.Rounded).AddColumns("Id", "Title", "Author").AddColumn(new TableColumn("Gutenberg").RightAligned())
            .AddColumn(new TableColumn("Chapters").RightAligned()).AddColumns("Indexed", "Last run").AddColumn(new TableColumn("Cost").RightAligned());
        foreach (var b in rows)
            table.AddRow(Markup.Escape(b.Id), Markup.Escape(b.Title), Markup.Escape(b.Author), b.Gutenberg?.ToString() ?? "-", b.Chapters > 0 ? b.Chapters.ToString() : b.SourceText ? "0" : "no text",
                b.IndexedRun is { } run ? $"[green]yes[/] ({Markup.Escape(run)})" : "no", Markup.Escape(b.LastRun ?? "-"), b.LastRunCost is { } c ? c.ToString("0.00") : b.LastRun is null ? "-" : "not known");
        return table;
    }

    public int Remove(string id, bool yes) => RemoveAsync(id, yes, CancellationToken.None).GetAwaiter().GetResult();

    public async Task<int> RemoveAsync(string id, bool yes, CancellationToken ct)
    {
        if (!yes)
        {
            if (!interactive) return CliContext.NeedsTerminal("books remove asks for confirmation", $"books remove {id} --yes");
            if (!await console.ConfirmAsync($"Remove {Markup.Escape(id)}: its manifest entry, source text and database? (runs/ are kept)", false, ct))
            {
                console.MarkupLine("Nothing changed.");
                return Exit.Ok;
            }
        }
        try
        {
            var removed = intake.Remove(id);
            CliContext.Result = new { id, removed };
            console.MarkupLine($"Removed [bold]{Markup.Escape(id)}[/] from data/books/books.json{(removed.Count > 0 ? " and " + Markup.Escape(string.Join(", ", removed)) : "")}. Its runs are kept.");
            return Exit.Ok;
        }
        catch (BookInputException ex) { return CliContext.Fail(Exit.BadInput, ex.Message); }
    }

    public async Task<int> RefetchAsync(string id, CancellationToken ct)
    {
        try
        {
            var r = await intake.RefetchAsync(id, ct);
            CliContext.Result = new { r.Id, r.SourceUrl, r.Sha256Committed, r.Sha256Fetched, r.Bytes, r.Changed };
            console.MarkupLine($"{Markup.Escape(r.Id)}: {(r.Changed ? "[yellow]changed[/]" : "[green]unchanged[/]")} ({Markup.Escape(r.SourceUrl)})");
            console.MarkupLine($"  committed {r.Sha256Committed}");
            console.MarkupLine($"  fetched   {r.Sha256Fetched} ({r.Bytes:N0} bytes)");
            return Exit.Ok;
        }
        catch (BookInputException ex) { return CliContext.Fail(Exit.BadInput, ex.Message); }
        catch (BookDownloadException ex) { return CliContext.Fail(Exit.Failed, ex.Message, "download-failed"); }
    }
}

// ── Spectre commands ───────────────────────────────────────────────────────────────────────────────────────────────────────────

public sealed class BooksAddCommand : AsyncCommand<BooksAddCommand.Settings>
{
    public sealed class Settings : GlobalSettings
    {
        [CommandArgument(0, "[ref]")][Description("A Project Gutenberg number (2852), a gutenberg.org URL, a .txt URL, or a local .txt file.")] public string? Ref { get; init; }
        [CommandOption("--id <SLUG>")][Description("The book id; default: made from the title (\"The Hound of the Baskervilles\" → hound-of-the-baskervilles).")] public string? Id { get; init; }
        [CommandOption("--title <TITLE>")][Description("Default: from the Gutenberg catalogue, else the text's Title: line.")] public string? Title { get; init; }
        [CommandOption("--author <AUTHOR>")][Description("Default: from the Gutenberg catalogue, else the text's Author: line.")] public string? Author { get; init; }
        [CommandOption("--expect-chapters <N>")][Description("The chapter count, when the detected one is wrong (written to data/books/profiles/<id>.json).")] public int? ExpectChapters { get; init; }
        [CommandOption("--force")][Description("Fetch a book whose id is already in the manifest again (its roster, voices and images are kept).")] public bool Force { get; init; }
        [CommandOption("--index")][Description("Then index it: index run --book <id> --yes.")] public bool Index { get; init; }
        [CommandOption("--llm <NAME>")][Description("With --index: a named LLM profile; default: Indexing:Llm.")] public string? Llm { get; init; }
        [CommandOption("--dry-run")][Description("Show what would be fetched and the id the book would get; fetch and write nothing.")] public bool DryRun { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        var options = new BookAddOptions(s.Ref ?? "", s.Id, s.Title, s.Author, s.ExpectChapters, s.Force);
        if (s.DryRun) return await BooksRunner.ForConsole().DryRunAsync(options, ct);
        var (exit, outcome) = await BooksRunner.ForConsole().AddAsync(options, ct);
        if (exit != Exit.Ok || outcome is null || outcome.Unchanged) return exit;
        var index = s.Index || (CliContext.Interactive && await AnsiConsole.Console.ConfirmAsync("Start indexing now?", false, ct));
        if (!index)
        {
            CliContext.Next(BooksRunner.NextAfterAdd(outcome.Id));
            return exit;
        }
        var added = CliContext.Result;
        var code = await IndexRunner.ForConsole().RunAsync(new IndexRunOptions(outcome.Id, Llm: s.Llm, Yes: s.Index), ct);
        CliContext.Result = new { book = added, index = CliContext.Result };
        return code;
    }
}

public sealed class BooksListCommand : Command<GlobalSettings>
{
    public override int Execute(CommandContext context, GlobalSettings settings, CancellationToken cancellationToken) => BooksRunner.ForConsole().List();
}

public sealed class BooksRemoveCommand : AsyncCommand<BooksRemoveCommand.Settings>
{
    public sealed class Settings : GlobalSettings
    {
        [CommandArgument(0, "<id>")][Description("The book id.")] public string Id { get; init; } = "";
        [CommandOption("--yes")][Description("Don't ask for confirmation.")] public bool Yes { get; init; }
    }

    public override Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct) => BooksRunner.ForConsole().RemoveAsync(s.Id, s.Yes, ct);
}

public sealed class BooksRefetchCommand : AsyncCommand<BooksRefetchCommand.Settings>
{
    public sealed class Settings : GlobalSettings
    {
        [CommandArgument(0, "<id>")][Description("The book id.")] public string Id { get; init; } = "";
    }

    public override Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct) => BooksRunner.ForConsole().RefetchAsync(s.Id, ct);
}
