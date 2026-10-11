using System.Reflection;
using TemporalGraph.Data;

namespace TemporalGraph.Api;

/// <summary>The book manifest and databases embedded in this assembly (see TemporalGraph.Api.csproj).</summary>
public static class BookResources
{
    public static Assembly Assembly => typeof(BookResources).Assembly;

    public const string ManifestResourceName = "books/" + BookManifest.FileName;

    public static string DatabaseResourceName(BookEntry book) => "books/" + book.DatabaseFileName;

    /// <summary>The book's voice files (<c>books/&lt;id&gt;/voice/&lt;file&gt;</c>), or null when it has none.</summary>
    public static TemporalGraph.Chat.BookVoice? ReadVoice(BookEntry book)
    {
        var names = Assembly.GetManifestResourceNames().ToDictionary(n => n.Replace('\\', '/'), n => n, StringComparer.Ordinal);
        return TemporalGraph.Chat.BookVoice.Load(book, file =>
        {
            if (!names.TryGetValue($"books/{book.Id}/{TemporalGraph.Chat.BookVoice.Folder}/{file}", out var resource)) return null;
            using var stream = Assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        });
    }

    public static IReadOnlyList<BookEntry> ReadManifest()
    {
        using var stream = Assembly.GetManifestResourceStream(ManifestResourceName)
            ?? throw new InvalidOperationException($"Embedded resource not found: {ManifestResourceName}");
        return BookManifest.Read(stream);
    }
}
