using Spectre.Console;
using Spectre.Console.Cli;
using TemporalGraph.Chat;
using TemporalGraph.Cli.Books;
using TemporalGraph.Cli.Repl;
using TemporalGraph.Cli.Runs;
using TemporalGraph.Cli.Setup;
using TemporalGraph.Llm;
using TemporalGraph.Query;

namespace TemporalGraph.Cli;

/// <summary>
/// The CLI with no arguments at a terminal: a banner, one status line and a menu. Every entry calls what the
/// command of the same name calls; the menu only collects the choice. Without a terminal the CLI prints its help and exits 2
/// (<see cref="CliApp.RunAsync"/>), as <c>setup</c> does.
/// </summary>
public sealed class HomeCommand : AsyncCommand<GlobalSettings>
{
    public static readonly IReadOnlyList<string> Menu =
    [
        "Add a book from Project Gutenberg",
        "Index a book",
        "Query a book",
        "Chat with a character",
        "Books and runs",
        "Set up a model",
        "Exit",
    ];

    /// <summary>"Chat model: … · Indexing model: … · 3 books, 3 indexed".</summary>
    public static string StatusLine(string root, Microsoft.Extensions.Configuration.IConfiguration config)
    {
        static string Model(LlmSettings llm) => llm.Validate().Count == 0 ? $"{llm.Model} (ready)" : "not set up";
        var chat = LlmSettings.From(config, ChatSettings.LlmProfile(config));
        var indexing = LlmSettings.From(config, IndexingShared.IndexingSettings.From(config).DefaultLlmProfile);
        var books = BookShelf.List(root);
        return $"Chat model: {Model(chat)} · Indexing model: {Model(indexing)} · {books.Count} book{(books.Count == 1 ? "" : "s")}, {books.Count(b => b.IndexedRun is not null)} indexed";
    }

    public override async Task<int> ExecuteAsync(CommandContext context, GlobalSettings settings, CancellationToken ct)
    {
        if (!CliContext.Interactive) return CliContext.NeedsTerminal("The menu needs a person", "--help");
        var root = RepoPaths.FindRoot();
        var console = AnsiConsole.Console;
        console.Write(new FigletText("Temporal Graph").Color(Color.Aqua));
        while (true)
        {
            var config = LlmSettings.LoadConfiguration();
            console.MarkupLine($"[grey]{Markup.Escape(StatusLine(root, config))}[/]");
            // Ctrl+C at the menu, or in anything it opened, ends the CLI with "Cancelled." and exit 130 (CliApp); Esc is Exit here.
            var choice = await console.AskAsync(new SelectionPrompt<string>().Title($"What would you like to do? {Prompts.EscExit}").PageSize(12)
                .AddChoices(Menu).AddCancelResult(Menu[^1]), ct);
            switch (Menu.ToList().IndexOf(choice))
            {
                case 0:
                    var (exit, added) = await BooksRunner.ForConsole().AddAsync(new BookAddOptions(""), ct);
                    if (added is null || exit != Exit.Ok || added.Unchanged) break;
                    if (await console.ConfirmAsync("Start indexing now?", false, ct)) await IndexRunner.ForConsole().RunAsync(new IndexRunOptions(added.Id), ct);
                    else CliContext.Next(BooksRunner.NextAfterAdd(added.Id));
                    break;
                case 1:
                    await IndexRunner.ForConsole().RunAsync(new IndexRunOptions(null), ct);
                    break;
                case 2:
                    await QueryAsync(root, console, ct);
                    break;
                case 3:
                    await ChatConsole.OpenAsync(root, config, null, null, null, null, null, ct);
                    break;
                case 4:
                    IndexRunner.ForConsole().Status(null, null);
                    break;
                case 5:
                    await SetupRunner.ForConsole().WalkthroughAsync(ct);
                    break;
                default:
                    return Exit.Ok;
            }
            // A choice that could not go ahead said why; the menu comes back either way.
            CliContext.Problems.Clear();
            console.WriteLine();
        }
    }

    /// <summary>The menu's "Query a book": the book (the manifest's books, "+ Add a book…" first, "← Back" last), then the console.</summary>
    public static async Task<int> QueryAsync(string root, IAnsiConsole console, CancellationToken ct, Func<CancellationToken, Task<string?>>? addBook = null, ILineReader? lines = null)
    {
        var book = await BookPicker.PickAsync(console, root, b => b.Chapters > 0,
            b => $"{b.Id} — {b.Title}{(b.IndexedRun is null ? " (not indexed: chapters only)" : "")}", addBook ?? BookPicker.AddAtConsole, ct);
        if (book is null) return Exit.Ok;
        var graph = BookGraph.Load(Path.Combine(RepoPaths.Books(root), book + ".sqlite"));
        if (graph.Chapters.Count == 0)
        {
            console.MarkupLine($"{Markup.Escape(book)} has no chapters yet (a Stage 0 check failed when it was added): `index status --book {Markup.Escape(book)}`.");
            return Exit.Ok;
        }
        return await new QueryConsole(new QuerySession(graph, book, root, graph.Chapters.Keys.Max(), false, null), console, lines ?? new LineEditor()).RunAsync(ct);
    }
}
