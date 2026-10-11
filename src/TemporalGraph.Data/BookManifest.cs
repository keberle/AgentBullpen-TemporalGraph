using System.Text.Json;

namespace TemporalGraph.Data;

/// <summary>One book in <c>books.json</c>. <see cref="Id"/> is also the database file name: <c>&lt;id&gt;.sqlite</c>.</summary>
/// <param name="Chat">The chat roster: canonical names or aliases of the characters the web chat offers, in display order;
/// null when the manifest has none. Asking <i>about</i> anyone else stays possible through the tools.</param>
/// <param name="Hero">The book's 3:2 hero image, relative to <c>web/public</c> (<c>books/&lt;id&gt;.webp</c>); null when there is none.</param>
/// <param name="Avatars">The folder of the roster's 512×512 avatars, relative to <c>web/public</c>
/// (<c>books/&lt;id&gt;/characters</c>, one <c>&lt;slug&gt;.webp</c> per roster name); null when the book has none (initials then).</param>
/// <param name="Voices">A manner-of-speech line per roster name (keys as in <paramref name="Chat"/>), added to that character's
/// prompt as "Your voice: …". About manner only, never plot. Null or a missing name: no voice line.</param>
/// <param name="AnchorEdtf">The one assumed date (EDTF) the book's story time is derived from; written by <c>index promote</c>. Null for a book
/// indexed before the full schema.</param>
/// <param name="AnchorNote">The assumption in words (the clue in the text the anchor rests on).</param>
/// <param name="Gutenberg">The Project Gutenberg ebook number the text came from; null for a text from elsewhere.</param>
/// <param name="SourceUrl">Where <c>books add</c> downloaded the text from (or the local path it copied).</param>
/// <param name="Sha256">SHA-256 of <c>data/books/source/&lt;id&gt;.txt</c> as downloaded.</param>
/// <param name="FetchedAt">When it was downloaded (ISO 8601, UTC).</param>
public record BookEntry(string Id, string Title, string Author, IReadOnlyList<string>? Chat = null, string? Hero = null, string? Avatars = null,
    IReadOnlyDictionary<string, string>? Voices = null, string? AnchorEdtf = null, string? AnchorNote = null,
    int? Gutenberg = null, string? SourceUrl = null, string? Sha256 = null, string? FetchedAt = null)
{
    public string DatabaseFileName => $"{Id}.sqlite";
}

/// <summary>Reads and writes the book manifest, <c>books.json</c> (a JSON array of <see cref="BookEntry"/>).</summary>
public static class BookManifest
{
    public const string FileName = "books.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static IReadOnlyList<BookEntry> Read(Stream stream) =>
        JsonSerializer.Deserialize<List<BookEntry>>(stream, Json) ?? [];

    public static IReadOnlyList<BookEntry> Read(string path)
    {
        using var stream = File.OpenRead(path);
        return Read(stream);
    }

    public static void Write(string path, IEnumerable<BookEntry> books) =>
        File.WriteAllText(path, JsonSerializer.Serialize(books.Select(b => new { b.Id, b.Title, b.Author, b.Hero, b.Avatars, b.AnchorEdtf, b.AnchorNote, b.Gutenberg, b.SourceUrl, b.Sha256, b.FetchedAt, b.Chat, b.Voices }).ToList(), Json) + "\n");

    /// <summary>Adds <paramref name="book"/>, or replaces the entry with the same id (keeping its position, and its chat roster
    /// hero, avatars and voices when <paramref name="book"/> has none).</summary>
    public static IReadOnlyList<BookEntry> AddOrUpdate(IReadOnlyList<BookEntry> books, BookEntry book)
    {
        var list = books.ToList();
        var ix = list.FindIndex(b => b.Id == book.Id);
        if (ix >= 0) list[ix] = book with { Chat = book.Chat ?? list[ix].Chat, Hero = book.Hero ?? list[ix].Hero, Avatars = book.Avatars ?? list[ix].Avatars, Voices = book.Voices ?? list[ix].Voices, AnchorEdtf = book.AnchorEdtf ?? list[ix].AnchorEdtf, AnchorNote = book.AnchorNote ?? list[ix].AnchorNote, Gutenberg = book.Gutenberg ?? list[ix].Gutenberg, SourceUrl = book.SourceUrl ?? list[ix].SourceUrl, Sha256 = book.Sha256 ?? list[ix].Sha256, FetchedAt = book.FetchedAt ?? list[ix].FetchedAt };
        else list.Add(book);
        return list;
    }
}
