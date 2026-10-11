using System.Text.Json;
using System.Text.Json.Serialization;
using TemporalGraph.Cli.Runs;

namespace TemporalGraph.Cli.Indexing;

/// <summary>
/// What Stage 0 needs to know about one book's source text. Detected from the text (<see cref="Detect"/>), unless the book
/// has an override file, <c>data/books/profiles/&lt;id&gt;.json</c>, with the same fields. The first three books keep
/// their hand-checked values as override files (#2852, #1257 and #120).
/// </summary>
/// <param name="ExpectedChapters">Chapter count the chapter-count check requires.</param>
/// <param name="SkipHeadings">
/// Body lines (exact, whole line) that start a part to skip. A skipped part runs to the next chapter
/// heading or to the end of the body.
/// </param>
/// <param name="Headings">How a body chapter heading looks (<see cref="HeadingStyle"/>).</param>
/// <param name="NumberFixes">
/// Chapter title → its number, for a heading misnumbered in the source text (the check then passes, and its details name the fix).
/// </param>
public sealed record BookProfile(string BookId, int ExpectedChapters, IReadOnlyList<string> SkipHeadings,
    HeadingStyle Headings = HeadingStyle.ChapterWord, IReadOnlyDictionary<string, int>? NumberFixes = null)
{
    /// <summary>The profile of a text, detected (<see cref="Stage0.Detect"/>); null without Gutenberg START/END lines.</summary>
    public static BookProfile? Detect(string text, string bookId = "") => Stage0.Detect(bookId, text);

    /// <summary>"Chapter N." and the like, for a person to confirm.</summary>
    public string StyleName => Headings switch
    {
        HeadingStyle.BareRoman => "a Roman numeral on its own line",
        HeadingStyle.ChapterCaps => "CHAPTER N",
        HeadingStyle.Numbered => "N. Title",
        _ => "Chapter N.",
    };
}

public enum HeadingStyle
{
    /// <summary>"Chapter 1." / "Chapter I." on its own line; contents lines " Chapter 1  Title".</summary>
    ChapterWord,

    /// <summary>A bare Roman numeral on its own line ("XVI"), the title on the next; contents lines "     XVI.  TITLE . . . 100",
    /// a title that wraps continuing on the next indented line.</summary>
    BareRoman,

    /// <summary>"CHAPTER I", "CHAPTER 12." or "CHAPTER ONE" in capitals, the title on the same line or the next.</summary>
    ChapterCaps,

    /// <summary>"1. The Title".</summary>
    Numbered,
}

/// <summary>The profile of a book: its override file when it has one, else detected from its source text on disk.</summary>
public static class BookProfiles
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string OverridePath(string root, string bookId) => Path.Combine(RepoPaths.Books(root), "profiles", bookId + ".json");

    public static BookProfile? ReadOverride(string root, string bookId)
    {
        var path = OverridePath(root, bookId);
        return File.Exists(path) ? JsonSerializer.Deserialize<BookProfile>(File.ReadAllText(path), Json)! with { BookId = bookId } : null;
    }

    public static void WriteOverride(string root, BookProfile profile)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(OverridePath(root, profile.BookId))!);
        File.WriteAllText(OverridePath(root, profile.BookId), JsonSerializer.Serialize(profile, Json) + "\n");
    }

    /// <summary>True when the book has a source text under <paramref name="root"/> (the test of "can Stage 0 run on it").</summary>
    public static bool Has(string root, string bookId) => File.Exists(SourceTexts.PathOf(root, bookId));

    /// <summary>The override, else the detected profile.</summary>
    /// <exception cref="InvalidDataException">The text has no Gutenberg START/END lines.</exception>
    public static BookProfile For(string root, string bookId) =>
        ReadOverride(root, bookId)
        ?? BookProfile.Detect(System.Text.Encoding.UTF8.GetString(SourceTexts.Read(root, bookId)), bookId)
        ?? throw new InvalidDataException($"{bookId}: Gutenberg START/END lines not found.");

    public static BookProfile For(string bookId) => For(RepoPaths.FindRoot(), bookId);
}
