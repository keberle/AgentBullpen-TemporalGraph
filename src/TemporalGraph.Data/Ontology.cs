namespace TemporalGraph.Data;

/// <summary>The approved graph ontology: node labels and edge types. Anything else is rejected by the indexing passes.</summary>
public static class Ontology
{
    public const string Character = "Character";
    public const string Location = "Location";
    public const string Fact = "Fact";
    public const string Event = "Event";

    public static readonly IReadOnlyList<string> Labels = [Character, Location, Fact, Event];

    public const string Met = "MET";
    public const string Knows = "KNOWS";
    public const string About = "ABOUT";
    public const string LocatedAt = "LOCATED_AT";
    /// <summary>Carries a <c>kind</c> property (brother, wife, servant, …).</summary>
    public const string RelatedTo = "RELATED_TO";
    public const string IdenticalTo = "IDENTICAL_TO";
    public const string States = "STATES";
    public const string ParticipatedIn = "PARTICIPATED_IN";

    /// <summary>The edge types of a book indexed before the full schema (<i>The Three Musketeers</i> and <i>Treasure Island</i> until they are re-indexed).</summary>
    public static readonly IReadOnlyList<string> EdgeTypes = [Met, Knows, About, LocatedAt, RelatedTo, IdenticalTo, States, ParticipatedIn];

    /// <summary>Event → Fact, with a <c>how</c> from <see cref="DiscoveryModes"/>.</summary>
    public const string Discovered = "DISCOVERED";
    /// <summary>Character → Fact or Character → Character, with a <c>kind</c> from <see cref="PerceptionKinds"/> and a <c>causeEvent</c>.</summary>
    public const string Perceives = "PERCEIVES";
    /// <summary>A referenced Event → the narrated Event that mentions it.</summary>
    public const string MentionedIn = "MENTIONED_IN";

    /// <summary>The edge types of the full schema. MET, KNOWS, ABOUT and STATES are gone: a meeting is an Event with two participants,
    /// what a character knows is its PERCEIVES edges, a Fact's subjects are a property of the Fact, and a claim is an Event that
    /// DISCOVERED the Fact as <c>told</c> plus the speaker's own PERCEIVES.</summary>
    public static readonly IReadOnlyList<string> GraphEdgeTypes = [ParticipatedIn, Discovered, Perceives, MentionedIn, IdenticalTo, LocatedAt, RelatedTo];
}

/// <summary>An Event's <c>kind</c>.</summary>
public static class EventKinds
{
    /// <summary>The text tells it as it happens.</summary>
    public const string Narrated = "narrated";
    /// <summary>Someone mentions it ("we met yesterday at lunch"): no text of its own, story time earlier than its reveal.</summary>
    public const string Referenced = "referenced";

    public static readonly IReadOnlyList<string> All = [Narrated, Referenced];
}

/// <summary>How an Event's participants came to a Fact (<c>DISCOVERED.how</c>). Final as given; any other value is rejected.</summary>
public static class DiscoveryModes
{
    /// <summary>Present and observed it directly.</summary>
    public const string Witnessed = "witnessed";
    /// <summary>Another character said it to them.</summary>
    public const string Told = "told";
    /// <summary>Heard it without being addressed.</summary>
    public const string Overheard = "overheard";
    /// <summary>Deduced it.</summary>
    public const string Inferred = "inferred";
    /// <summary>A letter, a newspaper, a manuscript.</summary>
    public const string Read = "read";
    /// <summary>The character already knew it and the text reveals that.</summary>
    public const string Recalled = "recalled";

    public static readonly IReadOnlyList<string> All = [Witnessed, Told, Overheard, Inferred, Read, Recalled];
}

/// <summary>
/// The twelve values of <c>PERCEIVES.kind</c>, in four groups. One kind per edge; a character may hold one perception
/// per group of the same target at a time (<c>believes</c> and <c>threatened</c> together are two edges). Final as given.
/// </summary>
public static class PerceptionKinds
{
    public const string Believes = "believes";
    public const string Doubts = "doubts";
    public const string Disbelieves = "disbelieves";
    public const string Trusts = "trusts";
    public const string Distrusts = "distrusts";
    public const string Suspects = "suspects";
    public const string Threatened = "threatened";
    public const string Reassured = "reassured";
    public const string Fearful = "fearful";
    public const string Hopeful = "hopeful";
    public const string Curious = "curious";
    public const string Indifferent = "indifferent";

    public const string Credence = "credence";
    public const string Trust = "trust";
    public const string Threat = "threat";
    public const string Feeling = "feeling";

    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> Groups = new Dictionary<string, IReadOnlyList<string>>
    {
        [Credence] = [Believes, Doubts, Disbelieves],
        [Trust] = [Trusts, Distrusts, Suspects],
        [Threat] = [Threatened, Reassured],
        [Feeling] = [Fearful, Hopeful, Curious, Indifferent],
    };

    public static readonly IReadOnlyList<string> All = [.. Groups.Values.SelectMany(g => g)];

    /// <summary>The group of <paramref name="kind"/>; null for a value outside the enumeration.</summary>
    public static string? GroupOf(string kind) => Groups.FirstOrDefault(g => g.Value.Contains(kind)).Key;
}
