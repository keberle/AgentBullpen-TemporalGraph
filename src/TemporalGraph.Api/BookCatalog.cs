using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using TemporalGraph.Chat;
using TemporalGraph.Data;
using TemporalGraph.Query;

namespace TemporalGraph.Api;

/// <summary>One book as the API serves it: its graph (in memory), chapters and characters.</summary>
/// <param name="ChatCharacters">The characters with their aliases, node and initials, for chat (same order as <paramref name="Characters"/>).</param>
/// <param name="Locations">The locations table's names with their first mention (the at_location hint).</param>
/// <param name="Roster">The manifest's chat roster resolved to characters, in roster order; names that match nothing are
/// left out (<see cref="UnknownRosterNames"/>).</param>
/// <param name="RosterNames">For each <paramref name="Roster"/> character, the manifest name that resolved to it (the avatar's
/// slug comes from it: "Sherlock Holmes" → sherlock-holmes.webp, not the canonical "Mr. Sherlock Holmes").</param>
public sealed record LoadedBook(BookEntry Entry, BookGraph Graph, IReadOnlyList<BookChapter> Chapters, IReadOnlyList<string> Characters,
    IReadOnlyList<ChatCharacter> ChatCharacters, IReadOnlyList<(string Name, long? EvidenceStart)> Locations,
    IReadOnlyList<ChatCharacter> Roster, IReadOnlyList<string> UnknownRosterNames, IReadOnlyList<string> RosterNames,
    TemporalGraph.Chat.BookVoice? Voice = null)
{
    /// <summary>The whole graph for the 2D/3D tabs, built once on first use.</summary>
    public BookGraphPayload Payload => _payload ??= BookGraphPayload.From(Graph);

    private BookGraphPayload? _payload;

    /// <summary>Aliases shared by more than one entity (skipped for display names unless they're the only one revealed).</summary>
    public IReadOnlySet<string> Ambiguous => _ambiguous ??= TemporalGraph.Chat.ChatTools.AmbiguousAliases(Graph);

    private IReadOnlySet<string>? _ambiguous;
}

/// <summary>
/// The embedded book databases, each loaded once per process and cached (registered as a singleton). Opening: the
/// embedded <c>books/&lt;id&gt;.sqlite</c> is copied to a temp file once per process (SQLite can't open a .NET stream;
/// <see cref="BookDatabase.EmbeddedCopy"/>), then opened with <c>Mode=ReadOnly</c> (<see cref="BookDatabase.OpenReadOnlyFile"/>,
/// used by <see cref="BookGraph.Load"/>). The graph, chapters and characters are read once and the file is closed.
/// </summary>
public sealed class BookCatalog(IReadOnlyList<BookEntry> manifest, ILogger<BookCatalog>? logger = null)
{
    public static readonly ActivitySource Source = new(SourceName);
    public const string SourceName = "TemporalGraph.Api";

    private readonly ConcurrentDictionary<string, Lazy<LoadedBook>> _books = new();

    public IReadOnlyList<BookEntry> Manifest => manifest;

    /// <summary>The book, loaded on first use; a GraphQL error for an unknown id.</summary>
    public LoadedBook Get(string bookId)
    {
        var entry = manifest.FirstOrDefault(b => b.Id == bookId)
            ?? throw new GraphQLException(ErrorBuilder.New().SetMessage($"Unknown book id '{bookId}'.").SetCode("UNKNOWN_BOOK").Build());
        return _books.GetOrAdd(bookId, _ => new Lazy<LoadedBook>(() => Load(entry))).Value;
    }

    /// <summary>Loads every book once (at startup), so a roster name that matches nothing is logged then.</summary>
    public void LoadAll()
    {
        foreach (var entry in manifest) Get(entry.Id);
    }

    private LoadedBook Load(BookEntry entry)
    {
        var path = BookDatabase.EmbeddedCopy(BookResources.Assembly, BookResources.DatabaseResourceName(entry));
        // Roster characters carry their books.json spelling (rosterName), shown once revealed.
        var graph = TemporalGraph.Chat.ChatCharacters.WithRosterNames(BookGraph.Load(path), entry.Chat);
        using var db = BookDatabase.OpenReadOnlyFile(path);
        var chapters = db.Chapters.OrderBy(c => c.Number).Select(c => new BookChapter(c.Number, c.Title)).ToList();
        // Canonical names from the characters table, ordered by first mention (the node's evidence_start).
        var characters = db.Characters.Include(c => c.Node).AsEnumerable()
            .OrderBy(c => c.Node?.EvidenceStart ?? int.MaxValue).ThenBy(c => c.Id)
            .Select(c => c.Name).ToList();
        var chat = TemporalGraph.Chat.ChatCharacters.Load(path, graph);
        var (roster, unknown, rosterNames) = ResolveRoster(entry.Chat ?? [], chat);
        foreach (var name in unknown)
            logger?.LogWarning("Chat roster of {BookId}: '{Name}' matches no character in the promoted graph; skipped.", entry.Id, name);
        return new LoadedBook(entry, graph, chapters, characters, chat, TemporalGraph.Chat.ChatCharacters.LoadLocations(path), roster, unknown, rosterNames,
            BookResources.ReadVoice(entry));
    }

    /// <summary>
    /// The roster's names resolved to characters (<see cref="ChatCharacters.Resolve"/>: a canonical name, else an alias,
    /// case-insensitive), in roster order, each character once, with the manifest name that resolved it; names that match nothing are
    /// returned separately, never invented.
    /// </summary>
    public static (IReadOnlyList<ChatCharacter> Roster, IReadOnlyList<string> Unknown, IReadOnlyList<string> Names) ResolveRoster(
        IReadOnlyList<string> names, IReadOnlyList<ChatCharacter> characters)
    {
        var roster = new List<ChatCharacter>();
        var unknown = new List<string>();
        var resolvedBy = new List<string>();
        foreach (var name in names)
        {
            var canonical = TemporalGraph.Chat.ChatCharacters.Resolve(characters, name);
            if (canonical is null) unknown.Add(name);
            else if (roster.All(c => c.Name != canonical))
            {
                roster.Add(characters.First(c => c.Name == canonical));
                resolvedBy.Add(name);
            }
        }
        return (roster, unknown, resolvedBy);
    }
}
