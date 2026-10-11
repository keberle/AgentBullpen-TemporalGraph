namespace TemporalGraph.Data;

// One book's graph (nodes, labels, edges, event order) plus the metadata the
// indexing pass fills in (chapters, positions, characters, locations). Column
// names are snake_case; see BookDbContext.OnModelCreating.
//
// Text positions are character offsets into the book's source text exactly as
// committed, decoded as UTF-8: UTF-16 code units of that string, CRLF counted as
// two. Ranges are [start, end).

public class Node
{
    public long Id { get; set; }

    /// <summary>JSON object.</summary>
    public string Properties { get; set; } = "{}";

    /// <summary>First grounding mention in the text.</summary>
    public int? EvidenceStart { get; set; }
    public int? EvidenceEnd { get; set; }

    // Both clocks on every node, the same columns as on an edge. Null only in a book file built before the full schema.
    public int? RevealedPos { get; set; }
    public int? RetractedPos { get; set; }
    public string? StoryFromEdtf { get; set; }
    public string? StoryUntilEdtf { get; set; }
    public string? StoryFromLo { get; set; }
    public string? StoryFromHi { get; set; }
    public string? StoryUntilLo { get; set; }
    public string? StoryUntilHi { get; set; }
    public double? FabulaSeq { get; set; }

    // Where the graph tabs draw the node, computed once by `index layout` (web/scripts/layout.mjs) over the whole graph: the
    // 3D tab's position and the 2D tab's. Null in a file no layout has been computed for; the tabs then lay it out themselves.
    public double? LayoutX { get; set; }
    public double? LayoutY { get; set; }
    public double? LayoutZ { get; set; }
    public double? Layout2X { get; set; }
    public double? Layout2Y { get; set; }

    public List<NodeLabel> Labels { get; set; } = [];
}

public class NodeLabel
{
    public long NodeId { get; set; }
    public string Label { get; set; } = "";

    public Node Node { get; set; } = null!;
}

public class Edge
{
    public long Id { get; set; }
    public long SourceId { get; set; }
    public long TargetId { get; set; }
    public string Type { get; set; } = "";

    /// <summary>JSON object.</summary>
    public string Properties { get; set; } = "{}";

    // Reader timeline: text positions where the reader learns the fact, and where
    // the reader learns it was false.
    public int? RevealedPos { get; set; }
    public int? RetractedPos { get; set; }

    // The text that grounds the fact.
    public int? EvidenceStart { get; set; }
    public int? EvidenceEnd { get; set; }

    // Story timeline: when the fact is true, as EDTF text (no parser yet), plus
    // derived ISO-8601 lower/upper bounds. Filled in later passes.
    public string? StoryFromEdtf { get; set; }
    public string? StoryUntilEdtf { get; set; }
    public string? StoryFromLo { get; set; }
    public string? StoryFromHi { get; set; }
    public string? StoryUntilLo { get; set; }
    public string? StoryUntilHi { get; set; }

    /// <summary>Position in story (fabula) order.</summary>
    public double? FabulaSeq { get; set; }

    public Node Source { get; set; } = null!;
    public Node Target { get; set; } = null!;
}

public class Chapter
{
    public int Number { get; set; }
    public string? Title { get; set; }
    public int CharStart { get; set; }
    public int CharEnd { get; set; }

    /// <summary>Who tells the chapter, from the chapter titles ("Dr. Watson", "Dr. Watson (letter)", "Dr. Watson (diary)");
    /// null when the titles name no narrator, and in a book file built before the full schema.</summary>
    public string? Narrator { get; set; }
}

/// <summary>
/// One row of the published timeline, written by <c>index build-db</c> and read once at book load.
/// <see cref="Kind"/> is <c>anchor</c> (the one assumed date every other date is derived from; <see cref="Note"/> states the
/// assumption), <c>chapter</c> (<see cref="RefId"/> = the chapter number) or <c>event</c> (<see cref="RefId"/> = the Event node id).
/// </summary>
public class TimelineRow
{
    public long Id { get; set; }
    public string Kind { get; set; } = "";
    public long? RefId { get; set; }
    public string? Label { get; set; }
    public int? RevealedPos { get; set; }
    public int? RevealedPosEnd { get; set; }
    public string? StoryFromEdtf { get; set; }
    public string? StoryUntilEdtf { get; set; }
    public string? StoryFromLo { get; set; }
    public string? StoryFromHi { get; set; }
    public string? StoryUntilLo { get; set; }
    public string? StoryUntilHi { get; set; }
    public double? FabulaSeq { get; set; }
    public string? Note { get; set; }
}

public static class TimelineKinds
{
    public const string Anchor = "anchor";
    public const string Chapter = "chapter";
    public const string Event = "event";
}

/// <summary>One paragraph of the source text.</summary>
public class Position
{
    public long Id { get; set; }
    public int Chapter { get; set; }
    public int Paragraph { get; set; }
    public int CharStart { get; set; }
    public int CharEnd { get; set; }
    public int? SceneId { get; set; }
}

/// <summary>Relative order of two edges' events: a TimeML TLINK relation, or VAGUE.</summary>
public class EventOrder
{
    public long Id { get; set; }
    public long AEdgeId { get; set; }
    public long BEdgeId { get; set; }
    public string Relation { get; set; } = "";

    public Edge AEdge { get; set; } = null!;
    public Edge BEdge { get; set; } = null!;
}

/// <summary>
/// Allowed <see cref="EventOrder.Relation"/> values: the TLINK relType values of TimeML 1.2.1
/// (https://timeml.github.io/site/publications/timeMLdocs/timeml_1.2.1.html), plus VAGUE.
/// </summary>
public static class TemporalRelations
{
    public const string Vague = "VAGUE";
    public const string Identity = "IDENTITY";

    public static readonly IReadOnlyList<string> All =
    [
        "BEFORE", "AFTER", "INCLUDES", "IS_INCLUDED", "DURING",
        "SIMULTANEOUS", "IAFTER", "IBEFORE", "IDENTITY",
        "BEGINS", "ENDS", "BEGUN_BY", "ENDED_BY", "DURING_INV",
        Vague,
    ];

    /// <summary>
    /// The relations the story-time pass may propose: the TimeML 1.2.1 TLINK relTypes whose meaning is an order or
    /// overlap in time. Left out: IDENTITY, which in TimeML marks two event mentions as the same event (coreference), not a
    /// time relation, and which an earlier model used for "this item repeats that one"; and VAGUE (not a TimeML 1.2.1 relType;
    /// TempEval's "unknown"), since only orderings the text states are recorded. <see cref="All"/>, the database CHECK list,
    /// is unchanged.
    /// </summary>
    public static readonly IReadOnlyList<string> Ordering =
    [
        "BEFORE", "AFTER", "IBEFORE", "IAFTER", "INCLUDES", "IS_INCLUDED", "DURING", "DURING_INV",
        "SIMULTANEOUS", "BEGINS", "BEGUN_BY", "ENDS", "ENDED_BY",
    ];
}

public class Character
{
    public long Id { get; set; }
    public long? NodeId { get; set; }
    public string Name { get; set; } = "";
    public string? ShortName { get; set; }
    public string? Initials { get; set; }

    public Node? Node { get; set; }
}

public class Location
{
    public long Id { get; set; }
    public long? NodeId { get; set; }
    public string Name { get; set; } = "";

    public Node? Node { get; set; }
}
