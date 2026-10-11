using TemporalGraph.Query;

namespace TemporalGraph.Api;

/// <summary>
/// The whole promoted graph of a book, for the 2D/3D tabs to gate in the browser (one fetch per book, cached in
/// <see cref="BookCatalog"/>). Nothing is gated here, as in the Graph Query tab; the client applies the chat tools' rules.
/// <para>Both clocks on every node and edge, and the book's timeline, so the browser applies the spoiler rule
/// (the same one <see cref="SpoilerGate"/> gives the engine and the chat tools) and draws the slider without another call. A book
/// indexed before the full schema has an empty timeline and no clocks on its nodes: the client keeps its earlier gate for it.</para>
/// </summary>
[GraphQLName("BookGraphPayload")]
public sealed record BookGraphPayload(IReadOnlyList<GraphNodeView> Nodes, IReadOnlyList<GraphEdgeView> Edges, IReadOnlyList<GraphChapterView> Chapters,
    IReadOnlyList<GraphTimelineRow> Timeline)
{
    /// <summary>Fact and Event text longer than this is cut, ending in "…".</summary>
    public const int MaxText = 160;

    public static BookGraphPayload From(BookGraph graph)
    {
        var nodes = graph.Nodes.Select(n => new GraphNodeView(
            (int)n.Id,
            n.Labels.FirstOrDefault() ?? "",
            n.Properties.GetValueOrDefault("name") as string,
            n.Properties.GetValueOrDefault("shortName") as string,
            n.Properties.GetValueOrDefault("rosterName") as string,
            Cap(n.Properties.GetValueOrDefault("text") as string ?? n.Properties.GetValueOrDefault("summary") as string),
            Pos(n.Properties.GetValueOrDefault("evidenceStart")),
            n.Properties.GetValueOrDefault("aliasFirstPos") is IReadOnlyDictionary<string, object?> first
                ? first.Select(kv => (kv.Key, Pos: Pos(kv.Value))).Where(x => x.Pos is not null).Select(x => new AliasPos(x.Key, x.Pos!.Value)).OrderBy(a => a.Pos).ThenBy(a => a.Alias, StringComparer.Ordinal).ToList()
                : [],
            n.Properties.GetValueOrDefault("kind") as string,
            Pos(n.Properties.GetValueOrDefault("revealedPos")),
            Pos(n.Properties.GetValueOrDefault("retractedPos")),
            n.Properties.GetValueOrDefault("storyFrom") as string,
            n.Properties.GetValueOrDefault("storyFromLo") as string,
            n.Properties.GetValueOrDefault("storyUntilLo") as string,
            n.Properties.GetValueOrDefault("subject") is IReadOnlyList<object?> subject ? subject.Select(Pos).Where(s => s is not null).Select(s => s!.Value).ToList() : [],
            Num(n.Properties.GetValueOrDefault("layoutX")) is { } x && Num(n.Properties.GetValueOrDefault("layoutY")) is { } y && Num(n.Properties.GetValueOrDefault("layoutZ")) is { } z
                ? new GraphLayout3(x, y, z) : null,
            Num(n.Properties.GetValueOrDefault("layout2X")) is { } x2 && Num(n.Properties.GetValueOrDefault("layout2Y")) is { } y2 ? new GraphLayout2(x2, y2) : null)).ToList();
        var edges = graph.Relationships.Select(r => new GraphEdgeView((int)r.Id, (int)r.StartId, (int)r.EndId, r.Type, Pos(r.Properties.GetValueOrDefault("revealedPos")),
            r.Properties.GetValueOrDefault("kind") as string ?? r.Properties.GetValueOrDefault("how") as string,
            Pos(r.Properties.GetValueOrDefault("retractedPos")),
            r.Properties.GetValueOrDefault("storyFrom") as string,
            r.Properties.GetValueOrDefault("storyFromLo") as string,
            r.Properties.GetValueOrDefault("storyUntilLo") as string)).ToList();
        var chapters = graph.Chapters.OrderBy(c => c.Key).Select(c => new GraphChapterView(c.Key, c.Value.CharEnd)).ToList();
        var timeline = graph.Timeline.Select(t => new GraphTimelineRow(t.Kind, t.RefId is { } id ? (int)id : null, Cap(t.Label), t.RevealedPos, t.RevealedPosEnd,
            t.StoryFrom, t.StoryUntil, t.StoryFromLo, t.StoryFromHi, t.StoryUntilLo, t.StoryUntilHi, t.FabulaSeq, t.Note)).ToList();
        return new BookGraphPayload(nodes, edges, chapters, timeline);
    }

    private static string? Cap(string? text) => text is { Length: > MaxText } t ? t[..(MaxText - 1)].TrimEnd() + "…" : text;

    // BookGraph reads offsets as long, and offsets inside a property map (aliasFirstPos) as double.
    private static int? Pos(object? v) => v switch { long l => (int)l, double d => (int)d, int i => i, _ => null };

    private static double? Num(object? v) => v switch { double d => d, long l => l, int i => i, _ => null };
}

/// <param name="Name">Characters and Locations: the canonical name; null for Facts and Events (they have <paramref name="Text"/>).</param>
/// <param name="ShortName">Characters: the name the book uses most (build-db), shown once the reader has met it; else null.</param>
/// <param name="RosterName">Roster characters' books.json spelling, shown (before the short name) once the reader has met it; else null.</param>
/// <param name="Text">A Fact's sentence or an Event's summary.</param>
/// <param name="Pos">The node's first evidence offset (evidenceStart).</param>
/// <param name="AliasFirstPos">Each alias with the offset of its first mention, in mention order; empty for Facts and Events.</param>
/// <param name="Kind">An Event's kind, <c>narrated</c> or <c>referenced</c>; null for the other labels.</param>
/// <param name="RevealedPos">The reader clock: where the node is exposed. Null in a book indexed before the full schema.</param>
/// <param name="RetractedPos">Where the reader learns it was false; null when never.</param>
/// <param name="StoryFrom">The story clock: when it starts in story time, as EDTF (for display).</param>
/// <param name="StoryFromLo">The lower bound of <paramref name="StoryFrom"/> (ISO yyyy-MM-dd): what the Spoilers-on rule compares.</param>
/// <param name="StoryUntilLo">The lower bound of the story-time end, when there is one.</param>
/// <param name="Subject">The ids of the nodes a Fact is about (there is no ABOUT edge); empty for the other labels.</param>
/// <param name="Layout">Where the 3D tab draws the node, computed once by <c>index layout</c>; null when the book has no layout.</param>
/// <param name="Layout2">Where the 2D tab draws it; null when the book has no layout.</param>
[GraphQLName("GraphNode")]
public sealed record GraphNodeView(int Id, string Label, string? Name, string? ShortName, string? RosterName, string? Text, int? Pos, IReadOnlyList<AliasPos> AliasFirstPos,
    string? Kind = null, int? RevealedPos = null, int? RetractedPos = null, string? StoryFrom = null, string? StoryFromLo = null, string? StoryUntilLo = null,
    IReadOnlyList<int>? Subject = null, GraphLayout3? Layout = null, GraphLayout2? Layout2 = null);

/// <summary>A node's stored position in the 3D layout.</summary>
[GraphQLName("GraphLayout3")]
public sealed record GraphLayout3(double X, double Y, double Z);

/// <summary>A node's stored position in the 2D layout.</summary>
[GraphQLName("GraphLayout2")]
public sealed record GraphLayout2(double X, double Y);

[GraphQLName("AliasFirstPos")]
public sealed record AliasPos(string Alias, int Pos);

/// <param name="Pos">revealedPos: where the reader learns the edge.</param>
/// <param name="Kind">A PERCEIVES edge's kind (one of the twelve, also its icon's key), a DISCOVERED edge's <c>how</c>, a RELATED_TO edge's kind; else null.</param>
/// <param name="RetractedPos">Where a perception was closed for the reader; null while it stands.</param>
/// <param name="StoryFromLo">The lower bound of its story-time start (ISO), for the Spoilers-on rule.</param>
/// <param name="StoryUntilLo">The lower bound of its story-time end; null while it holds.</param>
[GraphQLName("GraphEdge")]
public sealed record GraphEdgeView(int Id, int Source, int Target, string Type, int? Pos, string? Kind = null, int? RetractedPos = null, string? StoryFrom = null,
    string? StoryFromLo = null, string? StoryUntilLo = null);

[GraphQLName("GraphChapter")]
public sealed record GraphChapterView(int Number, int CharEnd);

/// <summary>One row of the published timeline: the <c>anchor</c> (the one assumed date; <paramref name="Note"/> states the
/// assumption), a <c>chapter</c> (<paramref name="RefId"/> its number) or an <c>event</c> (<paramref name="RefId"/> the Event node's id).</summary>
[GraphQLName("GraphTimelineRow")]
public sealed record GraphTimelineRow(string Kind, int? RefId, string? Label, int? RevealedPos, int? RevealedPosEnd, string? StoryFrom, string? StoryUntil,
    string? StoryFromLo, string? StoryFromHi, string? StoryUntilLo, string? StoryUntilHi, double? FabulaSeq, string? Note);
