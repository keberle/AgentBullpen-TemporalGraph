using System.Text;
using Microsoft.Extensions.AI;
using TemporalGraph.Cli.Runs;
using TemporalGraph.Cli.Scoring;
using TemporalGraph.Cli.Telemetry;
using TemporalGraph.Data;
using TemporalGraph.Llm;

namespace TemporalGraph.Cli.Indexing;

// Stage 3 — events, facts, perceptions (skills/extract-facts.md from version 3).
// One structured-output call per chunk, k samples, given the chunk, the run's entities, the chapter's narrator and the
// previous chunk's event summaries. The model returns the schema's shape directly:
//   Events (narrated or referenced) with their participants; Facts with their subjects and, per discovering Event, how it
//   was discovered; Perceptions the text supports with a quote; Retractions; Identity reveals; and the two entity edges
//   the schema keeps (RELATED_TO, LOCATED_AT).
// Code checks every item (enumerations, endpoints, the evidence quote in the chunk), the samples vote (Events, Facts and
// the rest need MinVotes; Perceptions need PerceptionMinVotes), chunk overlaps are de-duplicated, and Facts with the same
// subjects and near-identical text collapse into one node whose DISCOVERED edges are the union.
// Stage3.cs is the earlier shape (facts as edges), kept to read the runs of the books not yet re-indexed.

// ---- What the model returns (strict mode: every field required; nullable fields are T|null) ---------------------------

public sealed record ChunkGraph(List<XEvent> Events, List<XFact> Facts, List<XPerception> Perceptions, List<XRetraction> Retractions,
    List<XIdentity> Identities, List<XRelation> Relations);

/// <param name="Id">An id of your own for this answer ("e1", "e2", …), used by the other items.</param>
/// <param name="Kind"><c>narrated</c> or <c>referenced</c>.</param>
/// <param name="Summary">One sentence, naming entities as the list does.</param>
/// <param name="When">The text's own time expression for the event ("the next morning", "three months ago"), verbatim; else null.</param>
/// <param name="Location">The entity id of the place where it happens; else null.</param>
/// <param name="MentionedIn">For a referenced event: the id of the narrated event of this answer in which it is mentioned; else null.</param>
public sealed record XEvent(string Id, string Kind, string Summary, string? When, List<XParticipant> Participants, int? Location, string Evidence, string? MentionedIn);

/// <param name="Role">Free text, a word or two: "speaker", "listener", "narrator", "watched", …; or null.</param>
public sealed record XParticipant(int Character, string? Role);

/// <param name="Id">"f1", "f2", …</param>
/// <param name="Text">One declarative sentence about the world.</param>
/// <param name="Subjects">The entity ids the fact is about.</param>
public sealed record XFact(string Id, string Text, List<int> Subjects, string Evidence, List<XDiscovery> DiscoveredBy, string? SubjectSource = null);

/// <param name="Event">The id of an event of this answer.</param>
/// <param name="How">witnessed, told, overheard, inferred, read or recalled.</param>
public sealed record XDiscovery(string Event, string How);

/// <param name="Fact">The id of a fact of this answer, when the perception is of a fact; else null.</param>
/// <param name="TargetCharacter">The entity id of the character perceived, when the perception is of a character; else null.</param>
/// <param name="Kind">One of the twelve perception kinds.</param>
/// <param name="CauseEvent">The id of the event of this answer that produced the perception; null when it is the fact's discovering event.</param>
public sealed record XPerception(int Character, string? Fact, int? TargetCharacter, string Kind, string? CauseEvent, string Evidence);

/// <param name="Fact">The id of a fact of this answer, when the retracted fact is in this chunk; else null.</param>
/// <param name="Character">The entity id of the character whose belief ends; null when the reader learns the fact itself was false.</param>
/// <param name="Text">The fact that turns out false, in one sentence (as it was believed).</param>
/// <param name="Subjects">The entity ids that fact is about.</param>
public sealed record XRetraction(string? Fact, int? Character, string Text, List<int> Subjects, string Evidence);

public sealed record XIdentity(int A, int B, string Evidence);

/// <param name="Type">RELATED_TO or LOCATED_AT.</param>
/// <param name="Kind">For RELATED_TO: the relation in a word or two; else null.</param>
public sealed record XRelation(string Type, int Source, int Target, string? Kind, string Evidence);

// ---- What the stage keeps (stage-3.json) -----------------------------------------------------------------------------

public sealed record GParticipant(int Character, string? Role);

/// <summary>An Event. Id "E1", "E2", … in book order.</summary>
/// <param name="MentionedIn">A referenced event's narrating event (its id); null for a narrated event.</param>
public sealed record GEvent(string Id, string Kind, string Summary, string? When, IReadOnlyList<GParticipant> Participants, int? Location, int Chunk, int Chapter,
    string Evidence, EvidenceMatch EvidenceMatch, string? MentionedIn, int Votes);

/// <param name="Votes">How many samples named this event as a discoverer of the fact.</param>
/// <param name="Discovery"><c>inferred_by_code</c> when the extraction named no usable discovering event and the link stage gave
/// the fact one (the event whose evidence holds the fact's, else the nearest narrated event); null when the model named it.</param>
public sealed record GDiscovery(string Event, string How, int Votes, string? Discovery = null)
{
    public const string InferredByCode = "inferred_by_code";
}

/// <summary>A Fact. Id "F1", "F2", … in book order.</summary>
/// <param name="AlsoEvidence">The evidence spans of later facts merged into this one by the run-wide dedupe; null when none.</param>
/// <param name="SubjectSource"><c>inferred_pronoun</c> when a subject was written as a pronoun nothing resolved and the link
/// stage gave it by rule (the narrator for a first-person one, else the character last mentioned in the paragraph); null otherwise.</param>
public sealed record GFact(string Id, string Text, IReadOnlyList<int> Subjects, int Chunk, int Chapter, string Evidence, EvidenceMatch EvidenceMatch,
    IReadOnlyList<GDiscovery> Discoveries, int Votes, IReadOnlyList<int[]>? AlsoEvidence = null, string? SubjectSource = null)
{
    public const string InferredPronoun = "inferred_pronoun";
}

/// <summary>A perception the text supports with a quote. Id "P1", …</summary>
/// <param name="Fact">The fact perceived (its id), or null when <paramref name="TargetCharacter"/> is set.</param>
public sealed record GPerception(string Id, int Character, string? Fact, int? TargetCharacter, string Kind, string CauseEvent, int Chunk, int Chapter,
    string Evidence, EvidenceMatch EvidenceMatch, int Votes);

/// <summary>A retraction. Id "X1", … <paramref name="Fact"/> is the fact it was matched to (null: unmatched, logged);
/// <paramref name="Character"/> null means the fact itself is retracted, else that character's perception of it.</summary>
/// <param name="MatchScore">For a fact from an earlier chunk: the text similarity of the match; null when the model named the fact.</param>
public sealed record GRetraction(string Id, string? Fact, int? Character, string Text, IReadOnlyList<int> Subjects, int Chunk, int Chapter,
    string Evidence, EvidenceMatch EvidenceMatch, int Votes, double? MatchScore = null);

public sealed record GIdentity(string Id, int A, int B, int Chunk, int Chapter, string Evidence, EvidenceMatch EvidenceMatch, int Votes);

/// <summary>A RELATED_TO or LOCATED_AT edge between two entities. Id "R1", …</summary>
public sealed record GRelation(string Id, string Type, int Source, int Target, string? Kind, int Chunk, int Chapter, string Evidence, EvidenceMatch EvidenceMatch, int Votes);

/// <param name="Item">"event", "fact", "perception", "retraction", "identity", "relation", "participant", "discovery".</param>
/// <param name="Sample">The sample (0-based); -1 for an outcome of the vote.</param>
public sealed record GraphReject(string Item, string Tag, string Reason, int Chunk, int Chapter, int Sample, string? What);

public sealed record GraphStage3Chunk(int Index, int Chapter, int Unreadable, int Returned, int Events, int Facts, int Perceptions, int Rejected);

/// <param name="Shape">2: the full-schema shape (facts as nodes). A stage-3.json without it is the earlier shape (<see cref="Stage3Result"/>).</param>
public sealed record GraphStage3Result(int Shape, string BookId, int FirstChapter, int LastChapter, double EvidenceThreshold, int Samples, int MinVotes,
    int PerceptionMinVotes, double DedupeThreshold, IReadOnlyList<GraphStage3Chunk> Chunks, IReadOnlyList<GEvent> Events, IReadOnlyList<GFact> Facts,
    IReadOnlyList<GPerception> Perceptions, IReadOnlyList<GRetraction> Retractions, IReadOnlyList<GIdentity> Identities, IReadOnlyList<GRelation> Relations,
    int Rejected, IReadOnlyDictionary<string, int> RejectedByReason, int OverlapDuplicates, int FactsMerged, int RelationsMerged);

/// <summary>Text similarity for the Fact dedupe and the gold soft match: the evidence checker's Smith-Waterman local alignment of the
/// two texts (lower-cased, quotes and whitespace normalized), as a share of the best possible score of the longer text. 1 = the
/// same text; 0.9 is the repo's evidence threshold.</summary>
public static class TextSimilarity
{
    public static double Score(string a, string b)
    {
        var (x, y) = (EvidenceChecker.Normalize(a).Text.ToLowerInvariant(), EvidenceChecker.Normalize(b).Text.ToLowerInvariant());
        if (x.Length == 0 || y.Length == 0) return x.Length == y.Length ? 1 : 0;
        if (x == y) return 1;
        var (shorter, longer) = x.Length <= y.Length ? (x, y) : (y, x);
        return Math.Round(EvidenceChecker.SmithWaterman(shorter, longer).Score / (double)(EvidenceChecker.Match * longer.Length), 4);
    }
}

public static class GraphStage3
{
    public const string StageName = "stage-3";
    public const int Shape = 2;

    /// <summary>The one voting threshold the full schema sets: a Perception needs 3 of the 5 samples.</summary>
    public const int DefaultPerceptionMinVotes = 3;

    /// <summary>Facts with the same subjects and text at least this similar are one node.</summary>
    public const double DefaultDedupeThreshold = 0.9;

    /// <summary>A retraction of a fact from an earlier chunk is matched to the most similar earlier fact that shares a subject, at or above this.</summary>
    public const double RetractionMatchThreshold = 0.5;

    public static readonly IReadOnlyList<string> RelationTypes = [Ontology.RelatedTo, Ontology.LocatedAt];

    private static string Flat(string s) => string.Join(' ', s.Split((char[])[' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries));

    private static bool Overlap(EvidenceMatch a, EvidenceMatch b) => a.CharStart < b.CharEnd && b.CharStart < a.CharEnd;

    /// <summary>The narrator's entity: the Character whose canonical name or an alias is the chapter's narrator (normalized names).</summary>
    public static EntityRef? NarratorEntity(ChapterInfo chapter, IReadOnlyList<EntityRef> entities)
    {
        if (chapter.Narrator is null) return null;
        var key = Stage2.NormalizeName(chapter.Narrator);
        return entities.Where(e => e.Type == MentionType.Character)
            .FirstOrDefault(e => Stage2.NormalizeName(e.Name) == key || e.Aliases.Any(a => Stage2.NormalizeName(a) == key));
    }

    public static string ChunkPrompt(ChunkInfo chunk, ChapterInfo chapter, string chunkText, IReadOnlyList<EntityRef> entities, IReadOnlyList<string> previousEvents)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Chunk {chunk.Index} (chapter {chunk.Chapter}: \"{chapter.Title}\").");
        if (NarratorEntity(chapter, entities) is { } narrator)
            sb.AppendLine($"Narrator of this chapter: {narrator.Name} (entity {narrator.Id})" + (chapter.NarratorForm switch
            {
                "letter" => ", in a letter he or she writes. ",
                "diary" => ", in his or her diary. ",
                _ => ". ",
            }) + $"\"I\" outside quotation marks is entity {narrator.Id}; \"I\" inside a quotation is whoever speaks.");
        else if (chapter.Narrator is not null) sb.AppendLine($"Narrator of this chapter: {chapter.NarratorColumn} (not in the entity list).");
        sb.AppendLine("Entities (id · type · name · other names; \"named here\" lists the names used in this chunk):");
        foreach (var e in entities)
        {
            var here = e.Mentions.Where(m => m.Start >= chunk.CharStart && m.End <= chunk.CharEnd).Select(m => m.Text).Distinct().ToList();
            var line = $"{e.Id} · {e.Type} · {e.Name}";
            if (e.Aliases.Count > 0) line += " · " + string.Join(", ", e.Aliases.Take(8));
            if (here.Count > 0) line += $" · named here: {string.Join(", ", here)}";
            sb.AppendLine(line);
        }
        if (previousEvents.Count > 0)
        {
            sb.AppendLine("Events of the previous chunk, for context only (don't return them again):");
            foreach (var p in previousEvents) sb.AppendLine("- " + p);
        }
        sb.Append($"Return the events, facts, perceptions, retractions, identity reveals and relations in this chunk.\n<<<\n{chunkText}\n>>>");
        return sb.ToString();
    }

    // ---- The code checks on one sample ------------------------------------------------------------------------------

    public sealed record SEvent(string Local, string Kind, string Summary, string? When, List<GParticipant> Participants, int? Location, string Evidence, EvidenceMatch Match, string? MentionedIn);
    /// <param name="Inferred">A discovering event was given by code.</param>
    /// <param name="InferredEvents">Which ones (a fact can keep an event the model named beside one given by code); null: all, when <paramref name="Inferred"/>.</param>
    public sealed record SFact(string Local, string Text, List<int> Subjects, string Evidence, EvidenceMatch Match, List<XDiscovery> Discoveries, bool Inferred = false,
        List<string>? InferredEvents = null, string? SubjectSource = null);
    public sealed record SPerception(int Character, string? Fact, int? TargetCharacter, string Kind, string? Cause, string Evidence, EvidenceMatch Match);
    public sealed record SRetraction(string? Fact, int? Character, string Text, List<int> Subjects, string Evidence, EvidenceMatch Match);
    public sealed record SIdentity(int A, int B, string Evidence, EvidenceMatch Match);
    public sealed record SRelation(string Type, int Source, int Target, string? Kind, string Evidence, EvidenceMatch Match);

    public sealed record CheckedSample(List<SEvent> Events, List<SFact> Facts, List<SPerception> Perceptions, List<SRetraction> Retractions,
        List<SIdentity> Identities, List<SRelation> Relations, List<GraphReject> Rejected);

    /// <summary>
    /// The checks on one sample, in this order for each item: the enumeration (event kind, discovery mode, perception kind, edge
    /// type), the endpoints (known entities of the right type; the ids of events and facts of the same answer), then the evidence
    /// quote in the chunk (exact, or fuzzy at or above <paramref name="threshold"/>). Rules from the schema:
    /// a narrated event needs at least one participant; a referenced event needs the narrated event that mentions it; a fact needs
    /// at least one subject and at least one discovering event with a valid mode; a perception targets exactly one of a fact or
    /// another character. An invalid participant, place or discovery is dropped on its own; the item goes only when nothing valid is left.
    /// </summary>
    /// <param name="inferDiscovery">The link stage: a fact left without a discovering event gets the accepted event whose
    /// evidence span contains the fact's, else the narrated event nearest before it (else the first after); <c>witnessed</c> when
    /// <paramref name="narrator"/> takes part in that event, else <c>told</c>. Only a chunk with no accepted event still drops it.
    /// A discovery that names an event the answer does not have (or one that was not accepted) is given an event and a
    /// mode by the same rule, beside the discoveries the fact keeps, instead of being dropped.</param>
    public static CheckedSample Check(ChunkGraph output, ChunkInfo chunk, string chunkText, IReadOnlyList<EntityRef> entities, double threshold, int sample = 0,
        bool inferDiscovery = false, int? narrator = null)
    {
        var byId = entities.ToDictionary(e => e.Id);
        var result = new CheckedSample([], [], [], [], [], [], []);
        void Reject(string item, string tag, string reason, string? what) => result.Rejected.Add(new GraphReject(item, tag, reason, chunk.Index, chunk.Chapter, sample, what));
        bool IsCharacter(int id) => byId.TryGetValue(id, out var e) && e.Type == MentionType.Character;
        bool IsLocation(int id) => byId.TryGetValue(id, out var e) && e.Type == MentionType.Location;
        EvidenceMatch? Evidence(string item, string? quote, string what)
        {
            var match = EvidenceChecker.Find(quote ?? "", chunkText, chunk.CharStart);
            if (match is null) { Reject(item, FailureTags.QuoteMismatch, "empty evidence", what); return null; }
            if (match.Score < threshold) { Reject(item, FailureTags.QuoteMismatch, $"evidence not in chunk (best {match.Method} score {match.Score:0.###} < {threshold})", what); return null; }
            return match;
        }

        // Events: the narrated ones first, so a referenced event can name its narrating event.
        foreach (var e in (output.Events ?? []).OrderBy(e => e.Kind == EventKinds.Referenced))
        {
            var what = Flat(e.Summary ?? "");
            if (!EventKinds.All.Contains(e.Kind)) { Reject("event", FailureTags.TypeOutsideOntology, $"event kind '{e.Kind}' is not narrated or referenced", what); continue; }
            if (string.IsNullOrWhiteSpace(e.Summary) || string.IsNullOrWhiteSpace(e.Id)) { Reject("event", FailureTags.TypeOutsideOntology, "an event needs an id and a summary", what); continue; }
            if (result.Events.Any(x => x.Local == e.Id)) { Reject("event", FailureTags.BadEndpoint, $"the id '{e.Id}' is used twice", what); continue; }
            var participants = new List<GParticipant>();
            foreach (var p in (e.Participants ?? []).DistinctBy(p => p.Character))
            {
                if (IsCharacter(p.Character)) participants.Add(new GParticipant(p.Character, string.IsNullOrWhiteSpace(p.Role) ? null : p.Role.Trim()));
                else Reject("participant", FailureTags.BadEndpoint, $"participant {p.Character} is not a character in the list", what);
            }
            if (e.Kind == EventKinds.Narrated && participants.Count == 0) { Reject("event", FailureTags.BadEndpoint, "a narrated event needs at least one participant", what); continue; }
            var location = e.Location;
            if (location is { } loc && !IsLocation(loc)) { Reject("participant", FailureTags.BadEndpoint, $"location {loc} is not a place in the list", what); location = null; }
            string? narrating = null;
            if (e.Kind == EventKinds.Referenced)
            {
                narrating = result.Events.FirstOrDefault(x => x.Local == e.MentionedIn && x.Kind == EventKinds.Narrated)?.Local;
                if (narrating is null) { Reject("event", FailureTags.BadEndpoint, $"a referenced event needs the narrated event that mentions it ('{e.MentionedIn}' is not one)", what); continue; }
            }
            if (Evidence("event", e.Evidence, what) is not { } match) continue;
            result.Events.Add(new SEvent(e.Id, e.Kind, e.Summary.Trim(), string.IsNullOrWhiteSpace(e.When) ? null : e.When.Trim(), participants, location, e.Evidence, match, narrating));
        }
        var events = result.Events.Select(e => e.Local).ToHashSet();

        foreach (var f in output.Facts ?? [])
        {
            var what = Flat(f.Text ?? "");
            if (string.IsNullOrWhiteSpace(f.Text) || string.IsNullOrWhiteSpace(f.Id)) { Reject("fact", FailureTags.TypeOutsideOntology, "a fact needs an id and a text", what); continue; }
            if (result.Facts.Any(x => x.Local == f.Id)) { Reject("fact", FailureTags.BadEndpoint, $"the id '{f.Id}' is used twice", what); continue; }
            var subjects = (f.Subjects ?? []).Distinct().Where(byId.ContainsKey).Order().ToList();
            if (subjects.Count == 0) { Reject("fact", FailureTags.BadEndpoint, "no subject in the entity list", what); continue; }
            var discoveries = new List<XDiscovery>();
            var dangling = new List<XDiscovery>();
            foreach (var d in (f.DiscoveredBy ?? []).DistinctBy(d => d.Event))
            {
                if (!DiscoveryModes.All.Contains(d.How)) Reject("discovery", FailureTags.TypeOutsideOntology, $"discovery mode '{d.How}' is not one of {string.Join(", ", DiscoveryModes.All)}", what);
                else if (!events.Contains(d.Event))
                {
                    if (inferDiscovery) dangling.Add(d);
                    else Reject("discovery", FailureTags.BadEndpoint, $"discovering event '{d.Event}' is not an accepted event of this answer", what);
                }
                else discoveries.Add(d);
            }
            var inferredEvents = new List<string>();
            if (discoveries.Count == 0 || dangling.Count > 0)
            {
                var at = inferDiscovery ? EvidenceChecker.Find(f.Evidence ?? "", chunkText, chunk.CharStart) : null;
                var by = at is null || at.Score < threshold ? null
                    : result.Events.Where(e => e.Match.CharStart <= at.CharStart && e.Match.CharEnd >= at.CharEnd).OrderBy(e => e.Kind == EventKinds.Referenced).ThenBy(e => e.Match.CharEnd - e.Match.CharStart).FirstOrDefault()
                        ?? result.Events.Where(e => e.Kind == EventKinds.Narrated && e.Match.CharStart <= at.CharStart).OrderByDescending(e => e.Match.CharStart).FirstOrDefault()
                        ?? result.Events.Where(e => e.Kind == EventKinds.Narrated).OrderBy(e => e.Match.CharStart).FirstOrDefault();
                if (by is null && discoveries.Count == 0) { Reject("fact", FailureTags.NoDiscovery, "no discovering event with a valid mode", what); continue; }
                if (by is not null && discoveries.All(d => d.Event != by.Local))
                {
                    discoveries.Add(new XDiscovery(by.Local, narrator is { } teller && by.Participants.Any(p => p.Character == teller) ? "witnessed" : "told"));
                    inferredEvents.Add(by.Local);
                }
            }
            if (Evidence("fact", f.Evidence, what) is not { } match) continue;
            result.Facts.Add(new SFact(f.Id, f.Text.Trim(), subjects, f.Evidence, match, discoveries, inferredEvents.Count > 0, inferredEvents, f.SubjectSource));
        }
        var facts = result.Facts.ToDictionary(f => f.Local);

        foreach (var p in output.Perceptions ?? [])
        {
            var what = $"{p.Character} {p.Kind} {p.Fact ?? p.TargetCharacter?.ToString()}";
            if (!PerceptionKinds.All.Contains(p.Kind)) { Reject("perception", FailureTags.TypeOutsideOntology, $"perception kind '{p.Kind}' is not one of the twelve", what); continue; }
            if (!IsCharacter(p.Character)) { Reject("perception", FailureTags.BadEndpoint, $"{p.Character} is not a character in the list", what); continue; }
            var hasFact = !string.IsNullOrWhiteSpace(p.Fact);
            // The item's shape, not an endpoint: the schema has one target, a fact or a character.
            if (hasFact == (p.TargetCharacter is not null)) { Reject("perception", FailureTags.TypeOutsideOntology, "a perception targets exactly one of a fact or a character", what); continue; }
            if (hasFact && !facts.ContainsKey(p.Fact!)) { Reject("perception", FailureTags.BadEndpoint, $"fact '{p.Fact}' is not an accepted fact of this answer", what); continue; }
            if (p.TargetCharacter is { } target && (!IsCharacter(target) || target == p.Character)) { Reject("perception", FailureTags.BadEndpoint, $"target {target} is not another character in the list", what); continue; }
            var cause = p.CauseEvent is not null && events.Contains(p.CauseEvent) ? p.CauseEvent : hasFact ? facts[p.Fact!].Discoveries[0].Event : null;
            if (cause is null) { Reject("perception", FailureTags.BadEndpoint, "a perception of a character needs the event that produced it", what); continue; }
            if (Evidence("perception", p.Evidence, what) is not { } match) continue;
            result.Perceptions.Add(new SPerception(p.Character, hasFact ? p.Fact : null, p.TargetCharacter, p.Kind, cause, p.Evidence, match));
        }

        foreach (var r in output.Retractions ?? [])
        {
            var what = Flat(r.Text ?? "");
            var local = string.IsNullOrWhiteSpace(r.Fact) ? null : r.Fact;
            if (local is not null && !facts.ContainsKey(local)) { Reject("retraction", FailureTags.BadEndpoint, $"fact '{r.Fact}' is not an accepted fact of this answer", what); continue; }
            if (local is null && string.IsNullOrWhiteSpace(r.Text)) { Reject("retraction", FailureTags.BadEndpoint, "a retraction needs the fact's id or its text", what); continue; }
            if (r.Character is { } who && !IsCharacter(who)) { Reject("retraction", FailureTags.BadEndpoint, $"{who} is not a character in the list", what); continue; }
            if (Evidence("retraction", r.Evidence, what) is not { } match) continue;
            var subjects = local is not null ? facts[local].Subjects : (r.Subjects ?? []).Distinct().Where(byId.ContainsKey).Order().ToList();
            result.Retractions.Add(new SRetraction(local, r.Character, local is not null ? facts[local].Text : r.Text.Trim(), subjects, r.Evidence, match));
        }

        foreach (var i in output.Identities ?? [])
        {
            var what = $"{i.A} = {i.B}";
            // The two names already are one character (Stage 2 merged them, or the link stage pointed both at one): nothing to join, and no endpoint is wrong.
            if (i.A == i.B && IsCharacter(i.A)) { Reject("identity", FailureTags.Duplicate, "both names already are one character: nothing to join", what); continue; }
            if (!IsCharacter(i.A) || !IsCharacter(i.B) || i.A == i.B) { Reject("identity", FailureTags.BadEndpoint, "an identity reveal joins two different characters in the list", what); continue; }
            if (Evidence("identity", i.Evidence, what) is not { } match) continue;
            result.Identities.Add(new SIdentity(Math.Min(i.A, i.B), Math.Max(i.A, i.B), i.Evidence, match));
        }

        foreach (var r in output.Relations ?? [])
        {
            var what = $"{r.Type} {r.Source} → {r.Target}";
            if (!RelationTypes.Contains(r.Type)) { Reject("relation", FailureTags.TypeOutsideOntology, $"'{r.Type}' is not RELATED_TO or LOCATED_AT", what); continue; }
            if (r.Type == Ontology.RelatedTo && string.IsNullOrWhiteSpace(r.Kind)) { Reject("relation", FailureTags.TypeOutsideOntology, "RELATED_TO needs a kind", what); continue; }
            if (r.Source == r.Target && byId.ContainsKey(r.Source)) { Reject("relation", FailureTags.Duplicate, "both names are one entity: a relation needs two", what); continue; }
            if (!byId.ContainsKey(r.Source) || !byId.ContainsKey(r.Target) || r.Source == r.Target) { Reject("relation", FailureTags.BadEndpoint, "unknown or identical endpoints", what); continue; }
            if (r.Type == Ontology.RelatedTo && (!IsCharacter(r.Source) || !IsCharacter(r.Target))) { Reject("relation", FailureTags.BadEndpoint, "RELATED_TO links two characters", what); continue; }
            if (r.Type == Ontology.LocatedAt && !IsLocation(r.Target)) { Reject("relation", FailureTags.BadEndpoint, "LOCATED_AT needs a place as its target", what); continue; }
            if (Evidence("relation", r.Evidence, what) is not { } match) continue;
            result.Relations.Add(new SRelation(r.Type, r.Source, r.Target, r.Type == Ontology.RelatedTo ? r.Kind!.Trim() : null, r.Evidence, match));
        }
        return result;
    }

    // ---- The vote over one chunk's samples --------------------------------------------------------------------------

    /// <summary>One chunk's voted items; ids are chunk-scoped ("12:e1") until <see cref="Assemble"/> numbers them.</summary>
    public sealed record ChunkItems(List<GEvent> Events, List<GFact> Facts, List<GPerception> Perceptions, List<GRetraction> Retractions,
        List<GIdentity> Identities, List<GRelation> Relations, List<GraphReject> Rejected);

    /// <summary>Two events of different samples are the same event when they have the same kind and their evidence overlaps, or
    /// when they have the same participants and summaries that share at least half their words.</summary>
    public static bool SameEvent(SEvent a, SEvent b) =>
        a.Kind == b.Kind && (Overlap(a.Match, b.Match) ||
            (a.Participants.Select(p => p.Character).ToHashSet().SetEquals(b.Participants.Select(p => p.Character)) && RunDedupe.Jaccard(a.Summary, b.Summary) >= 0.5));

    /// <summary>Two facts are the same fact when they have the same subjects and overlapping evidence (the wording is ignored).</summary>
    public static bool SameFact(SFact a, SFact b) => a.Subjects.SequenceEqual(b.Subjects) && Overlap(a.Match, b.Match);

    private static List<List<(int Sample, T Item)>> Clusters<T>(IReadOnlyList<IEnumerable<T>> samples, Func<T, T, bool> same)
    {
        var clusters = new List<List<(int Sample, T Item)>>();
        for (var s = 0; s < samples.Count; s++)
            foreach (var item in samples[s])
            {
                var target = clusters.FirstOrDefault(c => c.All(x => x.Sample != s) && c.Any(x => same(x.Item, item)));
                if (target is null) clusters.Add([(s, item)]);
                else target.Add((s, item));
            }
        return clusters;
    }

    // The member whose evidence span is the most common one (ties: the earliest sample).
    private static T Representative<T>(List<(int Sample, T Item)> c, Func<T, EvidenceMatch> match) =>
        c.GroupBy(x => (match(x.Item).CharStart, match(x.Item).CharEnd)).OrderByDescending(g => g.Count()).ThenBy(g => g.Min(x => x.Sample)).First().OrderBy(x => x.Sample).First().Item;

    /// <summary>
    /// Self-consistency voting over one chunk's checked samples. Events, facts, retractions, identity reveals and relations need
    /// <paramref name="minVotes"/> samples; perceptions need <paramref name="perceptionMinVotes"/>. An item is its cluster's most
    /// common evidence span. Then the links are settled among what was kept:
    /// <list type="bullet">
    /// <item>an event's participants are those at least half of its samples name (none left: the representative's);</item>
    /// <item>a referenced event keeps the narrated event most of its samples name, and is dropped when that event was not kept;</item>
    /// <item>a fact's discoveries are the kept events at least <paramref name="minVotes"/> of its samples name (none: the one named
    /// most), each with its most common mode; a fact with no kept discovering event is dropped (<c>no discovery</c>);</item>
    /// <item>a perception's cause is the kept event most of its samples name, else the fact's first discovery; else it is dropped.</item>
    /// </list>
    /// </summary>
    public static ChunkItems Vote(IReadOnlyList<CheckedSample> samples, ChunkInfo chunk, int minVotes, int perceptionMinVotes)
    {
        var rejected = new List<GraphReject>();
        void Drop(string item, string tag, string reason, string what) => rejected.Add(new GraphReject(item, tag, reason, chunk.Index, chunk.Chapter, -1, what));
        string Id(char prefix, int n) => $"{chunk.Index}:{prefix}{n}";

        // Events.
        var eventClusters = Clusters(samples.Select(s => (IEnumerable<SEvent>)s.Events.OrderBy(e => e.Match.CharStart)).ToList(), SameEvent);
        var eventOf = new Dictionary<(int Sample, string Local), int>();
        for (var c = 0; c < eventClusters.Count; c++) foreach (var (s, e) in eventClusters[c]) eventOf[(s, e.Local)] = c;
        var keptEvents = new Dictionary<int, GEvent>();
        foreach (var c in Enumerable.Range(0, eventClusters.Count).Where(c => eventClusters[c].Count >= minVotes && eventClusters[c][0].Item.Kind == EventKinds.Narrated))
        {
            var members = eventClusters[c];
            var rep = Representative(members, e => e.Match);
            var participants = members.SelectMany(m => m.Item.Participants).GroupBy(p => p.Character)
                .Where(g => g.Count() * 2 >= members.Count)
                .Select(g => new GParticipant(g.Key, g.Select(p => p.Role).FirstOrDefault(r => r is not null))).OrderBy(p => p.Character).ToList();
            if (participants.Count == 0) participants = rep.Participants;
            var location = members.Select(m => m.Item.Location).Where(l => l is not null).GroupBy(l => l).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
            keptEvents[c] = new GEvent(Id('e', c), rep.Kind, rep.Summary, rep.When ?? members.Select(m => m.Item.When).FirstOrDefault(w => w is not null), participants, location,
                chunk.Index, chunk.Chapter, rep.Evidence, rep.Match, null, members.Count);
        }
        foreach (var c in Enumerable.Range(0, eventClusters.Count).Where(c => eventClusters[c].Count >= minVotes && eventClusters[c][0].Item.Kind == EventKinds.Referenced))
        {
            var members = eventClusters[c];
            var rep = Representative(members, e => e.Match);
            var narrating = members.Select(m => eventOf.GetValueOrDefault((m.Sample, m.Item.MentionedIn!), -1)).Where(keptEvents.ContainsKey)
                .GroupBy(n => n).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).Select(g => (int?)g.Key).FirstOrDefault();
            if (narrating is null) { Drop("event", FailureTags.BadEndpoint, "referenced event: the narrated event that mentions it did not have the votes", rep.Summary); continue; }
            var participants = members.SelectMany(m => m.Item.Participants).GroupBy(p => p.Character).Where(g => g.Count() * 2 >= members.Count)
                .Select(g => new GParticipant(g.Key, g.Select(p => p.Role).FirstOrDefault(r => r is not null))).OrderBy(p => p.Character).ToList();
            var location = members.Select(m => m.Item.Location).Where(l => l is not null).GroupBy(l => l).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
            keptEvents[c] = new GEvent(Id('e', c), rep.Kind, rep.Summary, rep.When ?? members.Select(m => m.Item.When).FirstOrDefault(w => w is not null), participants, location,
                chunk.Index, chunk.Chapter, rep.Evidence, rep.Match, keptEvents[narrating.Value].Id, members.Count);
        }

        // Facts.
        var factClusters = Clusters(samples.Select(s => (IEnumerable<SFact>)s.Facts.OrderBy(f => f.Match.CharStart)).ToList(), SameFact);
        var factOf = new Dictionary<(int Sample, string Local), int>();
        for (var c = 0; c < factClusters.Count; c++) foreach (var (s, f) in factClusters[c]) factOf[(s, f.Local)] = c;
        var keptFacts = new Dictionary<int, GFact>();
        foreach (var c in Enumerable.Range(0, factClusters.Count).Where(c => factClusters[c].Count >= minVotes))
        {
            var members = factClusters[c];
            var rep = Representative(members, f => f.Match);
            var named = members.SelectMany(m => m.Item.Discoveries.Select(d => (Event: eventOf.GetValueOrDefault((m.Sample, d.Event), -1), d.How, m.Sample, Inferred: m.Item.InferredEvents is { } given ? given.Contains(d.Event) : m.Item.Inferred)))
                .Where(d => keptEvents.ContainsKey(d.Event)).GroupBy(d => d.Event)
                .Select(g => new GDiscovery(keptEvents[g.Key].Id, g.GroupBy(d => d.How).OrderByDescending(h => h.Count()).ThenBy(h => DiscoveryModes.All.ToList().IndexOf(h.Key)).First().Key,
                    g.Select(d => d.Sample).Distinct().Count(), g.All(d => d.Inferred) ? GDiscovery.InferredByCode : null))
                .OrderByDescending(d => d.Votes).ThenBy(d => d.Event, StringComparer.Ordinal).ToList();
            if (named.Count == 0) { Drop("fact", FailureTags.NoDiscovery, "none of its discovering events had the votes", rep.Text); continue; }
            var discoveries = named.Where(d => d.Votes >= minVotes).ToList();
            if (discoveries.Count == 0) discoveries = [named[0]];
            keptFacts[c] = new GFact(Id('f', c), rep.Text, rep.Subjects, chunk.Index, chunk.Chapter, rep.Evidence, rep.Match, discoveries, members.Count, SubjectSource: rep.SubjectSource);
        }

        // Perceptions: the same character, kind and target.
        var perceptions = new List<GPerception>();
        var perceived = samples.SelectMany((s, i) => s.Perceptions.Select(p => (Sample: i, Item: p,
                Target: p.Fact is not null ? (factOf.TryGetValue((i, p.Fact), out var fc) && keptFacts.ContainsKey(fc) ? $"f{fc}" : null) : $"c{p.TargetCharacter}")))
            .Where(x => x.Target is not null).GroupBy(x => (x.Item.Character, x.Item.Kind, x.Target));
        var n = 0;
        foreach (var g in perceived.OrderBy(g => g.Min(x => x.Item.Match.CharStart)))
        {
            var votes = g.Select(x => x.Sample).Distinct().Count();
            if (votes < perceptionMinVotes) continue;
            var rep = Representative(g.Select(x => (x.Sample, x.Item)).ToList(), p => p.Match);
            var fact = g.Key.Target!.StartsWith('f') ? keptFacts[int.Parse(g.Key.Target[1..])] : null;
            var cause = g.Select(x => eventOf.GetValueOrDefault((x.Sample, x.Item.Cause!), -1)).Where(keptEvents.ContainsKey).GroupBy(c => c)
                .OrderByDescending(c => c.Count()).ThenBy(c => c.Key).Select(c => keptEvents[c.Key].Id).FirstOrDefault() ?? fact?.Discoveries[0].Event;
            if (cause is null) { Drop("perception", FailureTags.BadEndpoint, "its cause event did not have the votes", $"{g.Key.Character} {g.Key.Kind} {g.Key.Target}"); continue; }
            perceptions.Add(new GPerception(Id('p', n++), g.Key.Character, fact?.Id, fact is null ? rep.TargetCharacter : null, g.Key.Kind, cause, chunk.Index, chunk.Chapter,
                rep.Evidence, rep.Match, votes));
        }

        // Retractions: of a fact of this chunk (the same fact and character), or of an earlier fact (the same character, overlapping evidence).
        var retractions = new List<GRetraction>();
        var retracted = samples.SelectMany((s, i) => s.Retractions.Select(r => (Sample: i, Item: r,
            Fact: r.Fact is not null ? (factOf.TryGetValue((i, r.Fact), out var fc) && keptFacts.ContainsKey(fc) ? (int?)fc : -1) : null))).Where(x => x.Fact != -1).ToList();
        var retractionClusters = Clusters<(int Sample, SRetraction Item, int? Fact)>(
            Enumerable.Range(0, samples.Count).Select(i => retracted.Where(x => x.Sample == i).OrderBy(x => x.Item.Match.CharStart).AsEnumerable()).ToList(),
            (a, b) => a.Item.Character == b.Item.Character && (a.Fact is not null || b.Fact is not null ? a.Fact == b.Fact : Overlap(a.Item.Match, b.Item.Match)));
        foreach (var c in retractionClusters.Where(c => c.Count >= minVotes))
        {
            var rep = Representative(c.Select(x => (x.Sample, x.Item.Item)).ToList(), r => r.Match);
            var fact = c[0].Item.Fact is { } fc ? keptFacts[fc] : null;
            retractions.Add(new GRetraction(Id('x', retractions.Count), fact?.Id, rep.Character, fact?.Text ?? rep.Text, fact?.Subjects ?? rep.Subjects, chunk.Index, chunk.Chapter,
                rep.Evidence, rep.Match, c.Count));
        }

        var identities = samples.SelectMany((s, i) => s.Identities.Select(x => (Sample: i, Item: x))).GroupBy(x => (x.Item.A, x.Item.B))
            .Where(g => g.Select(x => x.Sample).Distinct().Count() >= minVotes)
            .Select((g, i) => (g, rep: Representative(g.ToList(), x => x.Match), i))
            .Select(x => new GIdentity(Id('i', x.i), x.g.Key.A, x.g.Key.B, chunk.Index, chunk.Chapter, x.rep.Evidence, x.rep.Match, x.g.Select(y => y.Sample).Distinct().Count())).ToList();

        var relationClusters = Clusters(samples.Select(s => (IEnumerable<SRelation>)s.Relations.OrderBy(r => r.Match.CharStart)).ToList(),
            (a, b) => a.Type == b.Type && a.Source == b.Source && a.Target == b.Target && Overlap(a.Match, b.Match));
        var relations = relationClusters.Where(c => c.Count >= minVotes).Select((c, i) =>
        {
            var rep = Representative(c, r => r.Match);
            var kind = c.Select(x => x.Item.Kind).GroupBy(k => k?.ToLowerInvariant()).OrderByDescending(g => g.Count()).First().First();
            return new GRelation(Id('r', i), rep.Type, rep.Source, rep.Target, kind, chunk.Index, chunk.Chapter, rep.Evidence, rep.Match, c.Count);
        }).ToList();

        return new ChunkItems(keptEvents.Values.OrderBy(e => e.EvidenceMatch.CharStart).ToList(), keptFacts.Values.OrderBy(f => f.EvidenceMatch.CharStart).ToList(),
            perceptions, retractions, identities, relations, rejected);
    }

    // ---- The run: chunk overlaps, the fact dedupe, numbering --------------------------------------------------------

    public sealed record Assembled(List<GEvent> Events, List<GFact> Facts, List<GPerception> Perceptions, List<GRetraction> Retractions, List<GIdentity> Identities,
        List<GRelation> Relations, int OverlapDuplicates, int FactsMerged, int RelationsMerged, List<GraphReject> Rejected, List<(string Kept, string Dropped, double Score)> Merges);

    /// <summary>
    /// The run's items from its chunks' voted items:
    /// <list type="number">
    /// <item><b>Chunk overlaps.</b> Chunks overlap, so the next chunk can return an item again: an event (the same kind) or a fact
    /// (the same subjects) whose evidence overlaps one kept from an earlier chunk is that item; a fact's discoveries are added to it.</item>
    /// <item><b>Fact dedupe.</b> Two facts with the same subjects and text similarity (<see cref="TextSimilarity"/>) at or above
    /// <paramref name="dedupeThreshold"/> are one node: the earlier one, with the union of their DISCOVERED edges (one per event)
    /// and the later evidence in <see cref="GFact.AlsoEvidence"/>.</item>
    /// <item><b>Edges.</b> RELATED_TO once per pair of characters and kind, LOCATED_AT once per source and target, an identity
    /// reveal once per pair; the earliest is kept. A perception repeated in an overlap (the same character, kind and target, with
    /// overlapping evidence) is dropped.</item>
    /// <item><b>Retractions.</b> One of a fact from an earlier chunk is matched to the most similar fact revealed before it that
    /// shares a subject (similarity at or above <see cref="RetractionMatchThreshold"/>); unmatched ones stay, with no fact.</item>
    /// </list>
    /// Ids are then assigned in book order: E, F, P, X, I, R.
    /// </summary>
    public static Assembled Assemble(IReadOnlyList<ChunkItems> chunks, double dedupeThreshold)
    {
        var alias = new Dictionary<string, string>();
        string Resolve(string id) { while (alias.TryGetValue(id, out var to)) id = to; return id; }
        var rejected = new List<GraphReject>();
        var merges = new List<(string, string, double)>();
        var overlaps = 0;

        var events = new List<GEvent>();
        foreach (var e in chunks.SelectMany(c => c.Events).OrderBy(e => e.Chunk).ThenBy(e => e.EvidenceMatch.CharStart))
        {
            if (events.FirstOrDefault(k => k.Chunk != e.Chunk && k.Kind == e.Kind && Overlap(k.EvidenceMatch, e.EvidenceMatch)) is { } kept) { alias[e.Id] = kept.Id; overlaps++; }
            else events.Add(e);
        }
        events = events.Select(e => e.MentionedIn is null ? e : e with { MentionedIn = Resolve(e.MentionedIn) }).ToList();

        static List<GDiscovery> Union(IEnumerable<GDiscovery> all) =>
            all.GroupBy(d => d.Event).Select(g => g.OrderByDescending(d => d.Votes).First()).ToList();

        var facts = new List<GFact>();
        var factsMerged = 0;
        foreach (var raw in chunks.SelectMany(c => c.Facts).OrderBy(f => f.EvidenceMatch.CharStart).ThenBy(f => f.Chunk))
        {
            var f = raw with { Discoveries = Union(raw.Discoveries.Select(d => d with { Event = Resolve(d.Event) })) };
            var i = facts.FindIndex(k => k.Chunk != f.Chunk && k.Subjects.SequenceEqual(f.Subjects) && Overlap(k.EvidenceMatch, f.EvidenceMatch));
            var score = 0.0;
            if (i >= 0) overlaps++;
            else
            {
                i = facts.FindIndex(k => k.Subjects.SequenceEqual(f.Subjects) && (score = TextSimilarity.Score(k.Text, f.Text)) >= dedupeThreshold);
                if (i >= 0)
                {
                    factsMerged++;
                    merges.Add((facts[i].Id, f.Id, score));
                    facts[i] = facts[i] with { AlsoEvidence = (facts[i].AlsoEvidence ?? []).Append([f.EvidenceMatch.CharStart, f.EvidenceMatch.CharEnd]).ToList() };
                }
            }
            if (i < 0) { facts.Add(f); continue; }
            alias[f.Id] = facts[i].Id;
            facts[i] = facts[i] with { Discoveries = Union(facts[i].Discoveries.Concat(f.Discoveries)) };
        }

        var perceptions = new List<GPerception>();
        foreach (var raw in chunks.SelectMany(c => c.Perceptions).OrderBy(p => p.EvidenceMatch.CharStart).ThenBy(p => p.Chunk))
        {
            var p = raw with { Fact = raw.Fact is null ? null : Resolve(raw.Fact), CauseEvent = Resolve(raw.CauseEvent) };
            if (perceptions.Any(k => k.Chunk != p.Chunk && k.Character == p.Character && k.Kind == p.Kind && k.Fact == p.Fact && k.TargetCharacter == p.TargetCharacter
                    && Overlap(k.EvidenceMatch, p.EvidenceMatch))) { overlaps++; continue; }
            perceptions.Add(p);
        }

        static (int, int) Pair(GRelation r) => r.Source <= r.Target ? (r.Source, r.Target) : (r.Target, r.Source);
        var relations = new List<GRelation>();
        var relationsMerged = 0;
        foreach (var r in chunks.SelectMany(c => c.Relations).OrderBy(r => r.EvidenceMatch.CharStart).ThenBy(r => r.Chunk))
        {
            var same = r.Type == Ontology.RelatedTo
                ? relations.Any(k => k.Type == r.Type && Pair(k) == Pair(r) && RunDedupe.SameKind(RunDedupe.NormalizeKind(k.Kind), RunDedupe.NormalizeKind(r.Kind)))
                : relations.Any(k => k.Type == r.Type && k.Source == r.Source && k.Target == r.Target);
            if (same) relationsMerged++;
            else relations.Add(r);
        }
        var identities = chunks.SelectMany(c => c.Identities).OrderBy(i => i.EvidenceMatch.CharStart).GroupBy(i => (i.A, i.B)).Select(g => g.First()).ToList();

        var retractions = new List<GRetraction>();
        foreach (var raw in chunks.SelectMany(c => c.Retractions).OrderBy(r => r.EvidenceMatch.CharStart).ThenBy(r => r.Chunk))
        {
            var r = raw.Fact is null ? raw : raw with { Fact = Resolve(raw.Fact) };
            if (r.Fact is null)
            {
                var best = facts.Where(f => f.EvidenceMatch.CharStart < r.EvidenceMatch.CharStart && (r.Subjects.Count == 0 || f.Subjects.Intersect(r.Subjects).Any()))
                    .Select(f => (f, Score: Math.Max(TextSimilarity.Score(f.Text, r.Text), RunDedupe.Jaccard(f.Text, r.Text))))
                    .OrderByDescending(x => x.Score).ThenBy(x => x.f.EvidenceMatch.CharStart).FirstOrDefault();
                if (best.f is not null && best.Score >= RetractionMatchThreshold) r = r with { Fact = best.f.Id, MatchScore = Math.Round(best.Score, 4) };
                else rejected.Add(new GraphReject("retraction", FailureTags.UnmatchedRetraction, $"no earlier fact about the same subjects is similar enough (best {best.Score:0.###})", r.Chunk, r.Chapter, -1, r.Text));
            }
            if (retractions.Any(k => k.Fact is not null && k.Fact == r.Fact && k.Character == r.Character)) continue;
            retractions.Add(r);
        }

        // Numbering in book order, and the references re-pointed.
        var newId = new Dictionary<string, string>();
        var numberedEvents = events.OrderBy(e => e.EvidenceMatch.CharStart).ThenBy(e => e.Kind == EventKinds.Referenced).ThenBy(e => e.Id, StringComparer.Ordinal).ToList();
        for (var i = 0; i < numberedEvents.Count; i++) newId[numberedEvents[i].Id] = $"E{i + 1}";
        var numberedFacts = facts.OrderBy(f => f.EvidenceMatch.CharStart).ThenBy(f => f.Id, StringComparer.Ordinal).ToList();
        for (var i = 0; i < numberedFacts.Count; i++) newId[numberedFacts[i].Id] = $"F{i + 1}";
        return new Assembled(
            numberedEvents.Select(e => e with { Id = newId[e.Id], MentionedIn = e.MentionedIn is null ? null : newId[e.MentionedIn] }).ToList(),
            numberedFacts.Select(f => f with { Id = newId[f.Id], Discoveries = f.Discoveries.Select(d => d with { Event = newId[d.Event] }).OrderBy(d => int.Parse(d.Event[1..])).ToList() }).ToList(),
            perceptions.Select((p, i) => p with { Id = $"P{i + 1}", Fact = p.Fact is null ? null : newId[p.Fact], CauseEvent = newId[p.CauseEvent] }).ToList(),
            retractions.Select((r, i) => r with { Id = $"X{i + 1}", Fact = r.Fact is null ? null : newId[r.Fact] }).ToList(),
            identities.Select((x, i) => x with { Id = $"I{i + 1}" }).ToList(),
            relations.Select((r, i) => r with { Id = $"R{i + 1}" }).ToList(),
            overlaps, factsMerged, relationsMerged, rejected, merges.Select(m => (newId[m.Item1], m.Item2, m.Item3)).ToList());
    }

    public static async Task<GraphStage3Result> RunAsync(IChatClient client, LlmCallContext? context, SkillFile skill, Stage0Result stage0, string bookText,
        IReadOnlyList<EntityRef> entities, int firstChapter, int lastChapter, double threshold, RunFolder? run, CancellationToken ct, VotingSettings voting,
        int perceptionMinVotes = DefaultPerceptionMinVotes, double dedupeThreshold = DefaultDedupeThreshold, Action<int, int>? progress = null)
    {
        if (voting.Validate() is [var problem, ..]) throw new ArgumentException(problem.Replace("Stage1", "Stage3"), nameof(voting));
        if (perceptionMinVotes < 1 || perceptionMinVotes > voting.Samples) throw new ArgumentException($"Indexing:Stage3:PerceptionMinVotes must be between 1 and Samples ({voting.Samples}).", nameof(perceptionMinVotes));
        var chunks = stage0.Chunks.Where(c => c.Chapter >= firstChapter && c.Chapter <= lastChapter).ToList();
        var summaries = new List<GraphStage3Chunk>();
        var voted = new List<ChunkItems>();
        var rejected = new List<GraphReject>();
        IReadOnlyList<string> previous = [];
        var previousChapter = -1;

        foreach (var chunk in chunks)
        {
            using var span = IndexingTelemetry.Source.StartActivity($"chunk {chunk.Index}");
            span?.SetTag(IndexingTelemetry.RunId, run?.RunId);
            span?.SetTag(IndexingTelemetry.Stage, StageName);
            span?.SetTag(IndexingTelemetry.BookId, stage0.BookId);
            span?.SetTag(IndexingTelemetry.Chapter, chunk.Chapter);
            span?.SetTag(IndexingTelemetry.ChunkIndex, chunk.Index);
            span?.SetTag(IndexingTelemetry.SkillName, skill.Name);
            span?.SetTag(IndexingTelemetry.SkillVersion, skill.Version);
            if (context is not null)
            {
                context.BookId = stage0.BookId;
                context.Chapter = chunk.Chapter;
                context.ChunkIndex = chunk.Index;
                context.SkillName = skill.Name;
                context.SkillVersion = skill.Version.ToString();
            }
            var chapter = stage0.Chapters.Single(c => c.Number == chunk.Chapter);
            if (chunk.Chapter != previousChapter) previous = [];
            previousChapter = chunk.Chapter;
            var chunkText = bookText[chunk.CharStart..chunk.CharEnd];
            List<ChatMessage> messages = [new(ChatRole.System, skill.Text), new(ChatRole.User, ChunkPrompt(chunk, chapter, chunkText, entities, previous))];
            // k independent samples of the same request, in parallel. A reply that can't be read drops that sample; the others vote.
            var replies = await Task.WhenAll(Enumerable.Range(0, voting.Samples).Select(async _ =>
            {
                try { return ((ChunkGraph?)(await client.GetResponseAsync<ChunkGraph>(messages, Stage1.Json, Stage1.StrictOptions(), useJsonSchemaResponseFormat: true, ct)).Result, (string?)null); }
                catch (System.Text.Json.JsonException ex) { return (null, ex.Message); }
            }));
            foreach (var (_, error) in replies.Where(r => r.Item1 is null))
                run?.AppendFailure(new { type = FailureTags.UnreadableReply, stage = StageName, runId = run.RunId, bookId = stage0.BookId, chapter = chunk.Chapter, chunkIndex = chunk.Index, reason = error, traceId = span?.TraceId.ToString() });
            var outputs = replies.Where(r => r.Item1 is not null).Select(r => r.Item1!).ToList();
            var samples = outputs.Select((o, s) => Check(o, chunk, chunkText, entities, threshold, s)).ToList();
            var items = Vote(samples, chunk, voting.MinVotes, perceptionMinVotes);
            voted.Add(items);
            var chunkRejected = samples.SelectMany(s => s.Rejected).Concat(items.Rejected).ToList();
            rejected.AddRange(chunkRejected);
            foreach (var r in chunkRejected)
                run?.AppendFailure(new { type = r.Tag, stage = StageName, runId = run.RunId, bookId = stage0.BookId, chapter = r.Chapter, chunkIndex = r.Chunk, item = r.Item, sample = r.Sample, reason = r.Reason, what = r.What, traceId = span?.TraceId.ToString() });
            previous = items.Events.Select(e => $"{e.Summary}{(e.Kind == EventKinds.Referenced ? " (mentioned, not narrated)" : "")}").ToList();
            var returned = outputs.Sum(o => (o.Events?.Count ?? 0) + (o.Facts?.Count ?? 0) + (o.Perceptions?.Count ?? 0) + (o.Retractions?.Count ?? 0) + (o.Identities?.Count ?? 0) + (o.Relations?.Count ?? 0));
            summaries.Add(new GraphStage3Chunk(chunk.Index, chunk.Chapter, replies.Length - outputs.Count, returned, items.Events.Count, items.Facts.Count, items.Perceptions.Count, chunkRejected.Count));
            span?.SetTag("events.accepted", items.Events.Count);
            span?.SetTag("facts.accepted", items.Facts.Count);
            span?.SetTag("perceptions.accepted", items.Perceptions.Count);
            span?.SetTag("items.rejected", chunkRejected.Count);
            span?.SetTag("samples.unreadable", replies.Length - outputs.Count);
            progress?.Invoke(summaries.Count, chunks.Count);
        }

        var a = Assemble(voted, dedupeThreshold);
        rejected.AddRange(a.Rejected);
        if (run is not null)
        {
            foreach (var r in a.Rejected)
                run.AppendFailure(new { type = r.Tag, stage = StageName, runId = run.RunId, bookId = stage0.BookId, chapter = r.Chapter, chunkIndex = r.Chunk, item = r.Item, reason = r.Reason, what = r.What });
            foreach (var (kept, dropped, score) in a.Merges)
                run.AppendFailure(new { type = FailureTags.Duplicate, stage = StageName, runId = run.RunId, bookId = stage0.BookId, item = "fact", rule = "same subjects, text similarity at or above the threshold", kept, dropped, similarity = score });
        }
        return new GraphStage3Result(Shape, stage0.BookId, firstChapter, lastChapter, threshold, voting.Samples, voting.MinVotes, perceptionMinVotes, dedupeThreshold,
            summaries, a.Events, a.Facts, a.Perceptions, a.Retractions, a.Identities, a.Relations, rejected.Count,
            rejected.GroupBy(r => r.Tag).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count()), a.OverlapDuplicates, a.FactsMerged, a.RelationsMerged);
    }

    /// <summary>Counts for stage-3.json and the console.</summary>
    public static Dictionary<string, object> Counts(GraphStage3Result r) => new()
    {
        ["chunks"] = r.Chunks.Count,
        ["returned"] = r.Chunks.Sum(c => c.Returned),
        ["events"] = r.Events.Count,
        ["eventsByKind"] = EventKinds.All.ToDictionary(k => k, k => r.Events.Count(e => e.Kind == k)),
        ["participations"] = r.Events.Sum(e => e.Participants.Count),
        ["facts"] = r.Facts.Count,
        ["discoveries"] = r.Facts.Sum(f => f.Discoveries.Count),
        ["discoveriesByHow"] = DiscoveryModes.All.ToDictionary(h => h, h => r.Facts.Sum(f => f.Discoveries.Count(d => d.How == h))),
        ["perceptions"] = r.Perceptions.Count,
        ["perceptionsByKind"] = PerceptionKinds.All.ToDictionary(k => k, k => r.Perceptions.Count(p => p.Kind == k)),
        ["retractions"] = r.Retractions.Count,
        ["retractionsMatched"] = r.Retractions.Count(x => x.Fact is not null),
        ["identities"] = r.Identities.Count,
        ["relationsByType"] = RelationTypes.ToDictionary(t => t, t => r.Relations.Count(x => x.Type == t)),
        ["rejected"] = r.Rejected,
        ["rejectedByReason"] = r.RejectedByReason,
        ["overlapDuplicates"] = r.OverlapDuplicates,
        ["factsMerged"] = r.FactsMerged,
        ["relationsMerged"] = r.RelationsMerged,
        ["unreadableReplies"] = r.Chunks.Sum(c => c.Unreadable),
        ["samples"] = r.Samples,
        ["minVotes"] = r.MinVotes,
        ["perceptionMinVotes"] = r.PerceptionMinVotes,
        ["dedupeThreshold"] = r.DedupeThreshold,
    };
}
