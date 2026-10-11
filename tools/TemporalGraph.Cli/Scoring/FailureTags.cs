namespace TemporalGraph.Cli.Scoring;

/// <summary>The failure taxonomy: every "type" written to failures.jsonl comes from here.</summary>
public static class FailureTags
{
    // Stage 1 / Stage 2.
    public const string QuoteMismatch = "quote mismatch";
    public const string MergeConflict = "merge conflict";

    // Stage 3 / Stage 4.
    /// <summary>An edge type or node label outside the ontology (or KNOWS, which is Stage 5), or a TLINK relation outside TemporalRelations.All.</summary>
    public const string TypeOutsideOntology = "type outside ontology";
    /// <summary>An edge or link whose entity id is unknown or whose endpoint types don't fit its type; a node with no valid link; an unknown item id.</summary>
    public const string BadEndpoint = "bad endpoint";
    /// <summary>A story time that isn't in the supported EDTF subset (Edtf.TryParse), or ends before it starts.</summary>
    public const string InvalidEdtf = "invalid edtf";
    /// <summary>Stage 4 voting: samples gave dates but none had MinVotes votes; the date is null. Also a facts-scoring tag.</summary>
    public const string UnstableDate = "unstable date";
    /// <summary>Stage 3/4: a sample's reply can't be read as the structured output (cut off, e.g. by the provider's content
    /// filter); the sample is dropped and the others vote.</summary>
    public const string UnreadableReply = "unreadable reply";
    /// <summary>Stage 3 run-wide dedupe: an edge or Fact/Event node removed or merged as a duplicate of an earlier one.</summary>
    public const string Duplicate = "duplicate";

    // Stage 3 on the full schema.
    /// <summary>A Fact with no discovering Event left (none named, an invalid mode, or the event did not have the votes): every Fact needs a DISCOVERED edge.</summary>
    public const string NoDiscovery = "no discovery";
    /// <summary>A retraction of a fact from an earlier chunk that no earlier fact matches; it sets no <c>retracted_pos</c>.</summary>
    public const string UnmatchedRetraction = "unmatched retraction";

    // Facts scoring against facts gold. WrongType ("wrong type") is shared with entity scoring.
    public const string MissedFact = "missed fact";
    public const string SpuriousFact = "spurious fact";
    public const string WrongEndpoint = "wrong endpoint";
    /// <summary>The gold item is a claim (a Fact with a STATES link); the run made it a world fact.</summary>
    public const string StatedAsFact = "stated as fact";
    public const string WrongDate = "wrong date";
    /// <summary>The run dated an item the gold leaves undated.</summary>
    public const string InventedDate = "invented date";

    // Scoring against gold.
    public const string MissedEntity = "missed entity";
    public const string SpuriousEntity = "spurious entity";
    public const string WrongMerge = "wrong merge";
    public const string MissedMerge = "missed merge";
    public const string WrongAlias = "wrong alias";
    public const string WrongType = "wrong type";
    public const string OutOfScope = "out of scope";

    public static readonly IReadOnlyList<string> All =
        [QuoteMismatch, MergeConflict, TypeOutsideOntology, BadEndpoint, InvalidEdtf, UnstableDate, UnreadableReply, Duplicate, NoDiscovery, UnmatchedRetraction, MissedEntity, SpuriousEntity, WrongMerge, MissedMerge, WrongAlias, WrongType, OutOfScope,
         MissedFact, SpuriousFact, WrongEndpoint, StatedAsFact, WrongDate, InventedDate];

    public static readonly IReadOnlyList<string> FactScoring =
        [MissedFact, SpuriousFact, WrongEndpoint, WrongType, StatedAsFact, WrongDate, InventedDate, UnstableDate];

    public static readonly IReadOnlyList<string> Scoring =
        [MissedEntity, SpuriousEntity, WrongMerge, MissedMerge, WrongAlias, WrongType, OutOfScope,
         MissedFact, SpuriousFact, WrongEndpoint, StatedAsFact, WrongDate, InventedDate];
}
