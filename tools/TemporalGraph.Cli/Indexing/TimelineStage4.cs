using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using TemporalGraph.Cli.Runs;
using TemporalGraph.Cli.Scoring;
using TemporalGraph.Cli.Telemetry;
using TemporalGraph.Data;
using TemporalGraph.Llm;

namespace TemporalGraph.Cli.Indexing;

// Stage 4 — story time (skills/assign-story-time.md from version 4).
// 1. Anchor: one voted call on chapter 1 picks one plausible calendar date for the first narrated Event, from the text's own
//    clues. It is an assumption, stated once (the timeline's anchor row, the manifest). No chronology table, no outside data.
// 2. One voted call per chunk, with a bounded context: the anchor, the timeline rows so far of the previous two chapters and
//    of this chapter, and the chunk's Events and Facts. Never the book so far. Per item the model gives a story date (EDTF),
//    or the Event it is relative to and an offset ("the next morning" → that Event + P1D), which code resolves into EDTF.
// 3. Code fills what the model left: a narrated Event takes the date of the narrated Event before it (the first: the anchor);
//    a referenced Event the day before its narrating Event (never that Event's own date); a Fact its earliest discovering Event.
//    After this no Event or Fact is undated. Every other node and edge inherits in build-db (ClockPropagation).
// 4. The timeline rows: the anchor, every chapter, every Event.
// Stage4.cs is the earlier shape (one call per chapter over the book so far), kept to read the runs of the books not yet re-indexed.

/// <summary>Structured output of the anchor call.</summary>
/// <param name="Date">A full calendar date, YYYY-MM-DD.</param>
/// <param name="Clue">The clue in the text the date rests on, in a sentence.</param>
public sealed record AnchorAnswer(string Date, string Clue);

/// <summary>Structured output of one Stage 4 chunk call. Strict mode: every field required; nullable fields are string|null.</summary>
public sealed record ChunkTimes(List<ItemDate> Items);

/// <param name="Id">The item id (E… or F…).</param>
/// <param name="StoryFrom">EDTF, when the text gives the date itself; else null.</param>
/// <param name="StoryUntil">EDTF, only where the text supports an end; else null.</param>
/// <param name="RelativeTo">For a relative expression: the id of the Event it is counted from; else null.</param>
/// <param name="Offset">With <paramref name="RelativeTo"/>: an ISO 8601 duration, negative for "before" ("P1D", "-P3M", "P0D" for the same day).</param>
public sealed record ItemDate(string Id, string? StoryFrom, string? StoryUntil, string? RelativeTo, string? Offset);

/// <param name="VotedAt">How precisely the samples agreed: <c>day</c>, <c>month</c> or <c>year</c> (the day is then the most common one inside it).</param>
/// <param name="Proposed">Every sample's date, for the record.</param>
public sealed record AnchorChoice(string Edtf, string Note, string Clue, int Votes, string VotedAt, IReadOnlyList<string> Proposed, string? FirstEvent);

/// <summary>An Event's or a Fact's story time.</summary>
/// <param name="Kind">"event" or "fact".</param>
/// <param name="Source"><c>anchor</c>, <c>llm</c> (the model's date, or its relative expression resolved by code), <c>llm-coarse</c> (the
/// samples agreed only on the month or the year) or <c>propagated:…</c> (code: previous-event, before-narrating-event, discovering-event).</param>
/// <param name="FabulaSeq">Story order: an Event's rank among all Events (by story date, then the order told); a Fact has its dating Event's.</param>
public sealed record TimedNode(string Id, string Kind, int Chapter, int Chunk, StoryDate From, StoryDate? Until, string Source, double? FabulaSeq, int Votes,
    string? RelativeTo = null, string? Offset = null);

/// <param name="Kind"><c>anchor</c>, <c>chapter</c> or <c>event</c>.</param>
/// <param name="Ref">The chapter number or the Event id; null for the anchor.</param>
public sealed record TimelineItem(string Kind, string? Ref, string Label, int? RevealedPos, int? RevealedPosEnd, StoryDate? From, StoryDate? Until, double? FabulaSeq, string? Note);

public sealed record TimeReject(string Tag, string Reason, int Chunk, int Chapter, string? Id, int Sample);

public sealed record TimelineStage4Chunk(int Index, int Chapter, int Items, int Dated, int Unreadable, int Rejected, int ContextRows);

/// <param name="Shape">2: the full-schema shape. A stage-4.json without it is the earlier shape (<see cref="Stage4Result"/>).</param>
public sealed record TimelineStage4Result(int Shape, string BookId, AnchorChoice Anchor, IReadOnlyList<TimelineStage4Chunk> Chunks, IReadOnlyList<TimedNode> Items,
    IReadOnlyList<TimelineItem> Timeline, int Samples, int MinVotes, int Rejected, IReadOnlyDictionary<string, int> RejectedByReason);

public static partial class TimelineStage4
{
    public const string StageName = "stage-4";
    public const int Shape = 2;

    /// <summary>The context window: the timeline rows of this many chapters before the chunk's own.</summary>
    public const int ContextChapters = 2;

    [GeneratedRegex(@"\A(?<sign>[+-])?P(?:(?<y>\d+)Y)?(?:(?<m>\d+)M)?(?:(?<w>\d+)W)?(?:(?<d>\d+)D)?(?:T[0-9HMS.]*)?\z")]
    private static partial Regex Duration();

    private static string Flat(string s) => string.Join(' ', s.Split((char[])[' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries));
    private static string Cut(string s, int max) => Flat(s) is var f && f.Length > max ? f[..(max - 1)].TrimEnd() + "…" : Flat(s);

    public static StoryDate Date(EdtfValue v) => new(v.Text, v.LoIso, v.HiIso);

    private static StoryDate Date(string edtf) => Date(Edtf.Parse(edtf));

    /// <summary>"day", "month" or "year": how precise an EDTF value is (anything wider than a year counts as a year).</summary>
    public static string Precision(EdtfValue v) =>
        v.Lo is not { } lo || v.Hi is not { } hi ? "year"
        : lo == hi ? "day"
        : lo.Year == hi.Year && lo.Month == hi.Month ? "month"
        : "year";

    /// <summary>
    /// A relative expression resolved into EDTF: <paramref name="basis"/> (the date of the Event it is counted from) plus
    /// <paramref name="offset"/> (ISO 8601: P1D, -P3M, P2Y, P1W; a time part is ignored). The result is as precise as the
    /// coarser of the two: days onto a day give a day, months give a month, years a year. A result coarser than a day, or one
    /// from an approximate basis, is marked approximate (<c>~</c>). Null when the offset can't be read or the basis has no bounds.
    /// </summary>
    public static string? Resolve(EdtfValue basis, string offset)
    {
        var m = Duration().Match(offset.Trim());
        if (!m.Success || basis.Lo is not { } lo) return null;
        int N(string g) => m.Groups[g].Success ? int.Parse(m.Groups[g].Value, CultureInfo.InvariantCulture) : 0;
        var sign = m.Groups["sign"].Value == "-" ? -1 : 1;
        var (years, months, days) = (N("y"), N("m"), N("w") * 7 + N("d"));
        if (years == 0 && months == 0 && days == 0) return basis.Text;
        var offsetPrecision = days != 0 ? "day" : months != 0 ? "month" : "year";
        string[] order = ["day", "month", "year"];
        var precision = order[Math.Max(Array.IndexOf(order, Precision(basis)), Array.IndexOf(order, offsetPrecision))];
        DateOnly date;
        try { date = lo.AddYears(sign * years).AddMonths(sign * months).AddDays(sign * days); }
        catch (ArgumentOutOfRangeException) { return null; }
        return precision switch
        {
            "day" => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + (basis.Approximate ? "~" : ""),
            "month" => date.ToString("yyyy-MM", CultureInfo.InvariantCulture) + "~",
            _ => date.ToString("yyyy", CultureInfo.InvariantCulture) + "~",
        };
    }

    // ---- The anchor -------------------------------------------------------------------------------------------------

    public static string AnchorPrompt(ChapterInfo chapter, string chapterText, GEvent? first) =>
        $"The text of chapter {chapter.Number} (\"{chapter.Title}\"):\n<<<\n{chapterText}\n>>>\n" +
        (first is null ? "" : $"The first narrated event: \"{Flat(first.Summary)}\"\n") +
        "Pick one plausible calendar date (YYYY-MM-DD) for the first narrated event, from the text's own clues (season, weather, dates on documents, technology). State the clue.";

    /// <summary>
    /// The anchor vote: the date at least <paramref name="minVotes"/> samples give; failing that the month, then the year, that many
    /// agree on, with the most common day inside it (ties: the earliest). Null when not even a year has the votes.
    /// </summary>
    public static AnchorChoice? VoteAnchor(IReadOnlyList<AnchorAnswer> answers, int minVotes, string? firstEvent)
    {
        var dated = answers.Select(a => (a, Value: Edtf.TryParse(a.Date?.Trim(), out var v, out _) && v!.Lo is not null && v.Lo == v.Hi ? v : null))
            .Where(x => x.Value is not null).Select(x => (x.a, Day: x.Value!.Lo!.Value)).ToList();
        foreach (var (level, key) in new (string, Func<DateOnly, string>)[] { ("day", d => d.ToString("yyyy-MM-dd")), ("month", d => d.ToString("yyyy-MM")), ("year", d => d.ToString("yyyy")) })
        {
            var best = dated.GroupBy(x => key(x.Day)).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).FirstOrDefault();
            if (best is null || best.Count() < minVotes) continue;
            var day = best.GroupBy(x => x.Day).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).First();
            var edtf = day.Key.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var clue = Flat(day.First().a.Clue ?? "");
            return new AnchorChoice(edtf, $"Assumption: the story's first narrated event is placed on {edtf}, a date made up from the text's own clues ({clue}). Every other story date is derived from it.",
                clue, best.Count(), level, answers.Select(a => a.Date).ToList(), firstEvent);
        }
        return null;
    }

    // ---- One chunk --------------------------------------------------------------------------------------------------

    public static string ItemLine(GEvent e) =>
        $"- {e.Id} · Event ({e.Kind}{(e.MentionedIn is null ? "" : $", mentioned in {e.MentionedIn}")}) · \"{Flat(e.Summary)}\"" +
        (e.When is null ? "" : $" · when: \"{Flat(e.When)}\"") + $" · evidence: \"{Cut(e.Evidence, 200)}\"";

    public static string ItemLine(GFact f) =>
        $"- {f.Id} · Fact · \"{Flat(f.Text)}\" · discovered by {string.Join(", ", f.Discoveries.Select(d => $"{d.Event} ({d.How})"))} · evidence: \"{Cut(f.Evidence, 200)}\"";

    public static string ChunkPrompt(AnchorChoice anchor, IReadOnlyList<string> contextRows, ChunkInfo chunk, IReadOnlyList<string> itemLines)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Anchor: the story's first narrated event{(anchor.FirstEvent is null ? "" : $" ({anchor.FirstEvent})")} is assumed to happen on {anchor.Edtf}. It is an assumption; every other date is worked out from the text, relative to it.");
        if (contextRows.Count > 0)
        {
            sb.AppendLine("Timeline so far (the two chapters before this one, and this chapter):");
            foreach (var row in contextRows) sb.AppendLine(row);
        }
        sb.AppendLine($"Items of chunk {chunk.Index} (chapter {chunk.Chapter}) to date:");
        foreach (var line in itemLines) sb.AppendLine(line);
        sb.Append("Return one entry per item.");
        return sb.ToString();
    }

    /// <summary>One sample's dates for a chunk: item id → (from, until) and how it was given; plus the rejects.</summary>
    public sealed record CheckedTimes(Dictionary<string, (StoryDate From, StoryDate? Until, string? RelativeTo, string? Offset)> Dates, List<TimeReject> Rejected);

    /// <summary>
    /// The checks on one sample. An entry must be for an item of the chunk (the first entry for an id counts). A date is either
    /// EDTF that parses, or a relative expression: <c>relativeTo</c> an Event whose date is known (<paramref name="known"/>: the
    /// timeline so far) or given in this same answer, and an offset <see cref="Resolve"/> can read. <c>storyUntil</c> must parse and
    /// not end before the start, else it is dropped. An entry with neither gives the item no date here.
    /// </summary>
    public static CheckedTimes Check(ChunkTimes output, ChunkInfo chunk, IReadOnlySet<string> items, IReadOnlyDictionary<string, StoryDate> known, int sample = 0)
    {
        var result = new CheckedTimes([], []);
        void Reject(string tag, string reason, string? id) => result.Rejected.Add(new TimeReject(tag, reason, chunk.Index, chunk.Chapter, id, sample));
        var entries = new Dictionary<string, ItemDate>();
        foreach (var it in output.Items ?? [])
        {
            if (it.Id is null || !items.Contains(it.Id)) { Reject(FailureTags.BadEndpoint, $"unknown item id '{it.Id}'", it.Id); continue; }
            entries.TryAdd(it.Id, it);
        }
        StoryDate? Until(ItemDate it, StoryDate from)
        {
            if (string.IsNullOrWhiteSpace(it.StoryUntil)) return null;
            if (!Edtf.TryParse(it.StoryUntil.Trim(), out var u, out var error)) { Reject(FailureTags.InvalidEdtf, $"storyUntil '{it.StoryUntil}': {error}", it.Id); return null; }
            if (u!.HiIso is { } hi && from.Lo is { } lo && string.CompareOrdinal(hi, lo) < 0) { Reject(FailureTags.InvalidEdtf, $"storyUntil '{u.Text}' ends before storyFrom '{from.Edtf}' starts", it.Id); return null; }
            return Date(u);
        }

        // Absolute dates first; then relative ones, which may rest on a date given in this same answer.
        var pending = new List<ItemDate>();
        foreach (var it in entries.Values)
        {
            if (!string.IsNullOrWhiteSpace(it.RelativeTo)) { pending.Add(it); continue; }
            if (string.IsNullOrWhiteSpace(it.StoryFrom)) continue;
            if (!Edtf.TryParse(it.StoryFrom.Trim(), out var v, out var error)) { Reject(FailureTags.InvalidEdtf, $"storyFrom '{it.StoryFrom}': {error}", it.Id); continue; }
            if (v!.Lo is null) { Reject(FailureTags.InvalidEdtf, $"storyFrom '{v.Text}' gives no year (every date is derived from the anchor)", it.Id); continue; }
            var from = Date(v);
            result.Dates[it.Id] = (from, Until(it, from), null, null);
        }
        for (var pass = 0; pass < 6 && pending.Count > 0; pass++)
        {
            foreach (var it in pending.ToList())
            {
                var basisId = it.RelativeTo!.Trim();
                var basis = result.Dates.TryGetValue(basisId, out var here) ? here.From : known.GetValueOrDefault(basisId);
                if (basis is null) continue;
                pending.Remove(it);
                if (basisId == it.Id) { Reject(FailureTags.BadEndpoint, "an item dated relative to itself", it.Id); continue; }
                if (Resolve(Edtf.Parse(basis.Edtf), it.Offset ?? "") is not { } edtf) { Reject(FailureTags.InvalidEdtf, $"offset '{it.Offset}' from {basisId} ({basis.Edtf}) can't be resolved", it.Id); continue; }
                var from = Date(edtf);
                result.Dates[it.Id] = (from, Until(it, from), basisId, it.Offset!.Trim());
            }
        }
        foreach (var it in pending) Reject(FailureTags.BadEndpoint, $"relativeTo '{it.RelativeTo}' is not an Event with a date in the timeline so far or in this answer", it.Id);
        return result;
    }

    /// <summary>
    /// The vote on one item over a chunk's samples. The pair (from, until), normalized, that at least <paramref name="minVotes"/>
    /// samples give is kept (<c>llm</c>). Failing that, the month (then the year) of the start that many samples agree on is kept as
    /// an approximate date (<c>llm-coarse</c>). Otherwise the item is left to code; if any sample gave a date, that is logged as
    /// <c>unstable date</c>.
    /// </summary>
    public static (StoryDate From, StoryDate? Until, string Source, int Votes, string? RelativeTo, string? Offset)? VoteItem(
        string id, IReadOnlyList<CheckedTimes> samples, int minVotes, out string? unstable)
    {
        unstable = null;
        var given = samples.Select((s, i) => (i, Has: s.Dates.TryGetValue(id, out var d), d)).Where(x => x.Has).Select(x => (x.i, x.d)).ToList();
        if (given.Count == 0) return null;
        static string Key((StoryDate From, StoryDate? Until, string? RelativeTo, string? Offset) d) => $"{Edtf.Normalize(d.From.Edtf)}|{(d.Until is null ? "" : Edtf.Normalize(d.Until.Edtf))}";
        var best = given.GroupBy(x => Key(x.d)).OrderByDescending(g => g.Count()).ThenBy(g => g.Min(x => x.i)).First();
        if (best.Count() >= minVotes)
        {
            var chosen = best.OrderBy(x => x.i).First().d;
            return (Date(Edtf.Normalize(chosen.From.Edtf)), chosen.Until is null ? null : Date(Edtf.Normalize(chosen.Until.Edtf)), "llm", best.Count(), chosen.RelativeTo, chosen.Offset);
        }
        foreach (var length in new[] { 7, 4 }) // yyyy-MM, then yyyy
        {
            var coarse = given.Where(x => x.d.From.Lo is { } lo && x.d.From.Hi is { } hi && lo[..length] == hi[..length])
                .GroupBy(x => x.d.From.Lo![..length]).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).FirstOrDefault();
            if (coarse is not null && coarse.Count() >= minVotes) return (Date(coarse.Key + "~"), null, "llm-coarse", coarse.Count(), null, null);
        }
        unstable = $"no date had {minVotes} of {samples.Count} votes: " + string.Join(", ", given.GroupBy(x => Key(x.d)).Select(g => $"{g.Key.TrimEnd('|').Replace("|", " until ")} ×{g.Count()}"));
        return null;
    }

    // ---- The run ----------------------------------------------------------------------------------------------------

    public static async Task<TimelineStage4Result> RunAsync(IChatClient client, LlmCallContext? context, SkillFile anchorSkill, SkillFile skill, Stage0Result stage0,
        string bookText, GraphStage3Result stage3, RunFolder? run, CancellationToken ct, VotingSettings voting, Action<int, int>? progress = null)
    {
        if (voting.Validate() is [var problem, ..]) throw new ArgumentException(problem.Replace("Stage1", "Stage4"), nameof(voting));
        void Context(int? chapter, int? chunk, SkillFile s)
        {
            if (context is null) return;
            context.BookId = stage0.BookId;
            context.Chapter = chapter;
            context.ChunkIndex = chunk;
            context.SkillName = s.Name;
            context.SkillVersion = s.Version.ToString();
        }
        var rejected = new List<TimeReject>();
        void Log(TimeReject r) => run?.AppendFailure(new { type = r.Tag, stage = StageName, runId = run.RunId, bookId = stage0.BookId, chapter = r.Chapter, chunkIndex = r.Chunk, id = r.Id, sample = r.Sample, reason = r.Reason });

        // 1. The anchor, from the run's first chapter.
        var firstChapter = stage0.Chapters.Single(c => c.Number == stage3.FirstChapter);
        var narrated = stage3.Events.Where(e => e.Kind == EventKinds.Narrated).OrderBy(e => e.EvidenceMatch.CharStart).ToList();
        var firstEvent = narrated.FirstOrDefault();
        AnchorChoice anchor;
        using (var span = IndexingTelemetry.Source.StartActivity("anchor"))
        {
            span?.SetTag(IndexingTelemetry.RunId, run?.RunId);
            span?.SetTag(IndexingTelemetry.Stage, StageName);
            Context(firstChapter.Number, null, anchorSkill);
            List<ChatMessage> messages = [new(ChatRole.System, anchorSkill.Text), new(ChatRole.User, AnchorPrompt(firstChapter, bookText[firstChapter.CharStart..firstChapter.CharEnd], firstEvent))];
            var replies = await Task.WhenAll(Enumerable.Range(0, voting.Samples).Select(async _ =>
            {
                try { return (await client.GetResponseAsync<AnchorAnswer>(messages, Stage1.Json, Stage1.StrictOptions(), useJsonSchemaResponseFormat: true, ct)).Result; }
                catch (System.Text.Json.JsonException) { return null; }
            }));
            anchor = VoteAnchor(replies.OfType<AnchorAnswer>().ToList(), voting.MinVotes, firstEvent is null ? null : $"{firstEvent.Id}: \"{Cut(firstEvent.Summary, 120)}\"")
                ?? throw new InvalidOperationException($"The anchor date did not get {voting.MinVotes} of {voting.Samples} votes even for a year: {string.Join(", ", replies.Select(r => r?.Date ?? "unreadable"))}.");
            span?.SetTag("anchor.edtf", anchor.Edtf);
        }
        var anchorDate = Date(anchor.Edtf);

        // 2. Chunk by chunk, with the bounded context.
        var events = stage3.Events.ToDictionary(e => e.Id);
        var dates = new Dictionary<string, TimedNode>();
        if (firstEvent is not null) dates[firstEvent.Id] = new TimedNode(firstEvent.Id, "event", firstEvent.Chapter, firstEvent.Chunk, anchorDate, null, "anchor", null, anchor.Votes);
        var chunks = stage0.Chunks.Where(c => c.Chapter >= stage3.FirstChapter && c.Chapter <= stage3.LastChapter).ToList();
        var summaries = new List<TimelineStage4Chunk>();
        foreach (var chunk in chunks)
        {
            var chunkEvents = stage3.Events.Where(e => e.Chunk == chunk.Index).ToList();
            var chunkFacts = stage3.Facts.Where(f => f.Chunk == chunk.Index).ToList();
            var toDate = chunkEvents.Select(e => e.Id).Concat(chunkFacts.Select(f => f.Id)).Where(id => !dates.ContainsKey(id)).ToHashSet();
            if (toDate.Count == 0) { progress?.Invoke(summaries.Count + 1, chunks.Count); summaries.Add(new TimelineStage4Chunk(chunk.Index, chunk.Chapter, 0, 0, 0, 0, 0)); continue; }

            using var span = IndexingTelemetry.Source.StartActivity($"chunk {chunk.Index}");
            span?.SetTag(IndexingTelemetry.RunId, run?.RunId);
            span?.SetTag(IndexingTelemetry.Stage, StageName);
            span?.SetTag(IndexingTelemetry.BookId, stage0.BookId);
            span?.SetTag(IndexingTelemetry.Chapter, chunk.Chapter);
            span?.SetTag(IndexingTelemetry.ChunkIndex, chunk.Index);
            Context(chunk.Chapter, chunk.Index, skill);

            // The timeline so far: Events already dated, of the two chapters before this one and of this chapter.
            var window = stage3.Events.Where(e => dates.ContainsKey(e.Id) && e.Chapter >= chunk.Chapter - ContextChapters && e.Chapter <= chunk.Chapter)
                .OrderBy(e => e.EvidenceMatch.CharStart).ToList();
            var known = window.ToDictionary(e => e.Id, e => dates[e.Id].From);
            if (firstEvent is not null) known.TryAdd(firstEvent.Id, anchorDate);
            var contextRows = window.Select(e => $"- {e.Id} · ch. {e.Chapter} · {e.Kind} · {dates[e.Id].From.Edtf}{(dates[e.Id].Until is { } u ? $" until {u.Edtf}" : "")} · \"{Cut(e.Summary, 110)}\"").ToList();
            var lines = chunkEvents.Where(e => toDate.Contains(e.Id)).Select(e => (e.EvidenceMatch.CharStart, Line: ItemLine(e)))
                .Concat(chunkFacts.Select(f => (f.EvidenceMatch.CharStart, Line: ItemLine(f)))).OrderBy(x => x.CharStart).Select(x => x.Line).ToList();
            List<ChatMessage> messages = [new(ChatRole.System, skill.Text), new(ChatRole.User, ChunkPrompt(anchor, contextRows, chunk, lines))];
            var replies = await Task.WhenAll(Enumerable.Range(0, voting.Samples).Select(async _ =>
            {
                try { return (await client.GetResponseAsync<ChunkTimes>(messages, Stage1.Json, Stage1.StrictOptions(), useJsonSchemaResponseFormat: true, ct)).Result; }
                catch (System.Text.Json.JsonException) { return null; }
            }));
            var unreadable = replies.Count(r => r is null);
            if (unreadable > 0)
                run?.AppendFailure(new { type = FailureTags.UnreadableReply, stage = StageName, runId = run.RunId, bookId = stage0.BookId, chapter = chunk.Chapter, chunkIndex = chunk.Index, samples = unreadable });
            var samples = replies.OfType<ChunkTimes>().Select((o, s) => Check(o, chunk, toDate, known, s)).ToList();
            var chunkRejected = samples.SelectMany(s => s.Rejected).ToList();
            var dated = 0;
            foreach (var id in toDate.OrderBy(id => id, StringComparer.Ordinal))
            {
                var vote = VoteItem(id, samples, voting.MinVotes, out var unstable);
                if (unstable is not null) chunkRejected.Add(new TimeReject(FailureTags.UnstableDate, unstable, chunk.Index, chunk.Chapter, id, -1));
                if (vote is not { } v) continue;
                dated++;
                dates[id] = new TimedNode(id, id.StartsWith('E') ? "event" : "fact", chunk.Chapter, chunk.Index, v.From, v.Until, v.Source, null, v.Votes, v.RelativeTo, v.Offset);
            }
            rejected.AddRange(chunkRejected);
            foreach (var r in chunkRejected) Log(r);

            // 3a. An Event the model left undated is dated by code now, so the next chunk's context has it.
            foreach (var e in chunkEvents.Where(e => !dates.ContainsKey(e.Id)).OrderBy(e => e.Kind == EventKinds.Referenced).ThenBy(e => e.EvidenceMatch.CharStart))
                dates[e.Id] = FallbackEvent(e, events, dates, narrated, anchorDate);

            summaries.Add(new TimelineStage4Chunk(chunk.Index, chunk.Chapter, toDate.Count, dated, unreadable, chunkRejected.Count, contextRows.Count));
            span?.SetTag("items", toDate.Count);
            span?.SetTag("items.dated", dated);
            span?.SetTag("context.rows", contextRows.Count);
            progress?.Invoke(summaries.Count, chunks.Count);
        }
        // Events of chunks outside the plan (none in a whole-run pass), then Facts.
        foreach (var e in stage3.Events.Where(e => !dates.ContainsKey(e.Id)).OrderBy(e => e.Kind == EventKinds.Referenced).ThenBy(e => e.EvidenceMatch.CharStart))
            dates[e.Id] = FallbackEvent(e, events, dates, narrated, anchorDate);

        // Story order of the Events: by story date, then the order told.
        var ordered = stage3.Events.OrderBy(e => dates[e.Id].From.Lo, StringComparer.Ordinal).ThenBy(e => dates[e.Id].From.Hi, StringComparer.Ordinal)
            .ThenBy(e => e.EvidenceMatch.CharStart).ThenBy(e => e.Id, StringComparer.Ordinal).ToList();
        for (var i = 0; i < ordered.Count; i++) dates[ordered[i].Id] = dates[ordered[i].Id] with { FabulaSeq = i + 1 };

        // 3b. Facts: the model's date, else the earliest discovering Event.
        foreach (var f in stage3.Facts)
        {
            var discovering = f.Discoveries.Select(d => dates[d.Event]).OrderBy(d => d.From.Lo, StringComparer.Ordinal).ThenBy(d => d.FabulaSeq).First();
            dates[f.Id] = dates.TryGetValue(f.Id, out var own)
                ? own with { FabulaSeq = discovering.FabulaSeq }
                : new TimedNode(f.Id, "fact", f.Chapter, f.Chunk, discovering.From, discovering.Until, "propagated:discovering-event", discovering.FabulaSeq, 0, discovering.Id);
        }

        var timeline = Timeline(stage0, stage3, anchor, dates);
        return new TimelineStage4Result(Shape, stage0.BookId, anchor, summaries,
            dates.Values.OrderBy(d => d.Kind).ThenBy(d => int.Parse(d.Id[1..], CultureInfo.InvariantCulture)).ToList(), timeline, voting.Samples, voting.MinVotes, rejected.Count,
            rejected.GroupBy(r => r.Tag).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count()));
    }

    /// <summary>
    /// The date code gives an Event the model left undated. Narrated: the date of the narrated Event told just before it (none: the
    /// anchor). Referenced: the day before its narrating Event, approximate — its own earlier time, never the narrating Event's date.
    /// </summary>
    public static TimedNode FallbackEvent(GEvent e, IReadOnlyDictionary<string, GEvent> events, IReadOnlyDictionary<string, TimedNode> dates,
        IReadOnlyList<GEvent> narratedInOrder, StoryDate anchor)
    {
        if (e.Kind == EventKinds.Referenced && e.MentionedIn is { } n && dates.TryGetValue(n, out var narrating) && Edtf.TryParse(narrating.From.Edtf, out var basis, out _)
            && basis!.Lo is { } lo && lo > DateOnly.MinValue.AddDays(1))
            return new TimedNode(e.Id, "event", e.Chapter, e.Chunk, Date(lo.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "~"), null,
                "propagated:before-narrating-event", null, 0, n, "-P1D");
        var before = narratedInOrder.Where(x => x.EvidenceMatch.CharStart < e.EvidenceMatch.CharStart && dates.ContainsKey(x.Id)).LastOrDefault();
        return before is null
            ? new TimedNode(e.Id, "event", e.Chapter, e.Chunk, anchor, null, "propagated:anchor", null, 0)
            : new TimedNode(e.Id, "event", e.Chapter, e.Chunk, dates[before.Id].From, null, "propagated:previous-event", null, 0, before.Id, "P0D");
    }

    /// <summary>
    /// The timeline rows: the anchor; one per chapter (its text span, and the story span of its narrated Events: from the
    /// earliest start to the latest, <c>until</c> only when they differ); one per Event (its evidence span, its story time, its
    /// story order), in book order.
    /// </summary>
    public static List<TimelineItem> Timeline(Stage0Result stage0, GraphStage3Result stage3, AnchorChoice anchor, IReadOnlyDictionary<string, TimedNode> dates)
    {
        var rows = new List<TimelineItem> { new(TimelineKinds.Anchor, null, "anchor", null, null, Date(anchor.Edtf), null, null, anchor.Note) };
        foreach (var chapter in stage0.Chapters.Where(c => c.Number >= stage3.FirstChapter && c.Number <= stage3.LastChapter))
        {
            var story = stage3.Events.Where(e => e.Chapter == chapter.Number && e.Kind == EventKinds.Narrated).Select(e => dates[e.Id]).ToList();
            var from = story.OrderBy(d => d.From.Lo, StringComparer.Ordinal).ThenBy(d => d.From.Hi, StringComparer.Ordinal).Select(d => d.From).FirstOrDefault();
            var last = story.OrderByDescending(d => d.From.Hi, StringComparer.Ordinal).Select(d => d.From).FirstOrDefault();
            rows.Add(new TimelineItem(TimelineKinds.Chapter, chapter.Number.ToString(CultureInfo.InvariantCulture), chapter.Title, chapter.CharStart, chapter.CharEnd,
                from, last is not null && from is not null && last.Edtf != from.Edtf ? last : null, null, null));
        }
        foreach (var e in stage3.Events.OrderBy(e => e.EvidenceMatch.CharStart).ThenBy(e => e.Id, StringComparer.Ordinal))
            rows.Add(new TimelineItem(TimelineKinds.Event, e.Id, e.Summary, e.EvidenceMatch.CharStart, e.EvidenceMatch.CharEnd, dates[e.Id].From, dates[e.Id].Until, dates[e.Id].FabulaSeq, null));
        return rows;
    }

    /// <summary>Counts for stage-4.json and the console.</summary>
    public static Dictionary<string, object> Counts(TimelineStage4Result r) => new()
    {
        ["anchor"] = r.Anchor.Edtf,
        ["anchorVotes"] = r.Anchor.Votes,
        ["anchorVotedAt"] = r.Anchor.VotedAt,
        ["chunks"] = r.Chunks.Count,
        ["items"] = r.Items.Count,
        ["events"] = r.Items.Count(i => i.Kind == "event"),
        ["facts"] = r.Items.Count(i => i.Kind == "fact"),
        ["bySource"] = r.Items.GroupBy(i => i.Source).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count()),
        ["eventsBySource"] = r.Items.Where(i => i.Kind == "event").GroupBy(i => i.Source).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count()),
        ["factsBySource"] = r.Items.Where(i => i.Kind == "fact").GroupBy(i => i.Source).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count()),
        ["relative"] = r.Items.Count(i => i.Source == "llm" && i.RelativeTo is not null),
        ["withStoryUntil"] = r.Items.Count(i => i.Until is not null),
        ["timelineRows"] = r.Timeline.Count,
        ["contextRowsMax"] = r.Chunks.Count == 0 ? 0 : r.Chunks.Max(c => c.ContextRows),
        ["rejected"] = r.Rejected,
        ["rejectedByReason"] = r.RejectedByReason,
        ["unreadableReplies"] = r.Chunks.Sum(c => c.Unreadable),
        ["samples"] = r.Samples,
        ["minVotes"] = r.MinVotes,
    };
}
