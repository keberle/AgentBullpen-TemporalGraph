using TemporalGraph.Query;

namespace TemporalGraph.Chat;

/// <summary>
/// What the character who speaks knows at this position, worked out once from the graph. Everything the model is shown
/// (every tool result and the knowledge pack) passes through it after the reader's gate:
/// <list type="bullet">
/// <item><b>Facts:</b> those the character has a visible PERCEIVES edge to, except the ones it disbelieves.</item>
/// <item><b>Events:</b> those it takes part in, those that DISCOVERED a fact it knows, and a referenced event whose narrating event
/// it knows.</item>
/// <item><b>Characters and places:</b> itself, those it has a PERCEIVES edge to, those it shares an event with (the other
/// participants and the event's place), the subjects of the facts it knows, and those a fact or an event it knows names in its
/// text. Anyone else it has never heard of.</item>
/// <item><b>Names:</b> a name of someone it knows counts as met when the book first uses it no later than the last thing the
/// character learned, or when it stands in a fact or an event the character knows.</item>
/// </list>
/// The spoiler mode moves the clock (what is visible), not who knows what: the gate is the same with spoilers on.
/// </summary>
public sealed class CharacterGate
{
    private readonly Dictionary<long, NodeValue> _nodes;
    private readonly Dictionary<long, RelationshipValue> _edges;
    private readonly Func<long, IReadOnlyList<long>> _union;
    private readonly long _pos;
    private readonly bool _spoilers;
    private readonly HashSet<long> _self;
    private readonly HashSet<long> _ownEvents = [];
    private readonly HashSet<long> _events = [];
    private readonly HashSet<long> _entities = [];
    private readonly Dictionary<long, IReadOnlySet<string>> _met = [];
    private readonly string _heard;
    private readonly string _written;
    private readonly double _horizon;

    public long CharacterId { get; }

    /// <summary>The facts the character knows.</summary>
    public IReadOnlySet<long> Facts { get; }

    /// <summary>The events the character knows of.</summary>
    public IReadOnlySet<long> Events => _events;

    /// <summary>The characters and places the character has heard of (itself included).</summary>
    public IReadOnlySet<long> Entities => _entities;

    /// <param name="knownFacts"><see cref="ChatTools.KnownFacts"/> of the character.</param>
    /// <param name="union">The nodes that are one character with a node (a visible identity).</param>
    /// <param name="visible">The spoiler rule at this position, on an element's properties.</param>
    public CharacterGate(BookGraph graph, long characterId, IReadOnlySet<long> knownFacts, Func<long, IReadOnlyList<long>> union,
        Func<IReadOnlyDictionary<string, object?>, bool> visible, long pos, bool spoilers)
    {
        (_union, _pos, _spoilers, CharacterId, Facts) = (union, pos, spoilers, characterId, knownFacts);
        _nodes = graph.Nodes.ToDictionary(n => n.Id);
        _edges = graph.Relationships.ToDictionary(r => r.Id);
        _self = union(characterId).ToHashSet();
        bool Is(long id, string label) => _nodes.TryGetValue(id, out var n) && n.Labels.Contains(label) && visible(n.Properties);
        // Someone the character has acted with or been told a fact about is known to it even when the graph's first mention of that
        // node comes later than the event or the fact (the extraction lists a chunk's names once and misses some): the edge is told.
        bool Named(long id) => _nodes.TryGetValue(id, out var n) && (n.Labels.Contains("Character") || n.Labels.Contains("Location"));
        double horizon = -1;
        void Reach(IReadOnlyDictionary<string, object?> p) => horizon = Math.Max(horizon, Number(p.GetValueOrDefault("revealedPos")) ?? -1);

        var known = new HashSet<long>(_self);
        foreach (var member in _self)
            foreach (var r in graph.Outgoing(member).Where(r => visible(r.Properties)))
            {
                if (r.Type == "PARTICIPATED_IN" && Is(r.EndId, "Event"))
                {
                    _ownEvents.Add(r.EndId);
                    Reach(_nodes[r.EndId].Properties);
                }
                else if (r.Type == "PERCEIVES")
                {
                    if (Is(r.EndId, "Character") || Is(r.EndId, "Location")) known.Add(r.EndId);
                    Reach(r.Properties);
                }
            }
        _horizon = horizon;

        _events.UnionWith(_ownEvents);
        foreach (var fact in knownFacts)
            foreach (var d in graph.Incoming(fact).Where(r => r.Type == "DISCOVERED" && visible(r.Properties) && Is(r.StartId, "Event")))
                _events.Add(d.StartId);
        // A referenced event is known when the event that mentions it is.
        var mentions = graph.Relationships.Where(r => r.Type == "MENTIONED_IN" && visible(r.Properties) && Is(r.StartId, "Event")).ToList();
        for (var grew = true; grew;)
        {
            grew = false;
            foreach (var m in mentions)
                if (_events.Contains(m.EndId) && _events.Add(m.StartId)) grew = true;
        }

        foreach (var e in _ownEvents)
        {
            foreach (var p in graph.Incoming(e).Where(r => r.Type == "PARTICIPATED_IN" && visible(r.Properties) && Named(r.StartId))) known.Add(p.StartId);
            foreach (var l in graph.Outgoing(e).Where(r => r.Type == "LOCATED_AT" && visible(r.Properties) && Named(r.EndId))) known.Add(l.EndId);
        }
        foreach (var fact in knownFacts)
            foreach (var subject in Subjects(_nodes[fact]))
                if (Named(subject)) known.Add(subject);
        _heard = string.Join("\n", knownFacts.Select(f => _nodes[f].Properties.GetValueOrDefault("text") as string)
            .Concat(_events.Select(e => _nodes[e].Properties.GetValueOrDefault("summary") as string)).OfType<string>());
        // The book's own words behind what the character knows: the evidence quotes of its facts, on one line each.
        _written = string.Join("\n", knownFacts.Select(f => _nodes[f].Properties.GetValueOrDefault("evidence") as string).OfType<string>()
            .Select(e => string.Join(' ', e.Split((char[])[' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries))));
        // A fact's subjects are the people and places the extraction tied it to; a fact also names others in its text ("Sir Charles
        // died at Baskerville Hall" is about Sir Charles). Someone named in a fact or an event the character knows has been heard of.
        foreach (var n in graph.Nodes.Where(n => !known.Contains(n.Id) && Named(n.Id)))
            if (Aliases(n).Any(a => a.Length >= 3 && (char.IsUpper(a[0]) || a.Contains(' ')) && Heard(a))) known.Add(n.Id);
        foreach (var id in known) _entities.UnionWith(union(id));
    }

    private static IEnumerable<string> Aliases(NodeValue node) => (node.Properties.GetValueOrDefault("aliases") as IReadOnlyList<object?> ?? []).OfType<string>();

    private static double? Number(object? v) => v switch { long l => l, double d => d, int i => i, _ => null };

    private static IEnumerable<long> Subjects(NodeValue fact) =>
        (fact.Properties.GetValueOrDefault("subject") as IReadOnlyList<object?> ?? []).OfType<long>();

    /// <summary>Whether a row that carries this node may be shown: a Fact, an Event, a Character or a Location the character knows.
    /// Any other node, and an id that is no node, passes.</summary>
    public bool Knows(long nodeId)
    {
        if (!_nodes.TryGetValue(nodeId, out var n)) return true;
        if (n.Labels.Contains("Fact")) return Facts.Contains(nodeId);
        if (n.Labels.Contains("Event")) return _events.Contains(nodeId);
        if (n.Labels.Contains("Character") || n.Labels.Contains("Location")) return _entities.Contains(nodeId);
        return true;
    }

    /// <summary>Whether a row that carries this edge may be shown: taking part in an event or mentioning one needs the event known,
    /// a discovery needs the fact, a placement or a relation needs both ends.</summary>
    public bool KnowsEdge(long edgeId)
    {
        if (!_edges.TryGetValue(edgeId, out var e)) return true;
        return e.Type switch
        {
            "PARTICIPATED_IN" => _events.Contains(e.EndId),
            "DISCOVERED" => Facts.Contains(e.EndId),
            "PERCEIVES" => Knows(e.EndId) && (_self.Contains(e.StartId) || Shown(e)),
            _ => Knows(e.StartId) && Knows(e.EndId),
        };
    }

    /// <summary>How someone else takes a thing is known when the event that caused it is; without one, when it was told no later
    /// than the last thing the character learned.</summary>
    private bool Shown(RelationshipValue perception) =>
        perception.Properties.GetValueOrDefault("causeEvent") is long cause ? _events.Contains(cause) : (Number(perception.Properties.GetValueOrDefault("revealedPos")) ?? -1) <= _horizon;

    /// <summary>Whether the node is a Fact or an Event (a row of its own; see <see cref="ChatTools"/>'s row gate).</summary>
    public bool IsFactOrEvent(long nodeId) => _nodes.TryGetValue(nodeId, out var n) && (n.Labels.Contains("Fact") || n.Labels.Contains("Event"));

    /// <summary>
    /// The names of <paramref name="node"/> the character has met: none for someone it has never heard of; every name the reader
    /// has seen for itself; else the names first used no later than the last thing it learned (with spoilers off, never a name the
    /// reader has not seen yet), and those that stand in a fact or an event it knows.
    /// </summary>
    public IReadOnlySet<string> Met(NodeValue node)
    {
        if (_met.TryGetValue(node.Id, out var met)) return met;
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (_entities.Contains(node.Id) && node.Properties.GetValueOrDefault("aliasFirstPos") is IReadOnlyDictionary<string, object?> first)
        {
            var own = _self.Contains(node.Id);
            foreach (var (alias, at) in first)
            {
                var pos = Number(at) ?? double.MaxValue;
                // A name that stands in a fact or an event the character knows is met, whatever position the graph gives its first use.
                if (Heard(alias) || ((_spoilers || pos <= _pos) && (own || pos <= _horizon))) names.Add(alias);
            }
        }
        return _met[node.Id] = names;
    }

    /// <summary>The name as whole words in the text of a fact or an event the character knows: it has heard the name.</summary>
    public bool Heard(string name) => WholeWord(name, _heard);

    /// <summary>
    /// The name as whole words in the book's own text behind a fact the character knows (the fact's evidence quote). The graph's
    /// first mention of a name can lag the book (the extraction lists a chapter's names once and misses some): a name the book has
    /// already written in a passage the character was there for is no secret, whatever the node's first position says.
    /// </summary>
    public bool Wrote(string name) => WholeWord(name, _written);

    private static bool WholeWord(string name, string text)
    {
        if (name.Length == 0) return false;
        for (var i = text.IndexOf(name, StringComparison.Ordinal); i >= 0; i = text.IndexOf(name, i + 1, StringComparison.Ordinal))
        {
            var before = i == 0 || !char.IsLetterOrDigit(text[i - 1]);
            var after = i + name.Length >= text.Length || !char.IsLetterOrDigit(text[i + name.Length]);
            if (before && after) return true;
        }
        return false;
    }

    /// <summary>most_discussed for this character: the characters it knows, by how many of the facts it knows are about them.</summary>
    public IReadOnlyList<(long Character, long Facts)> MostDiscussed(int limit = 10)
    {
        var counts = new Dictionary<long, long>();
        foreach (var fact in Facts)
            foreach (var lead in Subjects(_nodes[fact]).Where(s => _entities.Contains(s) && _nodes[s].Labels.Contains("Character")).Select(s => _union(s)[0]).Distinct())
                counts[lead] = counts.GetValueOrDefault(lead) + 1;
        return counts.OrderByDescending(c => c.Value).ThenBy(c => c.Key).Take(limit).Select(c => (c.Key, c.Value)).ToList();
    }
}
