using Microsoft.Extensions.Configuration;
using TemporalGraph.Data;
using TemporalGraph.Query;

namespace TemporalGraph.Chat;

/// <summary>The <c>Chat</c> config section: <c>Chat:MaxRows</c> (80), <c>Chat:KnowledgePack</c> (true), <c>Chat:RequireToolFirst</c> (true).</summary>
/// <param name="SearchMaxPerTerm"><c>Chat:SearchMaxPerTerm</c> (40), the most facts one search_facts term contributes.</param>
/// <param name="VoiceExamples"><c>Chat:VoiceExamples</c> (4), the example lines sent per conversation; 0 sends none.</param>
/// <param name="CharacterGate"><c>Chat:CharacterGate</c> (true): everything the model is shown is limited to what the character
/// who speaks knows (<see cref="Chat.CharacterGate"/>); false: the reader's gate only, to compare the two.</param>
public sealed record ChatSettings(int MaxRows, bool KnowledgePack, bool RequireToolFirst, int SearchMaxPerTerm = ChatTools.DefaultSearchMaxPerTerm,
    int VoiceExamples = ChatSettings.DefaultVoiceExamples, bool CharacterGate = true)
{
    public const int DefaultVoiceExamples = 4;

    /// <summary><c>Chat:MaxRows</c> when not configured (80, was 50).</summary>
    public const int DefaultMaxRows = 80;

    /// <summary>
    /// <c>Chat:Llm</c>, a named profile (<c>Llm:Profiles:&lt;name&gt;</c>) for the characters' model, as <c>Indexing:Llm</c> is for
    /// the indexing stages. Empty, absent or a <c>&lt;placeholder&gt;</c> → null: the <c>Llm</c> section (gpt-4.1-mini today).
    /// </summary>
    public static string? LlmProfile(IConfiguration config) =>
        config["Chat:Llm"]?.Trim() is { Length: > 0 } p && !p.StartsWith('<') ? p : null;

    public static ChatSettings From(IConfiguration config) =>
        new(config.GetValue("Chat:MaxRows", DefaultMaxRows), config.GetValue("Chat:KnowledgePack", true), config.GetValue("Chat:RequireToolFirst", true),
            config.GetValue("Chat:SearchMaxPerTerm", ChatTools.DefaultSearchMaxPerTerm), config.GetValue("Chat:VoiceExamples", DefaultVoiceExamples),
            config.GetValue("Chat:CharacterGate", true));
}

/// <summary>
/// Everything a conversation needs before the first question, built the same way for the CLI and the API: the
/// character's display name at <c>$pos</c>, the system prompt, the tools, and the knowledge pack with
/// <c>$character</c> = the canonical name if it is one of the character's aliases, else its first alias.
/// </summary>
/// <param name="VoiceLines">The example lines in the prompt (none without a voice layer).</param>
/// <param name="NeverSay">The book's never-say words, checked against every answer (none without a voice guide).</param>
public sealed record ChatSetup(string Character, string DisplayName, string SystemPrompt, ChatTools Tools, ChatSettings Settings,
    string? PackText, IReadOnlyList<ChatTools.PackSection> PackSections, IReadOnlyList<VoiceLine>? VoiceLines = null, IReadOnlyList<string>? NeverSay = null)
{
    /// <summary>The pack's late rows that were dropped (spoilers off).</summary>
    public int PackLateRowsSuppressed => PackSections.Sum(s => s.LateRowsSuppressed);

    /// <param name="character">The canonical name (<see cref="ChatCharacters.Resolve"/>).</param>
    /// <param name="queries">Tool name → query text (<see cref="ChatTools.LoadQueries"/>).</param>
    /// <param name="onCall">Called after each tool call (the caller's <see cref="IChatLog.Tool"/>).</param>
    /// <param name="voice">The book's voice files (<see cref="BookVoice"/>); null: the one-line <c>voices</c> entry only.</param>
    public static ChatSetup Create(BookEntry book, BookGraph graph, IReadOnlyList<ChatCharacter> characters, string character,
        IReadOnlyDictionary<string, string> queries, IReadOnlyList<(string Name, long? EvidenceStart)> locations, int chapter, bool spoilers,
        IConfiguration config, Action<ToolCallRecord>? onCall = null, BookVoice? voice = null)
    {
        var settings = ChatSettings.From(config);
        var selected = characters.First(c => c.Name == character);
        var tools = new ChatTools(graph, queries, chapter, spoilers, settings.MaxRows, onCall, locations, settings.SearchMaxPerTerm, selected.Node?.Id, settings.CharacterGate);
        var displayName = selected.Node is { } node ? ChatTools.DisplayName(node, tools.Pos, spoilers, tools.Ambiguous).Name ?? character : character;
        var roster = BookVoice.RosterName(book, characters, character);
        IReadOnlyList<VoiceLine> lines = voice is not null && roster is not null
            ? voice.Examples(roster, chapter, tools.Pos, spoilers, settings.VoiceExamples, graph) : [];
        var oneLine = ChatCharacters.Voice(book, characters, character);
        var layer = voice is null ? null : ChatPrompt.Fit(new VoicePrompt(voice.Guide, voice.Card(book, characters, character), lines.Select(l => l.Text).ToList()), oneLine);
        if (layer is not null) lines = lines.Where(l => layer.Lines.Contains(l.Text)).ToList();   // what the budget kept
        var systemPrompt = ChatPrompt.Fill(displayName, book, chapter, ChatPrompt.Register(config, book.Id), knowledgePack: settings.KnowledgePack,
            voice: oneLine, layer: layer);

        string? packText = null;
        IReadOnlyList<ChatTools.PackSection> packSections = [];
        if (settings.KnowledgePack)
        {
            var aliases = selected.Aliases;
            var asked = aliases.Contains(character) || aliases.Count == 0 ? character : aliases[0];
            (packText, packSections) = tools.KnowledgePack(displayName, asked);
        }
        // The names the rule hides at this position are never-say words too (derived from the graph, not kept by hand).
        // With the character gate, so is every name the character has not met.
        var neverSay = (voice?.NeverSay ?? []).Concat(tools.HiddenNames()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return new ChatSetup(character, displayName, systemPrompt, tools, settings, packText, packSections, lines, neverSay);
    }

    /// <summary>The conversation over these tools and this pack.</summary>
    /// <param name="history">The turns of a conversation this one continues at another chapter or spoiler mode; null: a new one.</param>
    public ChatConversation Conversation(Microsoft.Extensions.AI.IChatClient client, IChatLog log, IReadOnlyList<(string Question, string Answer)>? history = null) =>
        new(client, Tools, SystemPrompt, log, PackText, Settings.RequireToolFirst, PackLateRowsSuppressed, NeverSay, history);
}
