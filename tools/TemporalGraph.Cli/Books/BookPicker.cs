using Spectre.Console;
using TemporalGraph.Cli.Runs;

namespace TemporalGraph.Cli.Books;

/// <summary>
/// The one "Which book?" prompt (the menu's Query, <c>index run</c>, the chat console). It lists the manifest's books, read
/// each time it is shown, with a first entry that adds a book from Project Gutenberg and goes on with it, and a last entry that goes
/// back. Esc goes back too. It is only called from the interactive paths (a terminal was checked before it).
/// </summary>
public static class BookPicker
{
    public const string AddLabel = "+ Add a book from Project Gutenberg…";

    private sealed record Choice(BookRow? Book, string Label);

    /// <param name="filter">The books this prompt can go on with (those with chapters, those that are indexed).</param>
    /// <param name="add">Runs the intake and returns the id of the book it added; null when nothing was added.</param>
    /// <returns>The book id; null: back.</returns>
    public static async Task<string?> PickAsync(IAnsiConsole console, string root, Func<BookRow, bool> filter, Func<BookRow, string> label,
        Func<CancellationToken, Task<string?>> add, CancellationToken ct)
    {
        while (true)
        {
            var books = BookShelf.List(root).Where(filter).ToList();
            var addBook = new Choice(null, AddLabel);
            var back = new Choice(null, Prompts.Back);
            var choices = books.Select(b => new Choice(b, label(b))).ToList();
            var prompt = new SelectionPrompt<Choice>().Title($"Which book? {Prompts.EscBack}").PageSize(12).MoreChoicesText("[grey](more books below)[/]")
                .UseConverter(c => Markup.Escape(c.Label)).AddChoices(addBook).AddChoices(choices).AddChoices(back).AddCancelResult(back);
            // The cursor starts on the first book, so Enter picks a book as before; the entry to add one is the line above it.
            if (choices.Count > 0) prompt.DefaultValue(choices[0]);
            var picked = await console.AskAsync(prompt, ct);
            if (ReferenceEquals(picked, back)) return null;
            if (picked.Book is { } book) return book.Id;
            if (await add(ct) is { } added) return added;
        }
    }

    /// <summary>The book intake, at the console: asks for the ref, shows the chapters, and returns the new book's id.</summary>
    public static async Task<string?> AddAtConsole(CancellationToken ct) => (await BooksRunner.ForConsole().AddAsync(new BookAddOptions(""), ct)).Outcome?.Id;
}
