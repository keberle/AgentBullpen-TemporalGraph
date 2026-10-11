using System.Text.Json;
using System.Text.RegularExpressions;
using TemporalGraph.Data;
using TemporalGraph.Query;

namespace TemporalGraph.Chat;

/// <summary>One line a character says in the novel (<c>data/books/&lt;id&gt;/voice/lines.json</c>).</summary>
/// <param name="Character">The roster name (<c>books.json</c> <c>chat</c>).</param>
/// <param name="Pos">The offset of the line's first word in the book text (the reader clock).</param>
/// <param name="Text">The line as the novel prints it, whitespace collapsed; a narrator's tag between two parts is cut and marked " … ".</param>
public sealed record VoiceLine(string Character, int Chapter, long Pos, string Text);

/// <summary>
/// A book's voice layer, from <c>data/books/&lt;id&gt;/voice/</c>: the book's voice guide (<c>book.md</c>), one card per roster
/// character (<c>&lt;slug&gt;.md</c>, <see cref="ChatCharacters.Slug"/>) and the example lines mined from the text (<c>lines.json</c>).
/// Cards and the guide are plain markdown; the HTML comment (sources) and the <c># </c> title line are not sent to the model.
/// </summary>
public sealed record BookVoice(string? Guide, IReadOnlyDictionary<string, string> Cards, IReadOnlyList<VoiceLine> Lines)
{
    public const string Folder = "voice";
    public const string GuideFile = "book.md";
    public const string LinesFile = "lines.json";
    public const string ReviewFile = "lines.review.md";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The guide's never-say words (<see cref="NeverSayList"/>), checked against every answer.</summary>
    public IReadOnlyList<string> NeverSay => _neverSay ??= NeverSayList(Guide);

    private IReadOnlyList<string>? _neverSay;

    /// <summary>
    /// The words of the guide's <c>Never say:</c> paragraph, comma- or semicolon-separated (text before a colon inside it,
    /// such as "nor stage-pirate cliches the book never uses:", is dropped). An item with a sense in brackets ("process (feelings)") or
    /// "for …" can't be checked by matching words and is left to the model; one whose brackets name the alternative ("gun (say pistol or
    /// musket)") is kept.
    /// </summary>
    public static IReadOnlyList<string> NeverSayList(string? guide)
    {
        if (guide is null) return [];
        var at = guide.IndexOf("Never say:", StringComparison.OrdinalIgnoreCase);
        if (at < 0) return [];
        var paragraph = guide[(at + "Never say:".Length)..];
        if (paragraph.IndexOf("\n\n", StringComparison.Ordinal) is var end and >= 0) paragraph = paragraph[..end];
        var words = new List<string>();
        foreach (var raw in paragraph.Split([',', ';']))
        {
            var item = raw.Trim().TrimEnd('.');
            if (item.LastIndexOf(':') is var colon and >= 0) item = item[(colon + 1)..].Trim();
            if (Regex.Match(item, @"\((?<q>[^)]*)\)") is { Success: true } q)
            {
                if (!q.Groups["q"].Value.TrimStart().StartsWith("say ", StringComparison.OrdinalIgnoreCase)) continue;
                item = item[..q.Index].Trim();
            }
            if (Regex.IsMatch(item, @"\bfor\b", RegexOptions.IgnoreCase)) continue;
            item = item.Trim('"', '\'', '“', '”', ' ');
            if (item.Length >= 2) words.Add(item);
        }
        return words.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The never-say words in <paramref name="answer"/> (whole words or phrases, ignoring case and the apostrophe's shape).</summary>
    public static IReadOnlyList<string> NeverSayHits(string answer, IReadOnlyList<string> words) =>
        words.Where(w => Regex.IsMatch(answer.Replace('’', '\''), @"(?<![\p{L}\p{N}])" + Regex.Escape(w.Replace('’', '\'')) + @"(?![\p{L}\p{N}])",
            RegexOptions.IgnoreCase)).ToList();

    /// <summary>The voice folder of a book under <c>data/books</c>.</summary>
    public static string FolderFor(string booksDir, string bookId) => Path.Combine(booksDir, bookId, Folder);

    /// <summary>The voice files through <paramref name="read"/> (file name → text, null when absent); null when the book has none.</summary>
    public static BookVoice? Load(BookEntry book, Func<string, string?> read)
    {
        var guide = read(GuideFile) is { } g ? Body(g) : null;
        var cards = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in book.Chat ?? [])
            if (read(ChatCharacters.Slug(name) + ".md") is { } card && Body(card) is { Length: > 0 } body)
                cards[ChatCharacters.Slug(name)] = body;
        var lines = read(LinesFile) is { } json ? JsonSerializer.Deserialize<List<VoiceLine>>(json, Json) ?? [] : [];
        return guide is null && cards.Count == 0 && lines.Count == 0 ? null : new BookVoice(guide, cards, lines);
    }

    /// <summary>From <c>data/books/&lt;id&gt;/voice/</c> on disk (the CLI).</summary>
    public static BookVoice? LoadFolder(BookEntry book, string booksDir)
    {
        var folder = FolderFor(booksDir, book.Id);
        return Directory.Exists(folder) ? Load(book, f => File.Exists(Path.Combine(folder, f)) ? File.ReadAllText(Path.Combine(folder, f)) : null) : null;
    }

    /// <summary>The markdown without HTML comments and without its first <c># </c> title line, trimmed.</summary>
    public static string Body(string markdown)
    {
        var text = Regex.Replace(markdown.Replace("\r\n", "\n"), @"<!--.*?-->", "", RegexOptions.Singleline).Trim();
        if (text.StartsWith("# ", StringComparison.Ordinal)) text = text.IndexOf('\n') is var nl and >= 0 ? text[(nl + 1)..] : "";
        return text.Trim();
    }

    /// <summary>The card of <paramref name="character"/> (a canonical name): the card of the roster name that resolves to it.</summary>
    public string? Card(BookEntry book, IReadOnlyList<ChatCharacter> characters, string character) =>
        RosterName(book, characters, character) is { } r && Cards.TryGetValue(ChatCharacters.Slug(r), out var card) ? card : null;

    /// <summary>The roster name that resolves to <paramref name="character"/> (as <see cref="ChatCharacters.Voice"/> does), or null.</summary>
    public static string? RosterName(BookEntry book, IReadOnlyList<ChatCharacter> characters, string character) =>
        book.Chat?.FirstOrDefault(r => ChatCharacters.Resolve(characters, r) == character
            || characters.FirstOrDefault(c => c.Name == character)?.Node?.Properties.GetValueOrDefault("rosterName") is string rn && ChatTools.SameName(rn, r));

    /// <summary>
    /// The example lines for one turn: the character's lines, spoilers off only those from chapters up to the reader's and none
    /// that names a person or place the reader hasn't met by <paramref name="pos"/> (an alias whose first mention is later); then the most
    /// recent first, one per chapter before a second from the same chapter. At most <paramref name="count"/>; 0 → none.
    /// </summary>
    public IReadOnlyList<VoiceLine> Examples(string rosterName, int chapter, long pos, bool spoilers, int count, BookGraph graph)
    {
        if (count <= 0) return [];
        var unrevealed = spoilers ? [] : UnrevealedNames(graph, pos);
        var eligible = Lines
            .Where(l => ChatTools.SameName(l.Character, rosterName))
            .Where(l => spoilers || l.Chapter <= chapter)
            .Where(l => !unrevealed.Any(n => NamePattern(n).IsMatch(l.Text)))
            .OrderByDescending(l => l.Chapter).ThenByDescending(l => l.Pos)
            .ToList();
        var picked = new List<VoiceLine>();
        foreach (var l in eligible)
        {
            if (picked.Count == count) break;
            if (picked.All(p => p.Chapter != l.Chapter)) picked.Add(l);
        }
        foreach (var l in eligible)
        {
            if (picked.Count == count) break;
            if (!picked.Contains(l)) picked.Add(l);
        }
        return picked.OrderBy(l => l.Pos).ToList();
    }

    /// <summary>Every alias (four letters or more) of a Character or Location whose first mention (<c>aliasFirstPos</c>) is after <paramref name="pos"/>.</summary>
    public static IReadOnlyList<string> UnrevealedNames(BookGraph graph, long pos)
    {
        var names = new List<string>();
        foreach (var n in graph.Nodes.Where(n => n.Labels.Contains("Character") || n.Labels.Contains("Location")))
        {
            if (n.Properties.GetValueOrDefault("aliasFirstPos") is not IReadOnlyDictionary<string, object?> first) continue;
            foreach (var (alias, at) in first)
                if (alias.Length >= 4 && at is long or double && Convert.ToInt64(at, System.Globalization.CultureInfo.InvariantCulture) > pos) names.Add(alias);
        }
        return names;
    }

    private static Regex NamePattern(string name) =>
        new(@"(?<![\p{L}\p{N}])" + Regex.Escape(name).Replace("'", "['’]").Replace("’", "['’]") + @"(?![\p{L}\p{N}])", RegexOptions.IgnoreCase);
}
