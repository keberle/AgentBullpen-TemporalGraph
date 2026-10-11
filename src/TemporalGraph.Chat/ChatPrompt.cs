using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using TemporalGraph.Data;
using TemporalGraph.Query;

namespace TemporalGraph.Chat;

/// <summary>
/// The built-in system prompt. It holds no facts about the novel: everything comes from the tools. With the
/// knowledge pack, <see cref="PackRules"/> follow "Everything you know comes from the tools." Then three lines
/// of grounding rules (met vs. heard of, no added detail, first person) and the search_facts rule. <c>{name}</c> is the character's display name.
/// </summary>
public static class ChatPrompt
{
    public const string Template =
        """
        You are {name}, a character in "{title}" by {author}. The reader has read up to the end of chapter {chapter}.
        Speak in the first person, in {register}. Stay in character; never mention tools, chapters, readers, graphs or that you are an AI.
        Everything you know comes from the tools. Before answering a question about people, places, events or relationships, call the tools that could answer it. Use the names exactly as the tools return them.
        If the tools return nothing relevant, say plainly that you do not know or have not heard of it, in character. Never invent names, places, dates or events. Never use knowledge of the novel that the tools did not return.
        "Every character known so far" lists names you have heard of, not people you have met. Say you have met someone only if a "People you have met" row or a who_met result says so.
        Say only what the rows say. Do not add how a place looks, what a thing is for, or what a person is like unless a row states it. If you would add such a detail, leave it out.
        When a row names you, speak of yourself in the first person.
        If a question does not name a person or place you can look up — who did something, what happened, why, who is responsible — call search_facts with two to four key words from the question before answering, and then look up any person it names.
        """;

    /// <summary>The two rules added after "Everything you know comes from the tools." when the knowledge pack is sent.</summary>
    public const string PackRules =
        """
        The record of what you know is given to you at the start. For any person, place or thing not covered by that record, call the tools before answering.
        If neither the record nor the tools mention it, you have not heard of it: say so in character. Do not fill the gap with anything else you may know about the story.
        """;

    private const string ToolsSentence = "Everything you know comes from the tools.";

    /// <summary>The length rule. Always the prompt's last paragraph, after the voice layer.</summary>
    public const string LengthRule =
        "Answer the question that was asked and nothing more: two to four sentences, one short paragraph at most. Do not recount other things the tools returned unless they are needed to answer. If the reader asks for the whole story or for details, you may use up to three short paragraphs.";

    /// <summary>The heading of the example lines.</summary>
    public const string LinesHeading = "Lines you have said (they show HOW you speak; they are not things to repeat, and they are not new knowledge):";


    /// <summary>A book without <c>Chat:Register:&lt;bookId&gt;</c>.</summary>
    public const string DefaultRegister = "the register of the book";

    /// <summary><c>Chat:Register:&lt;bookId&gt;</c> (appsettings.json), else <see cref="DefaultRegister"/>.</summary>
    public static string Register(IConfiguration config, string bookId) =>
        config[$"Chat:Register:{bookId}"] is { Length: > 0 } r ? r : DefaultRegister;

    /// <param name="name">The character's display name at this chapter (<see cref="ChatTools.DisplayName"/>).</param>
    /// <param name="knowledgePack">True: the template with <see cref="PackRules"/> (each on its own line after the tools sentence).</param>
    /// <param name="voice">The character's one-line manner of speech (<c>books.json</c> <c>voices</c>), the fallback when there is no
    /// card: "Manner of speaking: {voice} Keep this manner in every answer."; null or empty: no such paragraph.</param>
    /// <param name="layer">The book's voice guide, the character's card and the example lines, each its own paragraph after the
    /// rules. A card replaces <paramref name="voice"/>. The length rule (<see cref="LengthRule"/>) is always last.</param>
    public static string Fill(string name, BookEntry book, int chapter, string register, bool knowledgePack = false, string? voice = null,
        VoicePrompt? layer = null)
    {
        var prompt = (knowledgePack ? Template.Replace(ToolsSentence + " ", ToolsSentence + "\n" + PackRules + "\n") : Template).Replace("\r\n", "\n")
            .Replace("{name}", name)
            .Replace("{title}", book.Title)
            .Replace("{author}", book.Author)
            .Replace("{chapter}", chapter.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Replace("{register}", register)
            .TrimEnd('\n');
        var paragraphs = new List<string> { prompt };
        paragraphs.AddRange(VoiceParagraphs(layer, voice));
        paragraphs.Add(LengthRule);
        return string.Join("\n\n", paragraphs);
    }

    /// <summary>The voice paragraphs <see cref="Fill"/> adds between the rules and the length rule.</summary>
    public static List<string> VoiceParagraphs(VoicePrompt? layer, string? voice)
    {
        var paragraphs = new List<string>();
        if (layer?.Guide is { Length: > 0 } guide) paragraphs.Add($"How people speak in this book: {guide.Trim()}");
        if (layer?.Card is { Length: > 0 } card) paragraphs.Add($"Your way of speaking: {card.Trim()}\nKeep this manner in every answer.");
        if (layer?.Lines is { Count: > 0 } lines) paragraphs.Add(LinesHeading + "\n" + string.Join("\n", lines.Select(l => $"\"{l.Trim()}\"")));
        if (layer?.Card is not { Length: > 0 } && !string.IsNullOrWhiteSpace(voice))
            paragraphs.Add($"Manner of speaking: {voice.Trim()} Keep this manner in every answer.");
        return paragraphs;
    }

    /// <summary>The characters the voice paragraphs add to the prompt.</summary>
    public static int VoiceLength(VoicePrompt? layer, string? voice) =>
        VoiceParagraphs(layer, voice).Sum(p => p.Length + 2);

    /// <summary>
    /// The most characters the voice layer may add (<see cref="VoiceLength"/>). 1,800 characters are about 680 tokens at
    /// the heaviest rate measured (Claude Haiku 5.5 on <i>Treasure Island</i>'s dialect lines, 0.378 tokens a character), which keeps
    /// every call under 700 added tokens.
    /// </summary>
    public const int MaxVoiceChars = 1800;

    /// <summary>The layer with its longest example lines dropped, one at a time, until it fits <paramref name="maxChars"/>.</summary>
    public static VoicePrompt Fit(VoicePrompt layer, string? voice, int maxChars = MaxVoiceChars)
    {
        var lines = layer.Lines.ToList();
        while (lines.Count > 0 && VoiceLength(layer with { Lines = lines }, voice) > maxChars)
            lines.Remove(lines.MaxBy(l => l.Length)!);
        return layer with { Lines = lines };
    }
}

/// <summary>The voice layer as sent: the book's guide, the character's card and the example lines' texts (all optional).</summary>
public sealed record VoicePrompt(string? Guide, string? Card, IReadOnlyList<string> Lines);

/// <summary>A <c>--script</c> file: one question per non-blank line; lines starting with <c>#</c> are comments.</summary>
public static class ChatScript
{
    public static IReadOnlyList<string> Parse(string text) =>
        text.Replace("\r\n", "\n").Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .ToList();
}

/// <summary>A character: the canonical name (characters table), the names it goes by (the node's <c>aliases</c>), its node and the
/// characters table's <c>initials</c> (empty in the promoted graphs so far).</summary>
public sealed record ChatCharacter(string Name, IReadOnlyList<string> Aliases, NodeValue? Node = null, string? Initials = null);

/// <summary>Resolves <c>--character</c> (a canonical name or any alias, case-insensitive) to the canonical name.</summary>
public static class ChatCharacters
{
    /// <summary>The characters table's canonical names in the API's order (first mention), with their node's aliases.</summary>
    /// <summary>
    /// The graph with each roster name (<c>books.json</c> <c>chat</c>) written onto its character's node as <c>rosterName</c>, so the
    /// display-name rule (<see cref="ChatTools.DisplayName"/>) shows the roster's spelling once it is revealed. A name resolves as the
    /// roster does: a Character's canonical name, else one of its aliases (ignoring case and the apostrophe's shape); the first name
    /// that resolves to a character wins; names that match nothing are left out. Without a roster the graph is returned as it is.
    /// </summary>
    public static BookGraph WithRosterNames(BookGraph graph, IReadOnlyList<string>? roster)
    {
        if (roster is not { Count: > 0 }) return graph;
        var characters = graph.Nodes.Where(n => n.Labels.Contains("Character")).ToList();
        IEnumerable<string> Aliases(NodeValue n) => n.Properties.GetValueOrDefault("aliases") is IReadOnlyList<object?> l ? l.OfType<string>() : [];
        var names = new Dictionary<long, string>();
        foreach (var r in roster)
        {
            var node = characters.FirstOrDefault(n => n.Properties.GetValueOrDefault("name") is string c && ChatTools.SameName(c, r))
                ?? characters.FirstOrDefault(n => Aliases(n).Any(a => ChatTools.SameName(a, r)));
            if (node is not null) names.TryAdd(node.Id, r.Trim());
        }
        if (names.Count == 0) return graph;
        var nodes = graph.Nodes.Select(n => names.TryGetValue(n.Id, out var r)
            ? n with { Properties = new Dictionary<string, object?>(n.Properties) { ["rosterName"] = r } }
            : n);
        return new BookGraph(nodes, graph.Relationships, graph.Chapters.ToDictionary(c => c.Key, c => c.Value), graph.Collisions, graph.Timeline);
    }

    public static IReadOnlyList<ChatCharacter> Load(string dbPath, BookGraph graph)
    {
        using var db = BookDatabase.OpenReadOnlyFile(dbPath);
        var nodes = graph.Nodes.ToDictionary(n => n.Id);
        return db.Characters.AsEnumerable()
            .Select(c => (c, node: c.NodeId is { } id && nodes.TryGetValue(id, out var n) ? n : null))
            .OrderBy(x => x.node?.Properties.GetValueOrDefault("evidenceStart") is long s ? s : long.MaxValue).ThenBy(x => x.c.Id)
            .Select(x => new ChatCharacter(x.c.Name, Aliases(x.node), x.node, x.c.Initials))
            .ToList();
    }

    /// <summary>The locations table's names with their node's first mention (for the at_location hint).</summary>
    public static IReadOnlyList<(string Name, long? EvidenceStart)> LoadLocations(string dbPath)
    {
        using var db = BookDatabase.OpenReadOnlyFile(dbPath);
        return db.Locations.Include(l => l.Node).AsEnumerable()
            .Select(l => (l.Name, (long?)l.Node?.EvidenceStart)).ToList();
    }

    private static IReadOnlyList<string> Aliases(NodeValue? node) =>
        node?.Properties.GetValueOrDefault("aliases") is IReadOnlyList<object?> list ? list.OfType<string>().ToList() : [];

    private static readonly HashSet<string> Titles = new(StringComparer.OrdinalIgnoreCase)
        { "mr", "mrs", "miss", "ms", "dr", "sir", "lady", "lord", "the", "a", "an", "rear-admiral", "monsieur", "madame", "m", "mme", "mlle", "mademoiselle",
          "captain", "capt", "cap’n", "cap'n", "squire", "master", "doctor", "cardinal" };

    // Name particles, skipped like titles ("M. de Tréville" → "T", "Anne of Austria" → "AA").
    private static readonly HashSet<string> Particles = new(StringComparer.OrdinalIgnoreCase) { "de", "of", "la", "le", "du", "des", "von", "van" };

    /// <summary>
    /// Initials for an avatar: the first letters of the first and last words of <paramref name="name"/> without titles
    /// and particles ("Mr. Sherlock Holmes" → "SH", "Dr. Watson" → "W", "the lady" → "L", "M. de Tréville" → "T"); a name of
    /// titles only → its first letter.
    /// </summary>
    public static string DeriveInitials(string name)
    {
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => w.Trim('.', ',', '\'', '"')).Where(w => w.Length > 0).ToList();
        var rest = words.Where(w => !Titles.Contains(w) && !Particles.Contains(w)).ToList();
        if (rest.Count == 0) rest = words.Where(w => w is not ("the" or "The" or "a" or "A" or "an" or "An")).ToList();
        if (rest.Count == 0) rest = words;
        if (rest.Count == 0) return "?";
        var first = char.ToUpperInvariant(rest[0][0]).ToString();
        return rest.Count == 1 ? first : first + char.ToUpperInvariant(rest[^1][0]);
    }

    /// <summary>
    /// A roster name as a file name: lower-cased, every run of characters other than letters and digits → one hyphen,
    /// trimmed of hyphens ("Dr. Watson" → <c>dr-watson</c>, "Sir Henry Baskerville" → <c>sir-henry-baskerville</c>). The avatar
    /// is <c>&lt;avatars folder&gt;/&lt;slug&gt;.webp</c>.
    /// </summary>
    public static string Slug(string name) =>
        System.Text.RegularExpressions.Regex.Replace(name.ToLowerInvariant(), @"[^\p{L}\p{N}]+", "-").Trim('-');

    /// <summary>
    /// The voice line for <paramref name="character"/> (a canonical name): the <c>voices</c> entry whose roster name resolves to it
    /// (<see cref="Resolve"/>); null when there is none.
    /// </summary>
    public static string? Voice(BookEntry book, IReadOnlyList<ChatCharacter> characters, string character) =>
        book.Voices?.FirstOrDefault(v => Resolve(characters, v.Key) == character).Value is { Length: > 0 } voice ? voice : null;

    /// <summary>The canonical name for <paramref name="name"/>, or null. A canonical name wins over another character's alias.</summary>
    public static string? Resolve(IReadOnlyList<ChatCharacter> characters, string name)
    {
        var n = name.Trim();
        // The apostrophe's shape aside (a roster's d'Artagnan is the text's D’Artagnan).
        return characters.FirstOrDefault(c => ChatTools.SameName(c.Name, n))?.Name
            ?? characters.FirstOrDefault(c => c.Aliases.Any(a => ChatTools.SameName(a, n)))?.Name;
    }
}
