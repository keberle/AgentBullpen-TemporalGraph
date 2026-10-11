using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TemporalGraph.Data;

namespace TemporalGraph.Query;

/// <summary>
/// One book's graph in memory, for <see cref="CypherEngine"/>: nodes and relationships in id order, with adjacency lists,
/// and the chapters (for <c>$pos</c>). Loaded read-only from a book database (TemporalGraph.Data schema):
/// <list type="bullet">
/// <item><b>Node:</b> labels from <c>node_labels</c>; properties = the keys of the <c>properties</c> JSON object, plus
/// <c>evidenceStart</c> / <c>evidenceEnd</c> from the columns.</item>
/// <item><b>Relationship:</b> type = <c>edges.type</c>; properties = the keys of <c>edges.properties</c>, plus the columns
/// <c>revealedPos</c>, <c>retractedPos</c>, <c>evidenceStart</c>, <c>evidenceEnd</c>, <c>storyFrom</c> (story_from_edtf),
/// <c>storyUntil</c>, <c>storyFromLo</c>, <c>storyFromHi</c>, <c>storyUntilLo</c>, <c>storyUntilHi</c>, <c>fabulaSeq</c>.</item>
/// <item>A JSON key with a column's name is overridden by the column (<see cref="Collisions"/> lists any). A null column is
/// left out, so the property reads as null, the same as a missing one.</item>
/// </list>
/// No hidden time filter is applied. <c>event_order</c> is not loaded.
/// </summary>
public sealed class BookGraph
{
    public IReadOnlyList<NodeValue> Nodes { get; }
    public IReadOnlyList<RelationshipValue> Relationships { get; }
    /// <summary>Chapter number → [CharStart, CharEnd).</summary>
    public IReadOnlyDictionary<int, (int CharStart, int CharEnd)> Chapters { get; }
    /// <summary>"node 12: evidenceStart" for every JSON key a column overrode while loading.</summary>
    public IReadOnlyList<string> Collisions { get; }
    /// <summary>The published timeline (anchor, chapters, events), in table order; empty for a book indexed before the full schema.</summary>
    public IReadOnlyList<TimelineEntry> Timeline { get; }

    private readonly Dictionary<long, NodeValue> _byId;
    private readonly Dictionary<long, List<RelationshipValue>> _out = [];
    private readonly Dictionary<long, List<RelationshipValue>> _in = [];

    public BookGraph(IEnumerable<NodeValue> nodes, IEnumerable<RelationshipValue> relationships,
        IReadOnlyDictionary<int, (int, int)>? chapters = null, IReadOnlyList<string>? collisions = null, IReadOnlyList<TimelineEntry>? timeline = null)
    {
        Timeline = timeline ?? [];
        Nodes = nodes.OrderBy(n => n.Id).ToList();
        Relationships = relationships.OrderBy(r => r.Id).ToList();
        Chapters = chapters ?? new Dictionary<int, (int, int)>();
        Collisions = collisions ?? [];
        _byId = Nodes.ToDictionary(n => n.Id);
        foreach (var r in Relationships)
        {
            if (!_byId.ContainsKey(r.StartId) || !_byId.ContainsKey(r.EndId))
                throw new ArgumentException($"Relationship {r.Id} points to a missing node.");
            (_out.TryGetValue(r.StartId, out var o) ? o : _out[r.StartId] = []).Add(r);
            (_in.TryGetValue(r.EndId, out var i) ? i : _in[r.EndId] = []).Add(r);
        }
    }

    public NodeValue Node(long id) => _byId[id];
    public NodeValue? NodeOrNull(long id) => _byId.GetValueOrDefault(id);
    public IReadOnlyList<RelationshipValue> Outgoing(long nodeId) => _out.TryGetValue(nodeId, out var l) ? l : [];
    public IReadOnlyList<RelationshipValue> Incoming(long nodeId) => _in.TryGetValue(nodeId, out var l) ? l : [];

    /// <summary><c>story(pos)</c>: how far the story clock has run when the reader is at <paramref name="pos"/>.
    /// Null for a book without a timeline. See <see cref="SpoilerGate.StoryAt"/>.</summary>
    public string? StoryAt(long pos) => SpoilerGate.StoryAt(Timeline, pos);

    /// <summary>The read-only context <see cref="Load"/> reads through (exposed for the read-only test).</summary>
    internal static BookDbContext Open(string sqlitePath) => BookDatabase.OpenReadOnlyFile(sqlitePath);

    /// <summary>Loads a book database file read-only. The file is closed when this returns.</summary>
    public static BookGraph Load(string sqlitePath)
    {
        using var db = Open(sqlitePath);
        var collisions = new List<string>();
        var labels = db.NodeLabels.AsEnumerable().GroupBy(l => l.NodeId).ToDictionary(g => g.Key, g => g.Select(l => l.Label).Order(StringComparer.Ordinal).ToList());
        var nodes = db.Nodes.OrderBy(n => n.Id).AsEnumerable().Select(n =>
        {
            var p = Json(n.Properties);
            Column(p, "evidenceStart", n.EvidenceStart, $"node {n.Id}", collisions);
            Column(p, "evidenceEnd", n.EvidenceEnd, $"node {n.Id}", collisions);
            // A node's clocks, under the same names as an edge's. A file built before the full schema has none (the columns are null).
            if (n.RevealedPos is not null || n.StoryFromEdtf is not null)
            {
                var where = $"node {n.Id}";
                Column(p, "revealedPos", n.RevealedPos, where, collisions);
                Column(p, "retractedPos", n.RetractedPos, where, collisions);
                Column(p, "storyFrom", n.StoryFromEdtf, where, collisions);
                Column(p, "storyUntil", n.StoryUntilEdtf, where, collisions);
                Column(p, "storyFromLo", n.StoryFromLo, where, collisions);
                Column(p, "storyFromHi", n.StoryFromHi, where, collisions);
                Column(p, "storyUntilLo", n.StoryUntilLo, where, collisions);
                Column(p, "storyUntilHi", n.StoryUntilHi, where, collisions);
                Column(p, "fabulaSeq", n.FabulaSeq, where, collisions);
            }
            // The stored layout, when one has been computed.
            if (n.LayoutX is not null || n.Layout2X is not null)
            {
                var where = $"node {n.Id}";
                Column(p, "layoutX", n.LayoutX, where, collisions);
                Column(p, "layoutY", n.LayoutY, where, collisions);
                Column(p, "layoutZ", n.LayoutZ, where, collisions);
                Column(p, "layout2X", n.Layout2X, where, collisions);
                Column(p, "layout2Y", n.Layout2Y, where, collisions);
            }
            return new NodeValue(n.Id, labels.TryGetValue(n.Id, out var l) ? l : [], p);
        }).ToList();
        var edges = db.Edges.OrderBy(e => e.Id).AsEnumerable().Select(e =>
        {
            var p = Json(e.Properties);
            var where = $"edge {e.Id}";
            Column(p, "revealedPos", e.RevealedPos, where, collisions);
            Column(p, "retractedPos", e.RetractedPos, where, collisions);
            Column(p, "evidenceStart", e.EvidenceStart, where, collisions);
            Column(p, "evidenceEnd", e.EvidenceEnd, where, collisions);
            Column(p, "storyFrom", e.StoryFromEdtf, where, collisions);
            Column(p, "storyUntil", e.StoryUntilEdtf, where, collisions);
            Column(p, "storyFromLo", e.StoryFromLo, where, collisions);
            Column(p, "storyFromHi", e.StoryFromHi, where, collisions);
            Column(p, "storyUntilLo", e.StoryUntilLo, where, collisions);
            Column(p, "storyUntilHi", e.StoryUntilHi, where, collisions);
            Column(p, "fabulaSeq", e.FabulaSeq, where, collisions);
            return new RelationshipValue(e.Id, e.Type, e.SourceId, e.TargetId, p);
        }).ToList();
        var chapters = db.Chapters.AsEnumerable().ToDictionary(c => c.Number, c => (c.CharStart, c.CharEnd));
        var timeline = db.Timeline.OrderBy(t => t.Id).AsEnumerable().Select(t => new TimelineEntry(t.Kind, t.RefId, t.Label, t.RevealedPos, t.RevealedPosEnd,
            t.StoryFromEdtf, t.StoryUntilEdtf, t.StoryFromLo, t.StoryFromHi, t.StoryUntilLo, t.StoryUntilHi, t.FabulaSeq, t.Note)).ToList();
        return new BookGraph(nodes, edges, chapters, collisions, timeline);
    }

    private static void Column(Dictionary<string, object?> p, string name, object? value, string where, List<string> collisions)
    {
        if (p.ContainsKey(name)) collisions.Add($"{where}: {name}");
        p.Remove(name);
        if (value is not null) p[name] = CypherValues.FromClr(value);
    }

    private static Dictionary<string, object?> Json(string json)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return [];
        // Keys in the order they are stored; a JSON null is a missing property.
        var result = new Dictionary<string, object?>();
        foreach (var prop in doc.RootElement.EnumerateObject())
            if (FromJson(prop.Value) is { } v) result[prop.Name] = v;
        return result;
    }

    private static object? FromJson(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number => e.TryGetInt64(out var l) ? l : e.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Array => e.EnumerateArray().Select(FromJson).ToList(),
        JsonValueKind.Object => e.EnumerateObject().ToDictionary(p => p.Name, p => FromJson(p.Value)),
        _ => null,
    };
}
