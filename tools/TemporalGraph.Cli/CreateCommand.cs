using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using TemporalGraph.Data;

namespace TemporalGraph.Cli;

/// <summary>
/// <c>create &lt;id&gt; --title --author [--force]</c>: builds an empty book database and adds or
/// updates its manifest entry. Paths are relative to the current directory (run from the repo root).
/// </summary>
public sealed class CreateCommand : Command<CreateCommand.Settings>
{
    public static readonly string BooksDir = Path.Combine("data", "books");

    public sealed class Settings : GlobalSettings
    {
        [CommandArgument(0, "<id>")]
        [Description("Book id; also the database file name (<id>.sqlite).")]
        public string Id { get; init; } = "";

        [CommandOption("--title <TITLE>")]
        [Description("Book title.")]
        public string Title { get; init; } = "";

        [CommandOption("--author <AUTHOR>")]
        [Description("Book author.")]
        public string Author { get; init; } = "";

        [CommandOption("--force")]
        [Description("Delete and recreate the database if it already exists.")]
        public bool Force { get; init; }

        public override ValidationResult Validate()
        {
            if (string.IsNullOrWhiteSpace(Id) || Id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                return ValidationResult.Error("<id> must be a valid file name.");
            if (string.IsNullOrWhiteSpace(Title)) return ValidationResult.Error("--title is required.");
            if (string.IsNullOrWhiteSpace(Author)) return ValidationResult.Error("--author is required.");
            return ValidationResult.Success();
        }
    }

    public override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var book = new BookEntry(settings.Id, settings.Title, settings.Author);
        var dbPath = Path.Combine(BooksDir, book.DatabaseFileName);
        var manifestPath = Path.Combine(BooksDir, BookManifest.FileName);

        var existed = File.Exists(dbPath);
        if (existed && !settings.Force)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(dbPath)} already exists.[/] Use [bold]--force[/] to delete and recreate it.");
            return 1;
        }

        AnsiConsole.Status().Start($"Creating {Markup.Escape(dbPath)}…", _ =>
        {
            if (existed) File.Delete(dbPath);
            BookDatabase.Create(dbPath);

            var books = File.Exists(manifestPath) ? BookManifest.Read(manifestPath) : [];
            BookManifest.Write(manifestPath, BookManifest.AddOrUpdate(books, book));
        });

        var table = new Table().AddColumns("Id", "Title", "Author", "Database", "Manifest");
        table.AddRow(
            Markup.Escape(book.Id),
            Markup.Escape(book.Title),
            Markup.Escape(book.Author),
            Markup.Escape(dbPath) + (existed ? " [yellow](recreated)[/]" : " [green](created)[/]"),
            Markup.Escape(manifestPath));
        AnsiConsole.Write(table);
        return 0;
    }
}
