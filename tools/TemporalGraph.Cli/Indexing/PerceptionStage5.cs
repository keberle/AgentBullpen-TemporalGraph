using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using TemporalGraph.Cli.Runs;
using TemporalGraph.Cli.Scoring;
using TemporalGraph.Cli.Telemetry;
using TemporalGraph.Data;
using TemporalGraph.Llm;

namespace TemporalGraph.Cli.Indexing;

// Stage 5 — perception seeding and closing (skills/revise-perceptions.md).
// Chapter by chapter, in book order:
//   1. Code, no model. The perceptions Stage 3 extracted (the text supports them with a quote) come first and override a seed
//      for the same character and target. Then every DISCOVERED edge seeds a credence perception for each participant of its
//      Event: `believes` for witnessed, read and recalled; for told and overheard, `believes` unless the character has an open
//      `distrusts` or `suspects` edge toward the speaker, then `doubts` (the cascade: the perception of the person the fact
//      came from). The speaker is a participant whose role names them as the source; without one, every other participant.
//   2. One voted call per chapter: given the perceptions held and the chapter's Events, which perceptions change and why?
//      Each change needs an evidence quote in the chapter and becomes a new edge.
// Closing, by code: a later edge for the same character, target and group closes the one before it: the earlier edge's
// story_until is the new edge's story_from, and its retracted_pos is the new edge's revealed_pos. A kind is never changed
// in place. What a character knows is then the Facts it has a PERCEIVES edge to (there is no KNOWS stage).

/// <summary>Structured output of one Stage 5 chapter call. Strict mode: every field required; nullable fields are T|null.</summary>
public sealed record PerceptionChanges(List<XChange> Changes);

/// <param name="Character">The entity id of the character whose perception changes.</param>
/// <param name="Fact">The id (F…) of the fact perceived; null when <paramref name="TargetCharacter"/> is set.</param>
/// <param name="TargetCharacter">The entity id of the character perceived; null when <paramref name="Fact"/> is set.</param>
/// <param name="Kind">The new perception kind, one of the twelve.</param>
/// <param name="CauseEvent">The id (E…) of the event of this chapter that changes it.</param>
/// <param name="Evidence">A verbatim quote from the chapter that shows the change.</param>
/// <param name="Why">One line.</param>
public sealed record XChange(int Character, string? Fact, int? TargetCharacter, string Kind, string CauseEvent, string Evidence, string Why);

/// <summary>A PERCEIVES edge with both clocks. Id "Q1", … in the order created.</summary>
/// <param name="Source"><c>extracted</c> (Stage 3, with a quote), <c>seeded</c> (code, from a DISCOVERED edge) or <c>revised</c> (the chapter pass).</param>
/// <param name="How">For a seeded edge: the discovery mode it was seeded from.</param>
/// <param name="ClosedBy">The id of the later edge that closed this one; null while open.</param>
public sealed record PerceptionEdge(string Id, int Character, string? Fact, int? TargetCharacter, string Kind, string CauseEvent, string Source, string? How,
    int RevealedPos, int? RetractedPos, int EvidenceStart, int EvidenceEnd, string? Evidence, int Chapter, StoryDate From, StoryDate? Until, double? FabulaSeq,
    string? ClosedBy = null, int Votes = 0, string? Why = null)
{
    public string Group => PerceptionKinds.GroupOf(Kind)!;
    public string Target => Fact ?? $"c{TargetCharacter}";
}

/// <summary>A Fact the reader learns was false: its <c>retracted_pos</c> (from a Stage 3 retraction with no character).</summary>
public sealed record FactRetraction(string Fact, int RetractedPos, string Retraction, string Evidence);

public sealed record PerceptionStage5Chapter(int Chapter, int Extracted, int Seeded, int Listed, int Proposed, int Revised, int Unreadable, int Rejected);

public sealed record PerceptionStage5Result(int Shape, string BookId, IReadOnlyList<PerceptionStage5Chapter> Chapters, IReadOnlyList<PerceptionEdge> Edges,
    IReadOnlyList<FactRetraction> FactRetractions, int Samples, int MinVotes, int Closed, int PerceptionRetractions, int Rejected, IReadOnlyDictionary<string, int> RejectedByReason);

public static partial class PerceptionStage5
{
    public const string StageName = "stage-5";
    public const int Shape = 2;

    /// <summary>The chapter pass (k = 5, m = 3).</summary>
    public const int DefaultSamples = 5;
    public const int DefaultMinVotes = 3;

    /// <summary>The most perceptions listed in one chapter prompt (the latest revealed are kept).</summary>
    public const int MaxListed = 250;

    // A participant's role that names them as where the fact came from.
    [GeneratedRegex(@"speak|tell|told|say|said|source|report|narrat|writ|author|inform|reveal|explain|confess|claim|read|announc|describ|recount|relat|deduc|infer|reason|conclud", RegexOptions.IgnoreCase)]
    private static partial Regex SourceRole();

    private static string Flat(string s) => string.Join(' ', s.Split((char[])[' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries));
    private static string Cut(string s, int max) => Flat(s) is var f && f.Length > max ? f[..(max - 1)].TrimEnd() + "…" : Flat(s);

    /// <summary>The participants a told or overheard fact came from: those whose role names them as the source; none: every participant
    /// (each is then a possible source for the others).</summary>
    public static IReadOnlyList<int> Sources(GEvent e)
    {
        var named = e.Participants.Where(p => p.Role is not null && SourceRole().IsMatch(p.Role)).Select(p => p.Character).ToList();
        return named.Count > 0 && named.Count < e.Participants.Count ? named : e.Participants.Select(p => p.Character).ToList();
    }

    /// <summary>The state of the edges so far: the latest edge (by story time, then by where it is told) per character, target and group.</summary>
    public sealed class Ledger
    {
        private readonly List<PerceptionEdge> _edges = [];
        private int _issued;
        public IReadOnlyList<PerceptionEdge> Edges => _edges;

        private static int Order(PerceptionEdge a, PerceptionEdge b) =>
            string.CompareOrdinal(a.From.Lo, b.From.Lo) is var c and not 0 ? c : a.RevealedPos.CompareTo(b.RevealedPos);

        /// <summary>The edge that holds for (character, target, group) at the end of everything added so far; null when none.</summary>
        public PerceptionEdge? Current(int character, string target, string group) =>
            _edges.Where(e => e.Character == character && e.Target == target && e.Group == group).OrderBy(e => e, Comparer<PerceptionEdge>.Create(Order)).LastOrDefault();

        /// <summary>The edge that holds for (character, target, group) at story time <paramref name="storyLo"/> (ISO date); null when none.</summary>
        public PerceptionEdge? At(int character, string target, string group, string? storyLo) =>
            _edges.Where(e => e.Character == character && e.Target == target && e.Group == group && string.CompareOrdinal(e.From.Lo, storyLo) <= 0)
                .OrderBy(e => e, Comparer<PerceptionEdge>.Create(Order)).LastOrDefault();

        /// <summary>Adds the edge unless it changes nothing: the edge it would follow (same character, target and group, at its story
        /// time) already has its kind and is told no later. Returns the added edge, or null.</summary>
        public PerceptionEdge? Add(PerceptionEdge edge)
        {
            var before = At(edge.Character, edge.Target, edge.Group, edge.From.Lo);
            if (before is not null && before.Kind == edge.Kind && before.RevealedPos <= edge.RevealedPos) return null;
            // A running number, not the count: a removed seed must not hand its id to the next edge.
            var added = edge with { Id = $"Q{++_issued}" };
            _edges.Add(added);
            return added;
        }

        /// <summary>Drops a seeded edge (an extracted or revised one for the same character and target overrides it).</summary>
        public void Remove(PerceptionEdge edge) => _edges.Remove(edge);

        /// <summary>
        /// The closing: for each character, target and group, in story order (then the order told), an edge followed by one of
        /// another kind is closed by it: <c>story_until</c> = the next edge's <c>story_from</c>, <c>retracted_pos</c> = the next edge's
        /// <c>revealed_pos</c> (only when that is after its own reveal: a reader can't unlearn before learning). Ids are renumbered.
        /// </summary>
        public (List<PerceptionEdge> Edges, int Closed) Close()
        {
            var closed = 0;
            var result = new List<PerceptionEdge>();
            var renumber = _edges.OrderBy(e => e.RevealedPos).ThenBy(e => e.Id.Length).ThenBy(e => e.Id, StringComparer.Ordinal).Select((e, i) => (e.Id, New: $"Q{i + 1}")).ToDictionary(x => x.Id, x => x.New);
            foreach (var sequence in _edges.GroupBy(e => (e.Character, e.Target, e.Group)))
            {
                var ordered = sequence.OrderBy(e => e, Comparer<PerceptionEdge>.Create(Order)).ToList();
                for (var i = 0; i < ordered.Count; i++)
                {
                    var next = ordered.Skip(i + 1).FirstOrDefault(n => n.Kind != ordered[i].Kind);
                    if (next is null) { result.Add(ordered[i]); continue; }
                    closed++;
                    result.Add(ordered[i] with
                    {
                        Until = next.From,
                        RetractedPos = next.RevealedPos > ordered[i].RevealedPos ? next.RevealedPos : ordered[i].RetractedPos,
                        ClosedBy = renumber[next.Id],
                    });
                }
            }
            return (result.Select(e => e with { Id = renumber[e.Id] }).OrderBy(e => int.Parse(e.Id[1..])).ToList(), closed);
        }
    }

    /// <summary>
    /// The seeds of one DISCOVERED edge (the code pass): one credence perception per participant of the discovering Event.
    /// <c>witnessed</c>, <c>read</c>, <c>recalled</c>: <c>believes</c>. <c>told</c>, <c>overheard</c>: the speaker (see
    /// <see cref="Sources"/>) <c>believes</c> what they say; a listener <c>believes</c> unless they hold <c>distrusts</c> or
    /// <c>suspects</c> toward a speaker at that story time, then <c>doubts</c>. <c>inferred</c> is seeded as told: the one who
    /// reasons it out believes it, the others weigh it by their trust in that person.
    /// </summary>
    public static IEnumerable<(int Character, string Kind)> Seeds(GEvent e, string how, Func<int, int, bool> distrusts)
    {
        if (how is DiscoveryModes.Witnessed or DiscoveryModes.Read or DiscoveryModes.Recalled)
        {
            foreach (var p in e.Participants) yield return (p.Character, PerceptionKinds.Believes);
            yield break;
        }
        var sources = Sources(e);
        var allAreSources = sources.Count == e.Participants.Count;
        foreach (var p in e.Participants)
        {
            if (!allAreSources && sources.Contains(p.Character)) { yield return (p.Character, PerceptionKinds.Believes); continue; }
            var wary = sources.Any(s => s != p.Character && distrusts(p.Character, s));
            yield return (p.Character, wary ? PerceptionKinds.Doubts : PerceptionKinds.Believes);
        }
    }

    /// <param name="withText">False: the chapter's events and the perceptions held, without the chapter text.</param>
    public static string ChapterPrompt(ChapterInfo chapter, string chapterText, IReadOnlyList<GEvent> events, IReadOnlyList<PerceptionEdge> held, int omitted,
        IReadOnlyDictionary<int, EntityRef> entities, IReadOnlyDictionary<string, GFact> facts, bool withText = true)
    {
        string Name(int id) => entities.TryGetValue(id, out var e) ? $"{e.Name} ({id})" : $"#{id}";
        var sb = new StringBuilder();
        sb.AppendLine($"Chapter {chapter.Number} (\"{chapter.Title}\"){(chapter.Narrator is null ? "" : $", told by {chapter.NarratorColumn}")}.");
        sb.AppendLine("Events of this chapter (id · kind · participants · summary):");
        foreach (var e in events)
            sb.AppendLine($"- {e.Id} · {e.Kind} · {(e.Participants.Count == 0 ? "—" : string.Join(", ", e.Participants.Select(p => Name(p.Character))))} · \"{Cut(e.Summary, 160)}\"");
        sb.AppendLine("Perceptions held as the chapter's events happen (character · kind · target · since chapter):");
        foreach (var p in held)
            sb.AppendLine($"- {Name(p.Character)} · {p.Kind} · {(p.Fact is { } f ? $"{f} \"{Cut(facts[f].Text, 140)}\"" : Name(p.TargetCharacter!.Value))} · ch. {p.Chapter}");
        if (omitted > 0) sb.AppendLine($"({omitted} earlier perceptions about other matters are not listed.)");
        sb.Append("Which of these perceptions change in this chapter, and why? Include a new perception of one character about another that this chapter's events produce.");
        if (withText) sb.Append("\n<<<\n").Append(chapterText).Append("\n>>>");
        else sb.Append(" You have the events, not the text: name the event that causes each change, and leave the evidence empty.");
        return sb.ToString();
    }

    /// <summary>One checked change of a sample.</summary>
    public sealed record SChange(int Character, string? Fact, int? TargetCharacter, string Kind, string CauseEvent, string Evidence, EvidenceMatch Match, string Why);

    /// <summary>
    /// The checks on one sample of the chapter pass: the kind is one of the twelve; the character is a Character; the target is
    /// exactly one of a known Fact revealed by the end of the chapter or another Character; the cause is an Event of this chapter;
    /// the quote is in the chapter text (exact, or fuzzy at or above <paramref name="threshold"/>).
    /// </summary>
    public static (List<SChange> Changes, List<(string Tag, string Reason, string What)> Rejected) Check(PerceptionChanges output, ChapterInfo chapter, string chapterText,
        IReadOnlyDictionary<int, EntityRef> entities, IReadOnlyDictionary<string, GFact> facts, IReadOnlySet<string> chapterEvents, double threshold,
        IReadOnlyDictionary<string, GEvent>? causeEvidence = null)
    {
        var changes = new List<SChange>();
        var rejected = new List<(string, string, string)>();
        bool IsCharacter(int id) => entities.TryGetValue(id, out var e) && e.Type == MentionType.Character;
        foreach (var c in output.Changes ?? [])
        {
            var what = $"{c.Character} {c.Kind} {c.Fact ?? c.TargetCharacter?.ToString()}";
            if (!PerceptionKinds.All.Contains(c.Kind)) { rejected.Add((FailureTags.TypeOutsideOntology, $"perception kind '{c.Kind}' is not one of the twelve", what)); continue; }
            if (!IsCharacter(c.Character)) { rejected.Add((FailureTags.BadEndpoint, $"{c.Character} is not a character", what)); continue; }
            var fact = string.IsNullOrWhiteSpace(c.Fact) ? null : c.Fact.Trim();
            if ((fact is not null) == (c.TargetCharacter is not null)) { rejected.Add((FailureTags.BadEndpoint, "a perception targets exactly one of a fact or a character", what)); continue; }
            if (fact is not null && (!facts.TryGetValue(fact, out var f) || f.EvidenceMatch.CharStart >= chapter.CharEnd)) { rejected.Add((FailureTags.BadEndpoint, $"'{fact}' is not a fact known by the end of the chapter", what)); continue; }
            if (c.TargetCharacter is { } t && (!IsCharacter(t) || t == c.Character)) { rejected.Add((FailureTags.BadEndpoint, $"target {t} is not another character", what)); continue; }
            if (c.CauseEvent is null || !chapterEvents.Contains(c.CauseEvent.Trim())) { rejected.Add((FailureTags.BadEndpoint, $"cause '{c.CauseEvent}' is not an event of this chapter", what)); continue; }
            // Without the text the change rests on its cause event, whose evidence was checked when it was extracted.
            if (causeEvidence is not null)
            {
                var cause = causeEvidence[c.CauseEvent.Trim()];
                changes.Add(new SChange(c.Character, fact, c.TargetCharacter, c.Kind, c.CauseEvent.Trim(), cause.Evidence, cause.EvidenceMatch, Flat(c.Why ?? "")));
                continue;
            }
            var match = EvidenceChecker.Find(c.Evidence ?? "", chapterText, chapter.CharStart);
            if (match is null || match.Score < threshold) { rejected.Add((FailureTags.QuoteMismatch, $"evidence not in the chapter (best score {match?.Score ?? 0:0.###} < {threshold})", what)); continue; }
            changes.Add(new SChange(c.Character, fact, c.TargetCharacter, c.Kind, c.CauseEvent.Trim(), c.Evidence!, match, Flat(c.Why ?? "")));
        }
        return (changes, rejected);
    }

    public static async Task<PerceptionStage5Result> RunAsync(IChatClient? client, LlmCallContext? context, SkillFile? skill, Stage0Result stage0, string bookText,
        IReadOnlyList<EntityRef> entityList, GraphStage3Result stage3, TimelineStage4Result stage4, double threshold, RunFolder? run, CancellationToken ct,
        VotingSettings voting, Action<int, int>? progress = null, bool withText = true, CacheHints? cache = null)
    {
        var entities = entityList.ToDictionary(e => e.Id);
        var events = stage3.Events.ToDictionary(e => e.Id);
        var facts = stage3.Facts.ToDictionary(f => f.Id);
        var dates = stage4.Items.ToDictionary(i => i.Id);
        var ledger = new Ledger();
        var rejected = new List<(string Tag, string Reason, string What, int Chapter)>();
        var chapters = new List<PerceptionStage5Chapter>();
        var numbers = Enumerable.Range(stage3.FirstChapter, stage3.LastChapter - stage3.FirstChapter + 1).ToList();

        PerceptionEdge Edge(int character, string? fact, int? target, string kind, string cause, string source, string? how, int revealed, int evidenceStart, int evidenceEnd,
            string? evidence, int chapter, int votes = 0, string? why = null) =>
            new("", character, fact, target, kind, cause, source, how, revealed, null, evidenceStart, evidenceEnd, evidence, chapter, dates[cause].From, null, dates[cause].FabulaSeq, null, votes, why);

        foreach (var number in numbers)
        {
            var chapter = stage0.Chapters.Single(c => c.Number == number);
            var chapterEvents = stage3.Events.Where(e => e.Chapter == number).OrderBy(e => e.EvidenceMatch.CharStart).ToList();

            // 1a. What the text itself says of a perception (Stage 3).
            var extracted = 0;
            foreach (var p in stage3.Perceptions.Where(p => p.Chapter == number).OrderBy(p => p.EvidenceMatch.CharStart))
            {
                var edge = Edge(p.Character, p.Fact, p.TargetCharacter, p.Kind, p.CauseEvent, "extracted", null, p.EvidenceMatch.CharStart, p.EvidenceMatch.CharStart, p.EvidenceMatch.CharEnd,
                    p.Evidence, number, p.Votes);
                if (ledger.Add(edge) is not null) extracted++;
            }

            // 1b. The seeds: this chapter's DISCOVERED edges, in story order.
            var seeded = 0;
            var discoveries = stage3.Facts.SelectMany(f => f.Discoveries.Select(d => (Fact: f, Discovery: d, Event: events[d.Event]))).Where(x => x.Event.Chapter == number)
                .OrderBy(x => dates[x.Event.Id].From.Lo, StringComparer.Ordinal).ThenBy(x => dates[x.Event.Id].FabulaSeq).ThenBy(x => x.Fact.EvidenceMatch.CharStart).ToList();
            foreach (var (fact, discovery, e) in discoveries)
            {
                var storyLo = dates[e.Id].From.Lo;
                bool Distrusts(int who, int whom) => ledger.At(who, $"c{whom}", PerceptionKinds.Trust, storyLo)?.Kind is PerceptionKinds.Distrusts or PerceptionKinds.Suspects;
                var revealed = Math.Max(fact.EvidenceMatch.CharStart, e.EvidenceMatch.CharStart);
                foreach (var (character, kind) in Seeds(e, discovery.How, Distrusts))
                {
                    // A perception the text gives for this character and fact (credence group) stands instead of the seed.
                    if (ledger.Edges.Any(x => x.Source != "seeded" && x.Character == character && x.Fact == fact.Id && x.Group == PerceptionKinds.Credence
                            && x.CauseEvent == e.Id)) continue;
                    if (ledger.Add(Edge(character, fact.Id, null, kind, e.Id, "seeded", discovery.How, revealed, fact.EvidenceMatch.CharStart, fact.EvidenceMatch.CharEnd, null, number)) is not null)
                        seeded++;
                }
            }

            // 2. The chapter pass: which perceptions change.
            int listed = 0, proposed = 0, revised = 0, unreadable = 0, chapterRejected = 0;
            if (client is not null && skill is not null && chapterEvents.Count > 0)
            {
                using var span = IndexingTelemetry.Source.StartActivity($"chapter {number}");
                span?.SetTag(IndexingTelemetry.RunId, run?.RunId);
                span?.SetTag(IndexingTelemetry.Stage, StageName);
                span?.SetTag(IndexingTelemetry.BookId, stage0.BookId);
                span?.SetTag(IndexingTelemetry.Chapter, number);
                if (context is not null)
                {
                    context.BookId = stage0.BookId;
                    context.Chapter = number;
                    context.ChunkIndex = null;
                    context.SkillName = skill.Name;
                    context.SkillVersion = skill.Version.ToString();
                }
                // Listed: what this chapter's participants hold, about what the chapter touches (the subjects of the facts it
                // discovers, and its participants), the latest first up to the cap.
                var present = chapterEvents.SelectMany(e => e.Participants.Select(p => p.Character)).ToHashSet();
                var touched = discoveries.SelectMany(x => x.Fact.Subjects).Concat(present).ToHashSet();
                var current = ledger.Edges.Where(e => present.Contains(e.Character)).GroupBy(e => (e.Character, e.Target, e.Group))
                    .Select(g => ledger.Current(g.Key.Character, g.Key.Target, g.Key.Group)!)
                    .Where(e => e.Fact is { } f ? facts[f].Subjects.Any(touched.Contains) : touched.Contains(e.TargetCharacter!.Value))
                    .OrderByDescending(e => e.RevealedPos).ToList();
                var held = current.Take(MaxListed).OrderBy(e => e.Character).ThenBy(e => e.RevealedPos).ToList();
                listed = held.Count;
                var chapterText = bookText[chapter.CharStart..chapter.CharEnd];
                List<ChatMessage> messages = [new(ChatRole.System, skill.Text), new(ChatRole.User, ChapterPrompt(chapter, chapterText, chapterEvents, held, current.Count - held.Count, entities, facts, withText))];
                var replies = await Task.WhenAll(Enumerable.Range(0, voting.Samples).Select(async _ =>
                {
                    using var cached = PromptCache.Use(cache);
                    using var place = LlmCallContext.At(number, null);
                    try { return (await client.GetResponseAsync<PerceptionChanges>(messages, Stage1.Json, Stage1.StrictOptions(), useJsonSchemaResponseFormat: true, ct)).Result; }
                    catch (System.Text.Json.JsonException) { return null; }
                }));
                unreadable = replies.Count(r => r is null);
                if (unreadable > 0)
                    run?.AppendFailure(new { type = FailureTags.UnreadableReply, stage = StageName, runId = run.RunId, bookId = stage0.BookId, chapter = number, samples = unreadable });
                var eventIds = chapterEvents.Select(e => e.Id).ToHashSet();
                var samples = replies.OfType<PerceptionChanges>().Select(o => Check(o, chapter, chapterText, entities, facts, eventIds, threshold, withText ? null : events)).ToList();
                proposed = samples.Sum(s => s.Changes.Count);
                foreach (var (tag, reason, what) in samples.SelectMany(s => s.Rejected)) { rejected.Add((tag, reason, what, number)); chapterRejected++; }
                foreach (var g in samples.SelectMany((s, i) => s.Changes.Select(c => (Sample: i, c))).GroupBy(x => (x.c.Character, Target: x.c.Fact ?? $"c{x.c.TargetCharacter}", x.c.Kind))
                             .OrderBy(g => g.Min(x => x.c.Match.CharStart)))
                {
                    var votes = g.Select(x => x.Sample).Distinct().Count();
                    if (votes < voting.MinVotes) continue;
                    var rep = g.GroupBy(x => (x.c.Match.CharStart, x.c.Match.CharEnd)).OrderByDescending(x => x.Count()).ThenBy(x => x.Min(y => y.Sample)).First().OrderBy(x => x.Sample).First().c;
                    var cause = g.GroupBy(x => x.c.CauseEvent).OrderByDescending(x => x.Count()).ThenBy(x => x.Key, StringComparer.Ordinal).First().Key;
                    var edge = Edge(rep.Character, rep.Fact, rep.TargetCharacter, rep.Kind, cause, "revised", null, rep.Match.CharStart, rep.Match.CharStart, rep.Match.CharEnd, rep.Evidence, number, votes, rep.Why);
                    // The text's own word on this character and target replaces a seed made from the same event.
                    foreach (var seed in ledger.Edges.Where(x => x.Source == "seeded" && x.Character == edge.Character && x.Target == edge.Target && x.Group == edge.Group && x.CauseEvent == cause).ToList())
                        ledger.Remove(seed);
                    if (ledger.Add(edge) is not null) revised++;
                    else { rejected.Add((FailureTags.Duplicate, "no change: the character already holds this perception", $"{rep.Character} {rep.Kind} {rep.Fact ?? rep.TargetCharacter?.ToString()}", number)); chapterRejected++; }
                }
                span?.SetTag("perceptions.listed", listed);
                span?.SetTag("perceptions.revised", revised);
            }
            chapters.Add(new PerceptionStage5Chapter(number, extracted, seeded, listed, proposed, revised, unreadable, chapterRejected));
            progress?.Invoke(chapters.Count, numbers.Count);
        }

        // Stage 3 retractions. Of a Fact (no character): the Fact's retracted_pos. Of one character's belief: that edge ends there.
        var (edges, closed) = ledger.Close();
        var factRetractions = new List<FactRetraction>();
        var perceptionRetractions = 0;
        var narratedByPos = stage3.Events.Where(e => e.Kind == EventKinds.Narrated).OrderBy(e => e.EvidenceMatch.CharStart).ToList();
        foreach (var x in stage3.Retractions.Where(x => x.Fact is not null).OrderBy(x => x.EvidenceMatch.CharStart))
        {
            var fact = facts[x.Fact!];
            // A retraction can stand before its claim in the same passage ("But one false statement was made…"): it then takes effect as
            // soon as the claim is revealed.
            var pos = Math.Max(x.EvidenceMatch.CharStart, fact.EvidenceMatch.CharStart + 1);
            if (x.Character is null)
            {
                if (factRetractions.All(r => r.Fact != fact.Id)) factRetractions.Add(new FactRetraction(fact.Id, pos, x.Id, x.Evidence));
                continue;
            }
            var when = narratedByPos.LastOrDefault(e => e.EvidenceMatch.CharStart <= pos) is { } at ? dates[at.Id].From : null;
            var i = edges.FindIndex(e => e.Character == x.Character && e.Fact == fact.Id && e.Group == PerceptionKinds.Credence && e.RetractedPos is null && e.RevealedPos < pos);
            if (i < 0) continue;
            edges[i] = edges[i] with { RetractedPos = pos, Until = edges[i].Until ?? when, ClosedBy = edges[i].ClosedBy ?? x.Id };
            perceptionRetractions++;
        }

        foreach (var (tag, reason, what, chapter) in rejected)
            run?.AppendFailure(new { type = tag, stage = StageName, runId = run.RunId, bookId = stage0.BookId, chapter, reason, what });
        return new PerceptionStage5Result(Shape, stage0.BookId, chapters, edges, factRetractions, voting.Samples, voting.MinVotes, closed, perceptionRetractions, rejected.Count,
            rejected.GroupBy(r => r.Tag).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count()));
    }

    /// <summary>Counts for stage-5.json and the console.</summary>
    public static Dictionary<string, object> Counts(PerceptionStage5Result r) => new()
    {
        ["edges"] = r.Edges.Count,
        ["bySource"] = r.Edges.GroupBy(e => e.Source).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count()),
        ["byKind"] = PerceptionKinds.All.ToDictionary(k => k, k => r.Edges.Count(e => e.Kind == k)),
        ["seededByHow"] = DiscoveryModes.All.ToDictionary(h => h, h => r.Edges.Count(e => e.Source == "seeded" && e.How == h)),
        ["ofFacts"] = r.Edges.Count(e => e.Fact is not null),
        ["ofCharacters"] = r.Edges.Count(e => e.TargetCharacter is not null),
        ["closed"] = r.Closed,
        ["open"] = r.Edges.Count(e => e.Until is null),
        ["factRetractions"] = r.FactRetractions.Count,
        ["perceptionRetractions"] = r.PerceptionRetractions,
        ["proposed"] = r.Chapters.Sum(c => c.Proposed),
        ["rejected"] = r.Rejected,
        ["rejectedByReason"] = r.RejectedByReason,
        ["unreadableReplies"] = r.Chapters.Sum(c => c.Unreadable),
        ["samples"] = r.Samples,
        ["minVotes"] = r.MinVotes,
    };
}
