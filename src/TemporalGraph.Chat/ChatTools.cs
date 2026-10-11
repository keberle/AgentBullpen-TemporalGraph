using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using TemporalGraph.Query;

namespace TemporalGraph.Chat;

/// <summary>What a tool's one model-supplied parameter names, if any.</summary>
/// <remarks><see cref="CharacterAndTarget"/> takes a <c>character</c> and an optional <c>target</c> (perceptions_of).</remarks>
public enum ToolParameter { None, Character, Entity, Location, Keywords, CharacterAndTarget }

/// <summary>One tool: a committed query file, its name and description for the model, and its parameter
/// (<c>character</c>, or <c>location</c> for at_location). <see cref="ToolParameter.Entity"/>: a <c>character</c> argument
/// matched against any node's aliases (facts_about has no label on purpose).</summary>
/// <param name="RecentFirst">The cap keeps the last <c>MaxRows</c> rows (the latest reveals); false: the first.</param>
/// <param name="SecondFile">A second query file the tool also runs (search_facts: search-events.cypher), loaded under
/// <c>Name + "#2"</c>.</param>
public sealed record ChatToolSpec(string Name, string File, string Description, ToolParameter Parameter, bool RecentFirst = true, string? SecondFile = null)
{
    public const string SecondSuffix = "#2";

    /// <summary>The query files the tool runs.</summary>
    public IEnumerable<string> Files => SecondFile is null ? [File] : [File, SecondFile];

    public bool TakesCharacter => Parameter is ToolParameter.Character or ToolParameter.Entity or ToolParameter.CharacterAndTarget;
}

/// <summary>One tool call as the transcript records it.</summary>
/// <param name="Rows">The rows the tool returned to the model (before the <c>MaxRows</c> cap, after late rows were dropped).</param>
/// <param name="ResolvedName">The alias an unknown name was retried as or matched by containment, else null.</param>
/// <param name="LateRowsSuppressed">Rows dropped because they name an entity not yet mentioned by any name at <c>$pos</c> (spoilers
/// off; "late rows").</param>
/// <param name="NodeIds">The node ids in the rows the model saw (the stripped id columns), for the graph highlight.</param>
/// <param name="EdgeIds">The edge ids in those rows (<c>edgeId</c> / <c>edgeIds</c> columns).</param>
/// <param name="UnknownRowsSuppressed">Rows dropped because the character who speaks does not know them (the character gate).</param>
public sealed record ToolCallRecord(int Turn, string Tool, IReadOnlyDictionary<string, string> Args, int Rows, double ElapsedMs, bool UnknownName, bool Truncated,
    string? ResolvedName = null, int LateRowsSuppressed = 0, IReadOnlyList<long>? NodeIds = null, IReadOnlyList<long>? EdgeIds = null, int UnknownRowsSuppressed = 0)
{
    public IReadOnlyList<long> NodeIdList => NodeIds ?? [];
    public IReadOnlyList<long> EdgeIdList => EdgeIds ?? [];
}

/// <summary>
/// The character's tools: the committed queries in <c>queries/&lt;bookId&gt;/</c>, run with <see cref="CypherEngine"/> on one
/// <see cref="BookGraph"/>. The model supplies only <c>character</c>; <c>$pos</c>, <c>$chapter</c> and <c>$spoilers</c> are
/// fixed for the command. Results are rows as text (columns joined with " · ", <see cref="CypherValues.Display"/>), capped at
/// <see cref="MaxRows"/>; empty → "(nothing known)". A name no <c>aliases</c> entry matches still runs (0 rows) and gets a
/// hint: the names from <c>characters_so_far</c>, or for <c>at_location</c> the location names mentioned so far.
/// <see cref="KnowledgePack"/> runs the selected character's own lookups by code.
/// <para>Entity names are shown as <see cref="DisplayName"/> (spoiler-safe), using the queries' <c>&lt;column&gt;Id</c> /
/// <c>&lt;column&gt;Ids</c> columns, which are not shown; <c>revealed</c> offsets are shown as <c>ch. N</c>; the cap keeps the
/// latest rows; an unknown name is retried without its article and with a trailing <c>s</c> added or removed.</para>
/// <para>With spoilers off, a late row (one naming an entity with no alias mentioned by <c>$pos</c>) is dropped from tool
/// results and the pack, and counted (<see cref="ToolCallRecord.LateRowsSuppressed"/>). An unknown name that the retry doesn't
/// resolve is matched as a whole word inside the aliases (<see cref="ContainingName"/>).</para>
/// </summary>
public sealed class ChatTools
{
    public const string Nothing = "(nothing known)";

    /// <summary>The tools, in the order they are given to the model. Not tools: unmentioned-optional, true-in-story-by.</summary>
    public static readonly IReadOnlyList<ChatToolSpec> Specs =
    [
        new("facts_about", "facts-about.cypher", "What is known about a person, place or thing, with everything each fact is about.", ToolParameter.Entity),
        new("who_met", "who-met.cypher", "Whom a character has met.", ToolParameter.Character),
        new("where_placed", "where.cypher", "Where characters have been placed (character, location, story time).", ToolParameter.None),
        new("events_for", "events-for-character.cypher", "Events a character took part in, in the order they were learned.", ToolParameter.Character),
        new("relations", "relations.cypher", "How a character is related to others (kind of relation).", ToolParameter.Character),
        new("characters_so_far", "characters-so-far.cypher", "Every character known so far.", ToolParameter.None, RecentFirst: false),
        new("most_discussed", "most-discussed.cypher", "Who the known facts talk about most.", ToolParameter.None, RecentFirst: false),
        new("at_location", "at-location.cypher", "Who has been at a place, and what happened there.", ToolParameter.Location),
        new("search_facts", "search-facts.cypher", SearchDescription, ToolParameter.Keywords, RecentFirst: false, SecondFile: "search-events.cypher"),
    ];

    /// <summary>
    /// The tools a book has when its folder holds their query files (a book on the full schema; the others don't):
    /// what a character came to know and how, how it takes facts and people (PERCEIVES), and the events that were only mentioned.
    /// </summary>
    public static readonly IReadOnlyList<ChatToolSpec> ExtraSpecs =
    [
        new("facts_discovered_by", "facts-discovered-by.cypher", "What a character has come to know, through which event and how: witnessed, told, overheard, inferred, read or recalled.", ToolParameter.Character),
        new("perceptions_of", "perceptions-of.cypher", "How a character takes facts and people: what it believes, doubts or disbelieves, whom it trusts, distrusts or suspects, what threatens or reassures it, what it fears, hopes for or is curious about. Optionally only about one person.", ToolParameter.CharacterAndTarget),
        new("referenced_events", "referenced-events.cypher", "Events that were only mentioned by someone, with their own earlier time in the story.", ToolParameter.None, RecentFirst: false),
    ];

    /// <summary>Every tool there can be: <see cref="Specs"/> and <see cref="ExtraSpecs"/>.</summary>
    public static IEnumerable<ChatToolSpec> AllSpecs => Specs.Concat(ExtraSpecs);

    /// <summary>The knowledge pack's sections: heading, tool (run for the selected character unless it takes no parameter).</summary>
    public static readonly IReadOnlyList<(string Heading, string Tool)> PackSections =
    [
        ("About yourself", "facts_about"),
        ("People you have met", "who_met"),
        ("Your relations", "relations"),
        ("Events you took part in", "events_for"),
        ("Where people have been placed", "where_placed"),
    ];

    /// <summary>An empty knowledge-pack section.</summary>
    public const string PackNothing = "(nothing)";

    /// <summary>The pack of a character who knows nothing at this position (no fact, no event, no one).</summary>
    public const string PackKnowsNothing = "You know nothing yet: nothing has happened to you and no one has told you anything.";

    /// <summary>The knowledge pack's second line.</summary>
    public const string PackStampLine =
        "Rows are stamped with the chapter in which you learned them. Phrases like \"three months ago\" or \"earlier that evening\" are relative to that chapter, not to now.";

    /// <summary>Columns holding a text offset, shown as <c>ch. N</c>.</summary>
    public static readonly IReadOnlySet<string> RevealedColumns = new HashSet<string>(StringComparer.Ordinal) { "revealed", "revealedPos" };

    private readonly BookGraph _graph;
    private readonly Dictionary<long, NodeValue> _nodes;
    private readonly Dictionary<long, RelationshipValue> _edges;
    // A book on the full schema has a timeline; its rows are post-filtered with the spoiler rule (SpoilerGate).
    private readonly bool _gated;
    private readonly string? _story;
    // Node id → the nodes it is one person with (itself included), through IDENTICAL_TO edges visible at this position.
    private readonly Dictionary<long, List<long>> _union = [];
    private readonly IReadOnlyDictionary<string, string> _queries;
    private readonly HashSet<string> _characterAliases;
    private readonly HashSet<string> _entityAliases;
    private readonly HashSet<string> _locationAliases;
    private readonly IReadOnlyList<long> _locationIds;
    private readonly Action<ToolCallRecord>? _onCall;
    private readonly List<ToolCallRecord> _calls = [];

    public long Pos { get; }
    public int Chapter { get; }
    public bool Spoilers { get; }
    public int MaxRows { get; }

    /// <summary>The most facts one search_facts term contributes, the most recent first (<c>Chat:SearchMaxPerTerm</c>).</summary>
    public int SearchMaxPerTerm { get; }

    /// <summary>Aliases of more than one entity, skipped for display names unless they are the only revealed alias.</summary>
    public IReadOnlySet<string> Ambiguous { get; }

    /// <summary>What the character who speaks knows; every tool result and the pack pass through it. Null: reader gate only
    /// (no character given, <c>Chat:CharacterGate</c> false, or a book indexed before the full schema).</summary>
    public CharacterGate? Gate { get; }

    /// <summary>The turn the next calls belong to (1-based; set by the caller before each question).</summary>
    public int Turn { get; set; } = 1;

    /// <summary>Every call so far, in order.</summary>
    public IReadOnlyList<ToolCallRecord> Calls => _calls;

    /// <param name="queries">Tool name → query text (the file, its <c>//</c> header stripped).</param>
    /// <param name="onCall">Called after each tool call (chat.jsonl line).</param>
    /// <param name="locations">Location names with their first mention (the locations table); default: the graph's Location nodes.
    /// The at_location hint lists those mentioned before <c>$pos</c>, as display names.</param>
    public ChatTools(BookGraph graph, IReadOnlyDictionary<string, string> queries, int chapter, bool spoilers, int maxRows, Action<ToolCallRecord>? onCall = null,
        IReadOnlyList<(string Name, long? EvidenceStart)>? locations = null, int searchMaxPerTerm = DefaultSearchMaxPerTerm, long? characterId = null, bool characterGate = true)
    {
        if (!graph.Chapters.TryGetValue(chapter, out var ch)) throw new ArgumentException($"No chapter {chapter} in the chapters table.", nameof(chapter));
        _graph = graph;
        _nodes = graph.Nodes.ToDictionary(n => n.Id);
        _edges = graph.Relationships.ToDictionary(r => r.Id);
        _gated = graph.Timeline.Count > 0;
        _queries = queries;
        _onCall = onCall;
        Pos = ch.CharEnd;
        Chapter = chapter;
        Spoilers = spoilers;
        MaxRows = maxRows;
        _story = spoilers ? graph.StoryAt(Pos) : null;
        if (_gated)
        {
            // An identity is just another edge under the rule: hidden, the two nodes are unrelated; visible, they are one character.
            foreach (var edge in graph.Relationships.Where(r => r.Type == "IDENTICAL_TO" && Visible(r.Properties) && NodeVisible(r.StartId) && NodeVisible(r.EndId)))
            {
                var a = _union.TryGetValue(edge.StartId, out var x) ? x : [edge.StartId];
                var b = _union.TryGetValue(edge.EndId, out var y) ? y : [edge.EndId];
                if (ReferenceEquals(a, b)) continue;
                var joined = a.Concat(b).Distinct().ToList();
                foreach (var id in joined) _union[id] = joined;
            }
        }
        _characterAliases = Aliases(graph, "Character");
        _entityAliases = Aliases(graph, null);
        SearchMaxPerTerm = searchMaxPerTerm;
        Ambiguous = AmbiguousAliases(graph);
        _locationAliases = Aliases(graph, "Location");
        var locationNodes = graph.Nodes.Where(n => n.Labels.Contains("Location") && n.Properties.GetValueOrDefault("name") is string).ToList();
        locations ??= locationNodes.Select(n => ((string)n.Properties["name"]!, n.Properties.GetValueOrDefault("evidenceStart") as long?)).ToList();
        // The locations table's names are mapped to their node by canonical name, for the display name.
        var byName = locationNodes.GroupBy(n => (string)n.Properties["name"]!).ToDictionary(g => g.Key, g => g.First().Id);
        if (_gated && characterGate && characterId is { } who && _nodes.ContainsKey(who))
            Gate = new CharacterGate(graph, who, KnownFacts(who), Union, Visible, Pos, spoilers);
        _locationIds = locations.Where(l => l.EvidenceStart is { } s && s < Pos && byName.ContainsKey(l.Name)).OrderBy(l => l.EvidenceStart)
            .Select(l => byName[l.Name]).Where(id => Gate?.Knows(id) ?? true).ToList();
    }

    /// <summary>The spoiler rule on an element's properties, at this position and in this mode.</summary>
    public bool Visible(IReadOnlyDictionary<string, object?> properties) => SpoilerGate.Visible(properties, Pos, Spoilers, _story);

    private bool NodeVisible(long id) => !_nodes.TryGetValue(id, out var n) || Visible(n.Properties);
    private bool EdgeVisible(long id) => !_edges.TryGetValue(id, out var e) || Visible(e.Properties);

    /// <summary>The nodes that are one character with <paramref name="id"/> at this position (itself alone unless an identity is visible).</summary>
    public IReadOnlyList<long> Union(long id) => _union.TryGetValue(id, out var members) ? members : [id];

    /// <summary>
    /// The character gate: the Facts a character knows at this position: those it has a visible PERCEIVES edge to (the Fact
    /// visible too), except the ones it <c>disbelieves</c>. A doubted fact is still a known fact. The identity union counts as one character.
    /// </summary>
    public IReadOnlySet<long> KnownFacts(long characterId)
    {
        var known = new HashSet<long>();
        var disbelieved = new HashSet<long>();
        foreach (var member in Union(characterId))
            foreach (var r in _graph.Outgoing(member).Where(r => r.Type == "PERCEIVES" && Visible(r.Properties)))
            {
                if (!_nodes.TryGetValue(r.EndId, out var target) || !target.Labels.Contains("Fact") || !Visible(target.Properties)) continue;
                if (r.Properties.GetValueOrDefault("kind") as string == "disbelieves") disbelieved.Add(r.EndId);
                else known.Add(r.EndId);
            }
        known.ExceptWith(disbelieved);
        return known;
    }

    /// <summary>
    /// The names that must not be said at this position: every alias (four letters or more) of a Character or Location the
    /// rule hides, and with spoilers off every alias first mentioned later, unless a visible node already goes by it. A character in a
    /// visible identity union has all its names free. Empty for a book indexed before the full schema.
    /// </summary>
    /// <remarks>With the character gate, a name the character has not met is hidden too (every name of someone it has never
    /// heard of, and a later name of someone it knows), and so is each word of a hidden person's name that is a name and nothing else
    /// ("Beryl" of "Beryl Garcia"): a word that no free name has and that the book's facts and events never write in lower case.
    /// A name or a word that stands in a fact or an event the character knows is never hidden from it (Watson hears "Beryl" in
    /// chapter 7; Holmes, in London, does not).</remarks>
    public IReadOnlyList<string> HiddenNames()
    {
        if (!_gated) return [];
        var hidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var free = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var people = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // hidden names of characters
        var sameNode = new HashSet<string>(StringComparer.OrdinalIgnoreCase);  // hidden names made only of words the same node's free names have
        foreach (var n in _graph.Nodes.Where(n => n.Labels.Contains("Character") || n.Labels.Contains("Location")))
        {
            var visible = Visible(n.Properties);
            var first = n.Properties.GetValueOrDefault("aliasFirstPos") as IReadOnlyDictionary<string, object?>;
            var met = Gate?.Met(n);
            var mine = new List<string>();
            var kept = new List<string>();
            foreach (var alias in (n.Properties.GetValueOrDefault("aliases") as IReadOnlyList<object?> ?? []).OfType<string>())
            {
                var late = !Spoilers && first?.GetValueOrDefault(alias) switch { long l => l > Pos, double d => d > Pos, _ => false };
                // A name without a first position (a graph built without alias first positions) can't be placed: the reader's rule alone decides.
                var unmet = met is not null && first?.ContainsKey(alias) == true && !met.Contains(alias);
                // A name the character has met is free even when the graph places the node's first mention later than what it was told.
                if (met?.Contains(alias) == true || (visible && !late && !unmet))
                {
                    free.Add(alias);
                    kept.Add(alias);
                }
                else if (alias.Length >= 4 && char.IsUpper(alias[0]))   // names only: a noun phrase ("the butler") is no secret
                {
                    hidden.Add(alias);
                    mine.Add(alias);
                    if (n.Labels.Contains("Character")) people.Add(alias);
                }
            }
            // "Dr. John Watson" is no news to someone who has "Dr. Watson" and "John Watson".
            var words = kept.SelectMany(AliasWords).ToHashSet();
            foreach (var alias in mine.Where(a => kept.Count > 0 && AliasWords(a).All(w => words.Contains(w) || StopWords.Contains(w)))) sameNode.Add(alias);
        }
        hidden.ExceptWith(free);
        hidden.ExceptWith(sameNode);
        // A hidden name that is only a word of a free name ("Baskerville" in "Sir Henry Baskerville") can't be checked by matching words.
        // Nor can the plural of one ("the Baskervilles").
        var names = hidden.Where(h => !free.Any(f => WholeWord(h).IsMatch(f) || (h.Length > 4 && h.EndsWith('s') && WholeWord(h[..^1]).IsMatch(f)))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (Gate is not null)
        {
            // A name that stands in a fact or an event the character knows, or in the book's own words behind one (the evidence
            // quote), is not hidden from it: the graph's first mention of a name can come later than the book's.
            names.RemoveWhere(n => Gate.Heard(n) || Gate.Wrote(n));
            var freeWords = free.SelectMany(AliasWords).ToHashSet();
            var lower = LowerCaseWords();
            // Only a name made of names ("Beryl Garcia", "Colonel Upwood"; not "German waiter"), and never its title.
            foreach (var word in names.Where(people.Contains).Select(n => Regex.Split(n, @"[^\p{L}]+").Where(w => w.Length > 0).ToList())
                         .Where(w => w.Count >= 2 && w.All(x => char.IsUpper(x[0]))).SelectMany(w => w).Where(w => w.Length >= 4).ToList())
                if (!freeWords.Contains(word.ToLowerInvariant()) && !StopWords.Contains(word) && !Titles.Contains(word) && !lower.Contains(word.ToLowerInvariant())
                    && !Gate.Heard(word) && !Gate.Wrote(word)) names.Add(word);
        }
        return names.OrderBy(h => h, StringComparer.Ordinal).ToList();
    }

    /// <summary>Words of a name that are a rank or a form of address, never hidden on their own.</summary>
    private static readonly HashSet<string> Titles = new(StringComparer.OrdinalIgnoreCase)
    {
        "colonel", "admiral", "rear", "captain", "major", "general", "lieutenant", "sergeant", "inspector", "doctor", "professor", "reverend", "father", "mother",
        "brother", "sister", "uncle", "aunt", "king", "queen", "prince", "princess", "duke", "duchess", "count", "countess", "baron", "cardinal", "bishop", "squire",
        "master", "mistress", "monsieur", "madame", "mademoiselle", "mlle", "saint", "lord", "lady", "miss", "young", "little", "great",
    };

    private HashSet<string>? _lowerCaseWords;

    /// <summary>Every word the book's facts and events write in lower case (so a capitalised word outside it is a name).</summary>
    private HashSet<string> LowerCaseWords() => _lowerCaseWords ??= _graph.Nodes
        .Select(n => (n.Properties.GetValueOrDefault("text") ?? n.Properties.GetValueOrDefault("summary")) as string).OfType<string>()
        .SelectMany(t => Regex.Matches(t, @"(?<![\p{L}])\p{Ll}[\p{L}]*").Select(m => m.Value)).ToHashSet(StringComparer.Ordinal);

    private static HashSet<string> Aliases(BookGraph graph, string? label) =>
        graph.Nodes.Where(n => label is null || n.Labels.Contains(label))
            .SelectMany(n => n.Properties.TryGetValue("aliases", out var a) && a is IReadOnlyList<object?> list ? list.OfType<string>() : [])
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>The query file names that <paramref name="folder"/> is missing (empty when every tool has its file).</summary>
    public static IReadOnlyList<string> MissingFiles(string folder) =>
        Specs.SelectMany(s => s.Files).Where(f => !System.IO.File.Exists(Path.Combine(folder, f))).ToList();

    /// <summary>Tool name → query text from <paramref name="folder"/> (queries/&lt;bookId&gt;/).</summary>
    public static IReadOnlyDictionary<string, string> LoadQueries(string folder)
    {
        var queries = Specs.ToDictionary(s => s.Name, s => StripHeader(System.IO.File.ReadAllText(Path.Combine(folder, s.File))));
        // The extra tools, for a book whose folder has their files.
        foreach (var s in ExtraSpecs.Where(s => System.IO.File.Exists(Path.Combine(folder, s.File))))
            queries[s.Name] = StripHeader(System.IO.File.ReadAllText(Path.Combine(folder, s.File)));
        foreach (var s in Specs.Where(s => s.SecondFile is not null))
            queries[s.Name + ChatToolSpec.SecondSuffix] = StripHeader(System.IO.File.ReadAllText(Path.Combine(folder, s.SecondFile!)));
        return queries;
    }

    /// <summary>The query text without its leading <c>//</c> comment lines.</summary>
    public static string StripHeader(string text) =>
        string.Join("\n", text.Replace("\r\n", "\n").Split('\n').SkipWhile(l => l.TrimStart().StartsWith("//") || l.Trim().Length == 0)).Trim();

    /// <summary>The tools as <see cref="AIFunction"/>s (AIFunctionFactory), for the agent.</summary>
    public IList<AITool> AsAITools() => [.. BaseTools(), .. ExtraTools()];

    /// <summary>The extra tools this book has a query for (<see cref="ExtraSpecs"/>).</summary>
    private IEnumerable<AITool> ExtraTools()
    {
        if (_queries.ContainsKey("facts_discovered_by"))
            yield return Bind("facts_discovered_by", ([Description("The character's name, as the tools return it.")] string character) => Run("facts_discovered_by", character));
        if (_queries.ContainsKey("perceptions_of"))
            yield return Bind("perceptions_of", ([Description("The character's name, as the tools return it.")] string character,
                [Description("Optional: the name of one person, to see only what the character makes of them and of facts about them.")] string? target = null) => Run("perceptions_of", character, target));
        if (_queries.ContainsKey("referenced_events"))
            yield return Bind("referenced_events", () => Run("referenced_events", null));
    }

    private IList<AITool> BaseTools() =>
    [
        Bind("facts_about", ([Description("The name of the person, place or thing, as the tools return it.")] string character) => Run("facts_about", character)),
        Bind("who_met", ([Description("The character's name, as the tools return it.")] string character) => Run("who_met", character)),
        Bind("where_placed", () => Run("where_placed", null)),
        Bind("events_for", ([Description("The character's name, as the tools return it.")] string character) => Run("events_for", character)),
        Bind("relations", ([Description("The character's name, as the tools return it.")] string character) => Run("relations", character)),
        Bind("characters_so_far", () => Run("characters_so_far", null)),
        Bind("most_discussed", () => Run("most_discussed", null)),
        Bind("at_location", ([Description("The place's name, as the tools return it.")] string location) => Run("at_location", location)),
        Bind("search_facts", ([Description("Two to four key words from the question, e.g. \"murder hound\".")] string keywords) => Run("search_facts", keywords)),
    ];

    private static AIFunction Bind(string name, Delegate method) =>
        AIFunctionFactory.Create(method, name, AllSpecs.Single(s => s.Name == name).Description);

    /// <summary>Runs one tool and returns the text the model sees. Records the call.</summary>
    /// <param name="name">The tool's argument: <c>character</c>, or <c>location</c> for at_location; null for tools without one.</param>
    /// <param name="target">perceptions_of only: the name of one person; null or empty for all.</param>
    public string Run(string tool, string? name, string? target = null)
    {
        var spec = AllSpecs.Single(s => s.Name == tool);
        if (spec.Parameter == ToolParameter.Keywords) return Search(spec, name ?? "");
        target = string.IsNullOrWhiteSpace(target) ? null : target.Trim();
        var watch = Stopwatch.StartNew();
        var result = Execute(spec, name, target);

        var known = spec.Parameter switch
        {
            ToolParameter.Character or ToolParameter.CharacterAndTarget => _characterAliases,
            ToolParameter.Entity => _entityAliases,
            ToolParameter.Location => _locationAliases,
            _ => null,
        };
        var unknown = known is not null && !known.Contains(name ?? "");

        // facts_about / at_location retry an unknown name in its other forms before giving the hint; then
        // as a whole word inside the aliases (one entity: use it; several: the hint lists them).
        string? resolved = null;
        IReadOnlyList<long> candidates = [];
        if (unknown && result.Rows.Count == 0 && spec.Parameter is ToolParameter.Entity or ToolParameter.Location)
        {
            var label = spec.Parameter == ToolParameter.Location ? "Location" : null;
            resolved = RetryName(name ?? "", label);
            if (resolved is null) (resolved, candidates) = ContainingName(name ?? "", label);
            if (resolved is not null)
            {
                unknown = false;
                result = Execute(spec, resolved, target);
            }
        }
        // The character gate: what a character has discovered is listed only while it still knows it (a visible
        // PERCEIVES edge that is not `disbelieves`).
        if (_gated && tool == "facts_discovered_by" && result.Columns.ToList().IndexOf("factId") is var factColumn and >= 0)
        {
            var knownFacts = Named(name ?? "", "Character").SelectMany(KnownFacts).ToHashSet();
            result = new QueryResult(result.Columns, result.Rows.Where(r => r[factColumn] is long id && knownFacts.Contains(id)).ToList());
        }
        // Someone the character who speaks has never heard of has no rows, and gets the unknown-name hint.
        if (Gate is not null && name is not null && spec.Parameter is not ToolParameter.None && Named(resolved ?? name, null).ToList() is { Count: > 0 } asked && !asked.Any(Gate.Knows))
        {
            result = new QueryResult(result.Columns, []);
            unknown = true;
            candidates = [];
        }
        if (Gate is not null && tool == "most_discussed") result = MostDiscussed(result);
        var elapsed = watch.Elapsed.TotalMilliseconds;

        var rendered = RenderWithIds(result, Nothing, spec.RecentFirst);
        var (text, shown, late) = (rendered.Text, rendered.Rows, rendered.LateRowsSuppressed);
        var truncated = shown > MaxRows;
        if (unknown)
            text += "\n" + (candidates.Count > 1 ? ContainingHint(name ?? "", candidates)
                : spec.Parameter == ToolParameter.Location ? LocationHint(name ?? "") : Hint(name ?? ""));

        var span = Activity.Current;
        span?.SetTag("tool.name", tool);
        span?.SetTag("query.rows", shown);
        if (late > 0) span?.SetTag("chat.late_rows_suppressed", late);

        var args = name is null || spec.Parameter == ToolParameter.None ? new Dictionary<string, string>()
            : new Dictionary<string, string> { [spec.Parameter == ToolParameter.Location ? "location" : "character"] = name };
        if (target is not null) args["target"] = target;
        if (rendered.Unknown > 0) span?.SetTag("chat.unknown_rows_suppressed", rendered.Unknown);
        var record = new ToolCallRecord(Turn, tool, args, shown, Math.Round(elapsed, 2), unknown, truncated, resolved, late, rendered.NodeIds, rendered.EdgeIds, rendered.Unknown);
        _calls.Add(record);
        _onCall?.Invoke(record);
        return text;
    }

    /// <summary>most_discussed counted over the facts the character knows (the query counts every visible fact).</summary>
    private QueryResult MostDiscussed(QueryResult result)
    {
        var columns = result.Columns.ToList();
        int iName = columns.IndexOf("character"), iFacts = columns.IndexOf("facts"), iId = columns.IndexOf("characterId");
        if (iName < 0 || iFacts < 0 || iId < 0) return result;
        return new QueryResult(result.Columns, Gate!.MostDiscussed().Select(c =>
        {
            var row = new object?[columns.Count];
            (row[iName], row[iFacts], row[iId]) = (_nodes[c.Character].Properties.GetValueOrDefault("name"), c.Facts, c.Character);
            return (IReadOnlyList<object?>)row;
        }).ToList());
    }

    private Dictionary<string, object?> Parameters(ChatToolSpec? spec, string? name, string? target = null)
    {
        var p = new Dictionary<string, object?> { ["pos"] = Pos, ["chapter"] = (long)Chapter, ["spoilers"] = Spoilers };
        if (name is not null && spec is { Parameter: not ToolParameter.None })
            p[spec.Parameter == ToolParameter.Location ? "location" : "character"] = name;
        if (spec is { Parameter: ToolParameter.CharacterAndTarget }) p["target"] = target;
        return p;
    }

    /// <summary>The nodes that go by <paramref name="name"/> (an alias), with this label when given.</summary>
    private IEnumerable<long> Named(string name, string? label) =>
        _graph.Nodes.Where(n => (label is null || n.Labels.Contains(label)) && (n.Properties.GetValueOrDefault("aliases") as IReadOnlyList<object?> ?? []).OfType<string>().Contains(name))
            .Select(n => n.Id);

    /// <summary>
    /// Runs the tool's query. When the asked name is a character that a visible identity joins with others, the query
    /// also runs for each of them (by its canonical name) and the rows are put together, each once, in the order learned: the union
    /// is one character. With the identity hidden nothing joins them.
    /// </summary>
    private QueryResult Execute(ChatToolSpec spec, string? name, string? target)
    {
        var result = CypherEngine.Execute(_graph, _queries[spec.Name], Parameters(spec, name, target));
        if (name is null || !spec.TakesCharacter || _union.Count == 0) return result;
        var asked = Named(name, null).ToList();
        var others = asked.SelectMany(Union).Distinct().Except(asked).Select(id => _nodes[id].Properties.GetValueOrDefault("name") as string).OfType<string>().Distinct().ToList();
        if (others.Count == 0) return result;
        var rows = result.Rows.ToList();
        var seen = new HashSet<IReadOnlyList<object?>>(rows, CypherValues.RowComparer.Instance);
        foreach (var other in others)
            foreach (var row in CypherEngine.Execute(_graph, _queries[spec.Name], Parameters(spec, other, target)).Rows)
                if (seen.Add(row)) rows.Add(row);
        var order = result.Columns.ToList().FindIndex(RevealedColumns.Contains);
        return new QueryResult(result.Columns, order < 0 ? rows : rows.OrderBy(r => r[order] is long l ? l : long.MaxValue).ToList());
    }

    /// <summary>"No character goes by "X". Known names: A, B, …" — the names from characters_so_far at this position (display names).</summary>
    private string Hint(string name)
    {
        var (text, _) = Render(CypherEngine.Execute(_graph, _queries["characters_so_far"], Parameters(null, null)), "", recentFirst: false, cap: false);
        return $"No character goes by \"{name}\". Known names: {string.Join(", ", text.Split('\n', StringSplitOptions.RemoveEmptyEntries))}.";
    }

    /// <summary>"No location goes by "X". Known places: A, B, …" — locations (locations table) mentioned before $pos, as display names.</summary>
    private string LocationHint(string name) =>
        $"No location goes by \"{name}\". Known places: {string.Join(", ", _locationIds.Select(id => DisplayNameOf(id).Name))}.";

    /// <summary>
    /// An unknown name tried without a leading article and with a trailing <c>s</c> added or removed, against every
    /// node's aliases (also compared without their article), case-insensitive. Exactly one matching node → the alias that
    /// matched (the query parameter); none or several → null.
    /// </summary>
    /// <param name="label">Only nodes with this label (at_location: Location); null: any node.</param>
    public string? RetryName(string name, string? label)
    {
        var forms = NameForms(name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var matches = _graph.Nodes.Where(n => (label is null || n.Labels.Contains(label)) && (Gate?.Knows(n.Id) ?? true))
            .Select(n => (n.Properties.GetValueOrDefault("aliases") as IReadOnlyList<object?> ?? []).OfType<string>()
                .FirstOrDefault(a => forms.Contains(a) || forms.Contains(StripArticle(a))))
            .Where(a => a is not null)
            .ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    private static readonly string[] Articles = ["the ", "a ", "an "];

    private static string StripArticle(string name)
    {
        var n = name.Trim();
        var article = Articles.FirstOrDefault(a => n.Length > a.Length && n.StartsWith(a, StringComparison.OrdinalIgnoreCase));
        return article is null ? n : n[article.Length..].Trim();
    }

    /// <summary>The name and the name without its article, each also with a trailing "s" added or removed.</summary>
    private static IEnumerable<string> NameForms(string name)
    {
        foreach (var n in new[] { name.Trim(), StripArticle(name) }.Distinct())
        {
            yield return n;
            yield return n.EndsWith('s') ? n[..^1] : n + "s";
        }
    }

    /// <summary>
    /// The asked name (and its forms without the article, with a trailing <c>s</c> added or removed) as a whole word inside
    /// any alias, case-insensitive. With spoilers off only aliases mentioned by <see cref="Pos"/> count (a node without
    /// <c>aliasFirstPos</c>: all its aliases), so a later name is never reached early. Exactly one matching node → its longest
    /// matching alias (the query parameter) and that node; several → null and the nodes (for the hint); none → null, empty.
    /// </summary>
    /// <param name="label">Only nodes with this label (at_location: Location); null: any node.</param>
    public (string? Alias, IReadOnlyList<long> Nodes) ContainingName(string name, string? label)
    {
        var patterns = NameForms(name).Where(f => f.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(f => new Regex(@"(?<!\w)" + Regex.Escape(f) + @"(?!\w)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            .ToList();
        if (patterns.Count == 0) return (null, []);
        var matches = _graph.Nodes.Where(n => (label is null || n.Labels.Contains(label)) && (Gate?.Knows(n.Id) ?? true))
            .Select(n => (n.Id, Alias: RevealedAliases(n).Where(a => patterns.Any(p => p.IsMatch(a)))
                .OrderByDescending(a => a.Length).ThenBy(a => a, StringComparer.Ordinal).FirstOrDefault()))
            .Where(x => x.Alias is not null)
            .ToList();
        return matches.Count == 1 ? (matches[0].Alias, [matches[0].Id]) : (null, matches.Select(m => m.Id).ToList());
    }

    /// <summary>The node's aliases; with spoilers off and <c>aliasFirstPos</c> present, only those mentioned by <see cref="Pos"/>.</summary>
    private IEnumerable<string> RevealedAliases(NodeValue node)
    {
        var aliases = (node.Properties.GetValueOrDefault("aliases") as IReadOnlyList<object?> ?? []).OfType<string>();
        if (Spoilers || node.Properties.GetValueOrDefault("aliasFirstPos") is not IReadOnlyDictionary<string, object?> { Count: > 0 } first) return aliases;
        return aliases.Where(a => first.GetValueOrDefault(a) switch { long l => l <= Pos, double d => d <= Pos, _ => false });
    }

    /// <summary>"No character goes by "X". Names that contain it: A, B." — the display names of several containment matches.</summary>
    private string ContainingHint(string name, IReadOnlyList<long> nodes) =>
        $"No character goes by \"{name}\". Names that contain it: {string.Join(", ", nodes.Select(id => DisplayNameOf(id).Name).OfType<string>().Distinct())}.";

    /// <summary>The search_facts description to the model.</summary>
    public const string SearchDescription =
        "Search what is known by topic: pass 2–4 key words (people, places, things, deeds). Use this for questions that do not name a person or place — who did something, what happened somewhere, why something occurred.";

    /// <summary><c>Chat:SearchMaxPerTerm</c> when not configured.</summary>
    public const int DefaultSearchMaxPerTerm = 40;

    /// <summary>A search term as a whole word, case-insensitive: no letter or digit right before or after it.</summary>
    public static Regex WholeWord(string term) =>
        new(@"(?<![\p{L}\p{N}])" + Regex.Escape(term) + @"(?![\p{L}\p{N}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The search_facts reply when no content word is left.</summary>
    public const string SearchHint = "No content words to search for: give 2–4 content words (people, places, things, deeds).";

    /// <summary>Words search_facts ignores (with any word shorter than 3 letters): function words, question words, titles.</summary>
    public static readonly IReadOnlySet<string> StopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "but", "for", "nor", "not", "yet", "with", "from", "into", "onto", "upon", "over", "under", "about", "after", "before",
        "this", "that", "these", "those", "there", "here", "then", "than", "who", "whom", "whose", "what", "which", "when", "where", "why", "how",
        "was", "were", "are", "is", "been", "being", "have", "has", "had", "did", "does", "doing", "done", "will", "would", "shall", "should",
        "can", "could", "may", "might", "must", "all", "any", "some", "his", "her", "hers", "him", "its", "our", "their", "them", "they", "she",
        "you", "your", "one", "out", "off", "very", "just", "also", "only", "really", "know", "known", "tell", "happened", "happen",
        "mr", "mrs", "miss", "sir", "dr", "lady", "lord",
    };

    /// <summary>
    /// The content words of a search_facts argument: split on whitespace, commas and semicolons, quotes and punctuation trimmed,
    /// lower-cased, stop words and words shorter than 3 letters dropped, duplicates removed (first order kept).
    /// </summary>
    public static IReadOnlyList<string> SearchTerms(string keywords) =>
        keywords.Split([' ', '\t', '\n', '\r', ',', ';'], StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Trim('"', '\'', '“', '”', '‘', '’', '?', '!', '.', ':', '(', ')').ToLowerInvariant())
            .Where(w => w.Length >= 3 && !StopWords.Contains(w))
            .Distinct()
            .ToList();

    /// <summary>
    /// search_facts: search-facts.cypher and search-events.cypher run once per content word with <c>$term</c>; facts are
    /// ranked by how many distinct words they match (ties: reveal order), facts with the same text are shown once, and the rows are
    /// rendered as facts_about rows (display names, <c>ch. N</c>, late rows dropped with spoilers off), at most <see cref="MaxRows"/>.
    /// </summary>
    private string Search(ChatToolSpec spec, string keywords)
    {
        var watch = Stopwatch.StartNew();
        var terms = SearchTerms(keywords);
        var rows = new Dictionary<long, IReadOnlyList<object?>>();
        var matched = new Dictionary<long, HashSet<string>>();
        var revealedOf = new Dictionary<long, long>();
        var notes = new List<string>();
        IReadOnlyList<string> columns = ["fact", "about", "revealed", "factId", "aboutIds", "edgeIds"];
        foreach (var term in terms)
        {
            // The query's CONTAINS finds candidates; only whole-word matches in the text or a name count (tool layer, no regex
            // in the engine). Then a term with more than SearchMaxPerTerm facts contributes only its most recent ones.
            var word = WholeWord(term);
            var termFacts = new Dictionary<long, long>();
            foreach (var query in new[] { _queries[spec.Name], _queries[spec.Name + ChatToolSpec.SecondSuffix] })
            {
                var p = Parameters(null, null);
                p["term"] = term;
                var result = CypherEngine.Execute(_graph, query, p);
                var col = result.Columns.ToList();
                int iFact = col.IndexOf("fact"), iAbout = col.IndexOf("about"), iRevealed = col.IndexOf("revealed"), iId = col.IndexOf("factId"), iIds = col.IndexOf("aboutIds"),
                    iEdges = col.IndexOf("edgeIds");
                foreach (var row in result.Rows)
                {
                    if (row[iId] is not long id || Gate?.Knows(id) == false) continue;
                    var names = (row[iAbout] as IReadOnlyList<object?> ?? []).OfType<string>();
                    if (!(row[iFact] is string factText && word.IsMatch(factText)) && !names.Any(n => word.IsMatch(n))) continue;
                    var revealed = row[iRevealed] is long r ? r : long.MaxValue;
                    rows.TryAdd(id, [row[iFact], row[iAbout], row[iRevealed], row[iId], row[iIds], iEdges >= 0 ? row[iEdges] : null]);
                    termFacts[id] = termFacts.TryGetValue(id, out var seen) ? Math.Min(seen, revealed) : revealed;
                }
            }
            var kept = termFacts.Count > SearchMaxPerTerm ? termFacts.OrderByDescending(kv => kv.Value).Take(SearchMaxPerTerm) : termFacts;
            if (termFacts.Count > SearchMaxPerTerm)
                notes.Add($"(term \"{term}\" matched {termFacts.Count} rows; showing the {SearchMaxPerTerm} most recent)");
            foreach (var (id, revealed) in kept)
            {
                (matched.TryGetValue(id, out var set) ? set : matched[id] = []).Add(term);
                revealedOf[id] = revealedOf.TryGetValue(id, out var r) ? Math.Min(r, revealed) : revealed;
            }
        }
        var hits = matched.ToDictionary(kv => kv.Key, kv => (Row: rows[kv.Key], Terms: kv.Value, Revealed: revealedOf[kv.Key]));
        var ranked = hits.Values.OrderByDescending(h => h.Terms.Count).ThenBy(h => h.Revealed)
            .DistinctBy(h => h.Row[0] as string ?? "")
            .Select(h => h.Row).ToList();
        var elapsed = watch.Elapsed.TotalMilliseconds;

        var rendered = terms.Count == 0 ? new Rendered(Nothing, 0, 0, [], [], 0) : RenderWithIds(new QueryResult(columns, ranked), Nothing, recentFirst: false);
        var (text, shown, late) = (rendered.Text, rendered.Rows, rendered.LateRowsSuppressed);
        if (terms.Count == 0) text += "\n" + SearchHint;
        foreach (var note in notes) text += "\n" + note;

        var span = Activity.Current;
        span?.SetTag("tool.name", spec.Name);
        span?.SetTag("query.rows", shown);
        span?.SetTag("search.terms", string.Join(" ", terms));
        if (late > 0) span?.SetTag("chat.late_rows_suppressed", late);

        var record = new ToolCallRecord(Turn, spec.Name, new Dictionary<string, string> { ["keywords"] = keywords }, shown, Math.Round(elapsed, 2),
            UnknownName: false, Truncated: shown > MaxRows, ResolvedName: null, LateRowsSuppressed: late, NodeIds: rendered.NodeIds, EdgeIds: rendered.EdgeIds,
            UnknownRowsSuppressed: rendered.Unknown);
        _calls.Add(record);
        _onCall?.Invoke(record);
        return text;
    }

    /// <summary>A knowledge-pack section's rendered rows, the number of rows shown (late rows dropped), and the late rows dropped.</summary>
    public sealed record PackSection(string Heading, string Tool, int Rows, string Text, int LateRowsSuppressed = 0);

    /// <summary>
    /// The knowledge pack: facts_about, who_met, relations, events_for for the selected character and where_placed,
    /// run by code with this command's $pos/$spoilers, rendered as in the tools under headings. Not recorded as tool calls.
    /// </summary>
    /// <param name="displayName">Shown in the frame (the character's <see cref="DisplayName"/>).</param>
    /// <param name="character">The name passed as <c>$character</c> (one of the character's aliases).</param>
    public (string Text, IReadOnlyList<PackSection> Sections) KnowledgePack(string displayName, string character)
    {
        var sections = PackSections.Select(s =>
        {
            var spec = Specs.Single(x => x.Name == s.Tool);
            var result = Execute(spec, spec.TakesCharacter ? character : null, null);
            var (text, shown, late) = RenderRows(result, PackNothing, spec.RecentFirst);
            return new PackSection(s.Heading, s.Tool, shown, text, late);
        }).ToList();
        // A character the gate leaves with nothing gets one line saying so, not five empty headings.
        var text = Gate is not null && sections.All(s => s.Rows == 0)
            ? $"What you, {displayName}, know at this point (from the record; nothing else is known):\n{PackKnowsNothing}"
            : $"What you, {displayName}, know at this point (from the record; nothing else is known):\n{PackStampLine}\n"
                + string.Join("\n", sections.Select(s => $"## {s.Heading}\n{s.Text}"));
        return (text, sections);
    }

    /// <summary>
    /// <see cref="DisplayName"/> of a node by id, with whether a row naming it is late (no alias mentioned by <see cref="Pos"/>;
    /// spoilers off only). Unknown id or a node without a name (an event at a location) → null.
    /// </summary>
    /// <remarks>With the character gate, the name is chosen among the names the character has met
    /// (<see cref="CharacterGate.Met"/>), by the same preferences; when it has met none, the reader's rule decides.</remarks>
    public (string? Name, bool Late) DisplayNameOf(long id)
    {
        if (!_nodes.TryGetValue(id, out var node)) return (null, false);
        var shown = _union.ContainsKey(id) ? United(id) : node;
        if (Gate is not null && Union(id).SelectMany(m => Gate.Met(_nodes[m])).ToHashSet() is { Count: > 0 } met
            && shown.Properties.GetValueOrDefault("aliasFirstPos") is IReadOnlyDictionary<string, object?> first)
        {
            var properties = new Dictionary<string, object?>(shown.Properties)
            {
                ["aliasFirstPos"] = first.Where(kv => met.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value),
            };
            // Only met names are left, so nothing is late: every one of them counts, the earliest first.
            return DisplayName(new NodeValue(shown.Id, shown.Labels, properties), long.MaxValue, false, Ambiguous);
        }
        return DisplayName(shown, Pos, Spoilers, Ambiguous);
    }

    /// <summary>
    /// The nodes of a visible identity as one character, for the display name: all their aliases with the first place each
    /// was seen, so the name shown is the alias the reader saw first among the union; a roster or short name wins as for a single node.
    /// </summary>
    private NodeValue United(long id)
    {
        var members = Union(id).Select(m => _nodes[m]).ToList();
        var firstSeen = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var m in members)
            if (m.Properties.GetValueOrDefault("aliasFirstPos") is IReadOnlyDictionary<string, object?> first)
                foreach (var (alias, at) in first)
                {
                    var pos = at switch { long l => l, double d => d, _ => double.MaxValue };
                    if (!firstSeen.TryGetValue(alias, out var have) || pos < (have switch { long l => l, double d => d, _ => double.MaxValue })) firstSeen[alias] = at;
                }
        // The member the reader met first gives the name, the short name and the roster name.
        static double Met(NodeValue n) => (n.Properties.GetValueOrDefault("aliasFirstPos") as IReadOnlyDictionary<string, object?>)?.Values
            .Select(v => v switch { long l => l, double d => d, _ => double.MaxValue }).DefaultIfEmpty(double.MaxValue).Min() ?? double.MaxValue;
        var lead = members.OrderBy(Met).ThenBy(m => m.Id).First();
        var properties = new Dictionary<string, object?>(lead.Properties) { ["aliasFirstPos"] = firstSeen };
        if (properties.GetValueOrDefault("rosterName") is null && members.Select(m => m.Properties.GetValueOrDefault("rosterName")).FirstOrDefault(r => r is string) is { } roster)
            properties["rosterName"] = roster;
        return new NodeValue(lead.Id, lead.Labels, properties);
    }

    /// <summary>
    /// The aliases (aliasFirstPos keys) that are shared with another entity: the alias's words, as a run of whole words,
    /// also occur in an alias of a different entity (case-insensitive). That covers an alias two entities both have and a bare
    /// surname inside other entities' names ("Baskerville": Sir Charles Baskerville, Hugo Baskerville, Baskerville Hall).
    /// Computed once per graph.
    /// </summary>
    public static IReadOnlySet<string> AmbiguousAliases(BookGraph graph)
    {
        var aliases = graph.Nodes
            .SelectMany(n => n.Properties.GetValueOrDefault("aliasFirstPos") is IReadOnlyDictionary<string, object?> first ? first.Keys.Select(k => (Alias: k, n.Id)) : [])
            .ToList();
        // Every run of whole words of every alias → the entities it occurs in.
        var owners = new Dictionary<string, HashSet<long>>(StringComparer.Ordinal);
        foreach (var (alias, id) in aliases)
        {
            var w = AliasWords(alias);
            for (var i = 0; i < w.Length; i++)
                for (var j = i + 1; j <= w.Length; j++)
                {
                    var key = string.Join(' ', w[i..j]);
                    (owners.TryGetValue(key, out var set) ? set : owners[key] = []).Add(id);
                }
        }
        return aliases.Where(x => AliasWords(x.Alias) is { Length: > 0 } w && owners[string.Join(' ', w)].Count > 1)
            .Select(x => x.Alias).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>An alias's words, lower-cased (letters and digits; everything else separates).</summary>
    public static string[] AliasWords(string alias) =>
        Regex.Split(alias.ToLowerInvariant(), @"[^\p{L}\p{N}]+").Where(w => w.Length > 0).ToArray();

    /// <summary>
    /// The display name. A roster character (<c>rosterName</c>, the <c>books.json</c> spelling, see
    /// <see cref="ChatCharacters.WithRosterNames"/>) is shown by its roster name once the reader has met it: always with spoilers on,
    /// with them off once an alias of the same spelling (ignoring case and the apostrophe's shape) is mentioned by
    /// <paramref name="pos"/>. Then a character with a <c>shortName</c> (build-db: the name the book uses most) is shown by it whenever the reader has
    /// met that name: always with spoilers on, with them off once its <c>aliasFirstPos</c> ≤ <paramref name="pos"/>. Otherwise: spoilers
    /// on, the canonical <c>name</c>; off, the alias with the smallest <c>aliasFirstPos</c> ≤ <paramref name="pos"/> (ties: the longest,
    /// then ordinal); none qualifies → the canonical name, and Late = true. A node without <c>aliasFirstPos</c> (a graph built without
    /// alias first positions) keeps its name.
    /// </summary>
    public static (string? Name, bool Late) DisplayName(NodeValue node, long pos, bool spoilers, IReadOnlySet<string>? ambiguous = null)
    {
        var name = node.Properties.GetValueOrDefault("name") as string;
        var shortName = node.Properties.GetValueOrDefault("shortName") as string;
        var aliasPos = node.Properties.GetValueOrDefault("aliasFirstPos") as IReadOnlyDictionary<string, object?>;
        if (node.Properties.GetValueOrDefault("rosterName") is string roster && roster.Length > 0
            && (spoilers || aliasPos?.Where(kv => SameName(kv.Key, roster)).Select(kv => kv.Value switch { long l => l, double d => d, _ => double.MaxValue })
                .DefaultIfEmpty(double.MaxValue).Min() <= pos))
            return (roster, false);
        if (shortName is { Length: > 0 } && (spoilers || (aliasPos?.GetValueOrDefault(shortName) switch { long l => l, double d => d, _ => double.MaxValue }) <= pos))
            return (shortName, false);
        if (spoilers || aliasPos is not { Count: > 0 } first)
            return (name, false);
        // BookGraph reads JSON numbers inside a property map as double; offsets compare the same either way.
        var revealed = first.Select(kv => (kv.Key, At: kv.Value switch { long l => l, double d => (double?)d, _ => null }))
            .Where(x => x.At <= pos).ToList();
        // An alias shared with another entity is skipped unless nothing else is revealed.
        var candidates = ambiguous is null ? revealed : revealed.Where(x => !ambiguous.Contains(x.Key)).ToList() is { Count: > 0 } clear ? clear : revealed;
        var alias = candidates.OrderBy(x => x.At).ThenByDescending(x => x.Key.Length).ThenBy(x => x.Key, StringComparer.Ordinal)
            .Select(x => x.Key).FirstOrDefault();
        return alias is null ? (name, true) : (alias, false);
    }

    /// <summary>Two spellings of a name are the same ignoring case, surrounding spaces and the apostrophe's shape (' ’ ‘).</summary>
    public static bool SameName(string a, string b) =>
        string.Equals(NormalizeName(a), NormalizeName(b), StringComparison.OrdinalIgnoreCase);

    private static string NormalizeName(string s) => s.Trim().Replace('\u2019', '\'').Replace('\u2018', '\'');

    /// <summary>The chapter whose [CharStart, CharEnd) holds <paramref name="offset"/>, as <c>ch. N</c> (between chapters: the one before).</summary>
    public static string ChapterStamp(BookGraph graph, long offset)
    {
        var ch = graph.Chapters.Where(c => c.Value.CharStart <= offset && offset < c.Value.CharEnd).Select(c => (int?)c.Key).FirstOrDefault()
            ?? graph.Chapters.Where(c => c.Value.CharStart <= offset).OrderByDescending(c => c.Key).Select(c => (int?)c.Key).FirstOrDefault();
        return ch is { } n ? $"ch. {n}" : $"before ch. {graph.Chapters.Keys.DefaultIfEmpty(1).Min()}";
    }

    /// <summary>
    /// One row per line, columns joined with " · ". A column <c>X</c> with an <c>XId</c> (or list <c>XIds</c>) column shows the
    /// display names of those nodes; the id columns are not shown. <see cref="RevealedColumns"/> show <c>ch. N</c>. With spoilers
    /// off, late rows (naming an entity not yet mentioned at <see cref="Pos"/>) are dropped first. Then at most
    /// <see cref="MaxRows"/> rows (unless <paramref name="cap"/> is false): the last ones when <paramref name="recentFirst"/>
    /// ("(last N of M rows; earlier rows omitted)"), else the first ("(first N of M rows)"). Empty → <paramref name="empty"/>.
    /// </summary>
    public (string Text, int LateRowsSuppressed) Render(QueryResult result, string empty = Nothing, bool recentFirst = true, bool cap = true)
    {
        var (text, _, late) = RenderRows(result, empty, recentFirst, cap);
        return (text, late);
    }

    /// <summary><see cref="Render"/>, with the number of rows left after late rows were dropped (before the cap).</summary>
    public (string Text, int Rows, int LateRowsSuppressed) RenderRows(QueryResult result, string empty = Nothing, bool recentFirst = true, bool cap = true)
    {
        var r = RenderWithIds(result, empty, recentFirst, cap);
        return (r.Text, r.Rows, r.LateRowsSuppressed);
    }

    /// <summary>A rendering, with the node and edge ids of the rows shown (after late rows are dropped and the cap).</summary>
    /// <param name="Unknown">Rows the character gate dropped.</param>
    public sealed record Rendered(string Text, int Rows, int LateRowsSuppressed, IReadOnlyList<long> NodeIds, IReadOnlyList<long> EdgeIds, int Unknown = 0);

    /// <summary>A column the model never sees: a name ending in <c>Id</c> or <c>Ids</c> (also the unpaired ones).</summary>
    public static bool IsIdColumn(string column) => column.EndsWith("Id", StringComparison.Ordinal) || column.EndsWith("Ids", StringComparison.Ordinal);

    /// <summary>An id column holding edge ids (<c>edgeId</c>, <c>edgeIds</c>); other id columns hold node ids.</summary>
    public static bool IsEdgeIdColumn(string column) => IsIdColumn(column) && column.StartsWith("edge", StringComparison.Ordinal);

    /// <summary><see cref="RenderRows"/>, with the ids of the rows the model sees.</summary>
    public Rendered RenderWithIds(QueryResult result, string empty = Nothing, bool recentFirst = true, bool cap = true)
    {
        if (result.Rows.Count == 0) return new Rendered(empty, 0, 0, [], []);
        var columns = result.Columns.ToList();
        var idColumn = new Dictionary<int, int>();
        for (var i = 0; i < columns.Count; i++)
        {
            var id = columns.IndexOf(columns[i] + "Id");
            if (id < 0) id = columns.IndexOf(columns[i] + "Ids");
            if (id >= 0) idColumn[i] = id;
        }
        var hidden = Enumerable.Range(0, columns.Count).Where(i => IsIdColumn(columns[i])).ToHashSet();

        var late = 0;
        var unknownRows = 0;
        var kept = new List<(string Line, List<long> Nodes, List<long> Edges)>();
        var lineAt = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var queried in result.Rows)
        {
            // The character gate: a row the character who speaks does not know is dropped; a list in a kept row loses
            // the ones it has never heard of.
            if (GateRow(queried, columns, idColumn, hidden) is not { } row)
            {
                unknownRows++;
                continue;
            }
            var rowLate = false;
            var cells = new List<string>();
            for (var i = 0; i < row.Count; i++)
            {
                if (hidden.Contains(i)) continue;
                var value = row[i];
                if (idColumn.TryGetValue(i, out var idCol)) value = Names(value, row[idCol], ref rowLate);
                else if (RevealedColumns.Contains(columns[i]) && value is long offset) value = ChapterStamp(_graph, offset);
                cells.Add(CypherValues.Display(value));
            }
            // The post-filter: a row that carries the id of a node or an edge the rule hides is dropped, in both modes
            // (the same SpoilerGate the queries' visible() uses). A book indexed before the full schema has no clocks to gate by.
            if (_gated && !rowLate)
                foreach (var i in hidden)
                {
                    var ids = new List<long>();
                    Ids(row[i], ids);
                    if (IsEdgeIdColumn(columns[i]) ? ids.All(EdgeVisible) : ids.All(NodeVisible)) continue;
                    rowLate = true;
                    break;
                }
            var line = string.Join(" · ", cells);
            // DisplayName flags a row late only with spoilers off, so with spoilers on nothing is dropped.
            if (rowLate)
            {
                late++;
                continue;
            }
            var nodes = new List<long>();
            var edges = new List<long>();
            foreach (var i in hidden) Ids(row[i], IsEdgeIdColumn(columns[i]) ? edges : nodes);
            // Rows that differ only in which member of an identity union they came from are one row (the first), with
            // the ids of all of them.
            if (_gated && !lineAt.TryAdd(line, kept.Count))
            {
                var same = lineAt[line];
                kept[same].Nodes.AddRange(nodes);
                kept[same].Edges.AddRange(edges);
                continue;
            }
            kept.Add((line, nodes, edges));
        }
        var shown = kept.Count;
        if (shown == 0) return new Rendered(empty, 0, late, [], [], unknownRows);
        var seen = cap && shown > MaxRows ? (recentFirst ? kept.Skip(shown - MaxRows).ToList() : kept.Take(MaxRows).ToList()) : kept;
        var lines = seen.Select(k => k.Line).ToList();
        if (cap && shown > MaxRows)
            lines.Add(recentFirst ? $"(last {MaxRows} of {shown} rows; earlier rows omitted)" : $"(first {MaxRows} of {shown} rows)");
        return new Rendered(string.Join("\n", lines), shown, late,
            seen.SelectMany(k => k.Nodes).Distinct().ToList(), seen.SelectMany(k => k.Edges).Distinct().ToList(), unknownRows);
    }

    /// <summary>
    /// One row through the character gate. An id column that holds one id decides the row: a Fact, an Event, a Character
    /// or a Location the character does not know, or an edge it can't know of, drops it (null). An id list loses its unknown
    /// members, with their names in the paired column; a row that stands only on edges (who_met: the meetings) is dropped when none
    /// is left. Without a gate the row comes back as it is.
    /// </summary>
    private IReadOnlyList<object?>? GateRow(IReadOnlyList<object?> row, List<string> columns, Dictionary<int, int> idColumn, HashSet<int> hidden)
    {
        if (Gate is not { } gate) return row;
        object?[]? copy = null;
        var own = hidden.Any(i => row[i] is long id && !IsEdgeIdColumn(columns[i]) && gate.IsFactOrEvent(id));
        foreach (var i in hidden)
        {
            var edge = IsEdgeIdColumn(columns[i]);
            bool Known(long id) => edge ? gate.KnowsEdge(id) : gate.Knows(id);
            if (row[i] is long one)
            {
                if (!Known(one)) return null;
            }
            else if (row[i] is IReadOnlyList<object?> list)
            {
                var keep = list.Select(v => v is not long id || Known(id)).ToList();
                if (keep.All(k => k)) continue;
                if (edge && !own && !keep.Any(k => k)) return null;
                copy ??= [.. row];
                copy[i] = list.Where((_, k) => keep[k]).ToList();
                foreach (var (nameColumn, idCol) in idColumn)
                    if (idCol == i && row[nameColumn] is IReadOnlyList<object?> names && names.Count == list.Count)
                        copy[nameColumn] = names.Where((_, k) => keep[k]).ToList();
            }
        }
        return copy ?? row;
    }

    /// <summary>An id (long) or a list of ids added to <paramref name="into"/>; anything else ignored.</summary>
    private static void Ids(object? value, List<long> into)
    {
        if (value is long id) into.Add(id);
        else if (value is IReadOnlyList<object?> list) foreach (var v in list) if (v is long l) into.Add(l);
    }

    /// <summary>A name (or list of names) replaced by the display name of its id (or id list); anything else, or misaligned lists → unchanged.</summary>
    private object? Names(object? names, object? ids, ref bool late)
    {
        switch (names, ids)
        {
            case (string, long id):
                var (name, isLate) = DisplayNameOf(id);
                late |= isLate;
                return name ?? names;
            case (IReadOnlyList<object?> list, IReadOnlyList<object?> idList) when list.Count == idList.Count:
                var mapped = new List<object?>(list.Count);
                for (var k = 0; k < list.Count; k++) mapped.Add(Names(list[k], idList[k], ref late));
                // The members of an identity union share one display name, so the list names the union once.
                return _gated ? mapped.Distinct().ToList() : mapped;
            default:
                return names;
        }
    }
}
