using Microsoft.Extensions.AI;
using TemporalGraph.Cli.Runs;
using TemporalGraph.Cli.Scoring;
using TemporalGraph.Cli.Telemetry;
using TemporalGraph.Data;
using TemporalGraph.Llm;

namespace TemporalGraph.Cli.Indexing;

// Stage 4: story time from the Events and Facts, not from the text; one call per chapter, one sample.
//   1. Anchor: one call on the first chapter's Events and Facts (their summaries, the text's own time expressions and the
//      evidence quotes) picks one plausible calendar date for the first narrated Event. An assumption, stated once.
//   2. One call per chapter: the anchor, the Events of the two chapters before it (id, summary, the text's time expression),
//      and this chapter's Events and Facts. Per item the model gives a date, or the Event it is relative to and an offset. The
//      chapters are asked side by side, so the rows of the chapters before carry no dates in the prompt: an item that follows
//      from an earlier chapter is given relative to one of its Events, and code resolves it.
//   3. Code, chapter by chapter in book order: the checks and the resolution of relative dates (TimelineStage4.Check), then the
//      same propagation as before (an undated narrated Event takes the date of the one before it, a referenced Event the day
//      before its narrating Event, a Fact its earliest discovering Event). No Event or Fact is left undated.
public static partial class TimelineStage4
{
    /// <summary>The output cap of a chapter call (one short entry per Event and Fact of the chapter).</summary>
    public const int ChapterMaxOutputTokens = 16000;

    public static string AnchorPromptFromItems(ChapterInfo chapter, IReadOnlyList<string> itemLines, GEvent? first) =>
        $"The events and facts of chapter {chapter.Number} (\"{chapter.Title}\"), each with the text's own time expression when it has one and a quote from the text:\n"
        + string.Join("\n", itemLines) + "\n"
        + (first is null ? "" : $"The first narrated event: \"{Flat(first.Summary)}\"\n")
        + "Pick one plausible calendar date (YYYY-MM-DD) for the first narrated event, from the clues these give (season, weather, dates on documents, technology). State the clue.";

    public static string ChapterPrompt(AnchorChoice anchor, IReadOnlyList<string> contextRows, ChapterInfo chapter, IReadOnlyList<string> itemLines)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Anchor: the story's first narrated event{(anchor.FirstEvent is null ? "" : $" ({anchor.FirstEvent})")} is assumed to happen on {anchor.Edtf}. It is an assumption; every other date is worked out from the events, relative to it.");
        if (contextRows.Count > 0)
        {
            sb.AppendLine("Events of the two chapters before this one (their dates are worked out separately; date an item that follows from one of them relative to it, by its id):");
            foreach (var row in contextRows) sb.AppendLine(row);
        }
        sb.AppendLine($"Items of chapter {chapter.Number} (\"{chapter.Title}\") to date:");
        foreach (var line in itemLines) sb.AppendLine(line);
        sb.Append("Return one entry per item.");
        return sb.ToString();
    }

    public static async Task<TimelineStage4Result> RunByChapterAsync(IChatClient client, LlmCallContext? context, SkillFile anchorSkill, SkillFile skill, Stage0Result stage0,
        GraphStage3Result stage3, StageRunner runner, RunFolder? run, CancellationToken ct, Action<int, int>? progress = null)
    {
        if (context is not null) context.BookId = stage0.BookId;
        var rejected = new List<TimeReject>();
        void Log(TimeReject r) => run?.AppendFailure(new { type = r.Tag, stage = StageName, runId = run.RunId, bookId = stage0.BookId, chapter = r.Chapter, chunkIndex = r.Chunk, id = r.Id, sample = r.Sample, reason = r.Reason });
        var options = Stage1.StrictOptions();
        options.MaxOutputTokens = ChapterMaxOutputTokens;
        var chapters = stage0.Chapters.Where(c => c.Number >= stage3.FirstChapter && c.Number <= stage3.LastChapter).OrderBy(c => c.Number).ToList();
        var narrated = stage3.Events.Where(e => e.Kind == EventKinds.Narrated).OrderBy(e => e.EvidenceMatch.CharStart).ToList();
        var firstEvent = narrated.FirstOrDefault();
        List<string> Lines(int chapter) => stage3.Events.Where(e => e.Chapter == chapter).Select(e => (e.EvidenceMatch.CharStart, Line: ItemLine(e)))
            .Concat(stage3.Facts.Where(f => f.Chapter == chapter).Select(f => (f.EvidenceMatch.CharStart, Line: ItemLine(f)))).OrderBy(x => x.CharStart).Select(x => x.Line).ToList();

        // 1. The anchor. Sent first and alone (the runner's first call), so the chapter calls find the cached prefix.
        AnchorChoice anchor;
        using (var span = IndexingTelemetry.Source.StartActivity("anchor"))
        {
            span?.SetTag(IndexingTelemetry.RunId, run?.RunId);
            span?.SetTag(IndexingTelemetry.Stage, StageName);
            if (context is not null) (context.SkillName, context.SkillVersion) = (anchorSkill.Name, anchorSkill.Version.ToString());
            var first = chapters[0];
            List<ChatMessage> messages = [new(ChatRole.System, anchorSkill.Text), new(ChatRole.User, AnchorPromptFromItems(first, Lines(first.Number), firstEvent))];
            AnchorAnswer? answer;
            try { answer = await runner.CallAsync(async () => (await client.GetResponseAsync<AnchorAnswer>(messages, Stage1.Json, options, useJsonSchemaResponseFormat: true, ct)).Result, ct, first.Number, null, new CacheHints("1h")); }
            catch (System.Text.Json.JsonException) { answer = null; }
            anchor = VoteAnchor(answer is null ? [] : [answer], 1, firstEvent is null ? null : $"{firstEvent.Id}: \"{Cut(firstEvent.Summary, 120)}\"")
                ?? throw new InvalidOperationException($"The anchor call gave no calendar date ({answer?.Date ?? "unreadable"}).");
            span?.SetTag("anchor.edtf", anchor.Edtf);
        }
        var anchorDate = Date(anchor.Edtf);

        // 2. The chapters, side by side.
        if (context is not null) (context.SkillName, context.SkillVersion) = (skill.Name, skill.Version.ToString());
        var done = 0;
        var replies = await StageRunner.ForEachAsync(chapters, async (chapter, _) =>
        {
            var lines = Lines(chapter.Number);
            if (lines.Count == 0) { progress?.Invoke(Interlocked.Increment(ref done), chapters.Count); return (Chapter: chapter, Output: (ChunkTimes?)null, Asked: false, ContextRows: 0); }
            using var span = IndexingTelemetry.Source.StartActivity($"chapter {chapter.Number}");
            span?.SetTag(IndexingTelemetry.RunId, run?.RunId);
            span?.SetTag(IndexingTelemetry.Stage, StageName);
            span?.SetTag(IndexingTelemetry.BookId, stage0.BookId);
            span?.SetTag(IndexingTelemetry.Chapter, chapter.Number);
            var contextRows = stage3.Events.Where(e => e.Chapter >= chapter.Number - ContextChapters && e.Chapter < chapter.Number).OrderBy(e => e.EvidenceMatch.CharStart)
                .Select(e => $"- {e.Id} · ch. {e.Chapter} · {e.Kind} · \"{Cut(e.Summary, 110)}\"{(e.When is null ? "" : $" · when: \"{Flat(e.When)}\"")}").ToList();
            List<ChatMessage> messages = [new(ChatRole.System, skill.Text), new(ChatRole.User, ChapterPrompt(anchor, contextRows, chapter, lines))];
            ChunkTimes? output;
            try { output = await runner.CallAsync(async () => (await client.GetResponseAsync<ChunkTimes>(messages, Stage1.Json, options, useJsonSchemaResponseFormat: true, ct)).Result, ct, chapter.Number, null, new CacheHints("1h")); }
            catch (System.Text.Json.JsonException) { output = null; }
            if (output is null)
                run?.AppendFailure(new { type = FailureTags.UnreadableReply, stage = StageName, runId = run.RunId, bookId = stage0.BookId, chapter = chapter.Number, samples = 1 });
            span?.SetTag("context.rows", contextRows.Count);
            progress?.Invoke(Interlocked.Increment(ref done), chapters.Count);
            return (Chapter: chapter, Output: output, Asked: true, ContextRows: contextRows.Count);
        });

        // 3. In book order: the checks, the relative dates, the fallbacks.
        var events = stage3.Events.ToDictionary(e => e.Id);
        var dates = new Dictionary<string, TimedNode>();
        if (firstEvent is not null) dates[firstEvent.Id] = new TimedNode(firstEvent.Id, "event", firstEvent.Chapter, firstEvent.Chunk, anchorDate, null, "anchor", null, 1);
        var summaries = new List<TimelineStage4Chunk>();
        foreach (var (chapter, output, asked, contextCount) in replies.OrderBy(r => r.Chapter.Number))
        {
            var chapterEvents = stage3.Events.Where(e => e.Chapter == chapter.Number).ToList();
            var chapterFacts = stage3.Facts.Where(f => f.Chapter == chapter.Number).ToList();
            var chunk = stage0.Chunks.Where(c => c.Chapter == chapter.Number).OrderBy(c => c.CharStart).First();
            var toDate = chapterEvents.Select(e => e.Id).Concat(chapterFacts.Select(f => f.Id)).Where(id => !dates.ContainsKey(id)).ToHashSet();
            int dated = 0, chapterRejected = 0;
            if (output is not null)
            {
                var known = stage3.Events.Where(e => dates.ContainsKey(e.Id) && e.Chapter >= chapter.Number - ContextChapters && e.Chapter <= chapter.Number).ToDictionary(e => e.Id, e => dates[e.Id].From);
                if (firstEvent is not null) known.TryAdd(firstEvent.Id, anchorDate);
                var sample = Check(output, chunk, toDate, known);
                foreach (var r in sample.Rejected) { rejected.Add(r); Log(r); chapterRejected++; }
                foreach (var id in toDate.OrderBy(id => id, StringComparer.Ordinal))
                {
                    if (VoteItem(id, [sample], 1, out _) is not { } v) continue;
                    dated++;
                    var item = id.StartsWith('E') ? (events[id].Chapter, events[id].Chunk) : (chapter.Number, chapterFacts.First(f => f.Id == id).Chunk);
                    dates[id] = new TimedNode(id, id.StartsWith('E') ? "event" : "fact", item.Item1, item.Item2, v.From, v.Until, v.Source, null, v.Votes, v.RelativeTo, v.Offset);
                }
            }
            foreach (var e in chapterEvents.Where(e => !dates.ContainsKey(e.Id)).OrderBy(e => e.Kind == EventKinds.Referenced).ThenBy(e => e.EvidenceMatch.CharStart))
                dates[e.Id] = FallbackEvent(e, events, dates, narrated, anchorDate);
            summaries.Add(new TimelineStage4Chunk(chunk.Index, chapter.Number, toDate.Count, dated, asked && output is null ? 1 : 0, chapterRejected, contextCount));
        }
        foreach (var e in stage3.Events.Where(e => !dates.ContainsKey(e.Id)).OrderBy(e => e.Kind == EventKinds.Referenced).ThenBy(e => e.EvidenceMatch.CharStart))
            dates[e.Id] = FallbackEvent(e, events, dates, narrated, anchorDate);

        var ordered = stage3.Events.OrderBy(e => dates[e.Id].From.Lo, StringComparer.Ordinal).ThenBy(e => dates[e.Id].From.Hi, StringComparer.Ordinal)
            .ThenBy(e => e.EvidenceMatch.CharStart).ThenBy(e => e.Id, StringComparer.Ordinal).ToList();
        for (var i = 0; i < ordered.Count; i++) dates[ordered[i].Id] = dates[ordered[i].Id] with { FabulaSeq = i + 1 };
        foreach (var f in stage3.Facts)
        {
            var discovering = f.Discoveries.Select(d => dates[d.Event]).OrderBy(d => d.From.Lo, StringComparer.Ordinal).ThenBy(d => d.FabulaSeq).First();
            dates[f.Id] = dates.TryGetValue(f.Id, out var own)
                ? own with { FabulaSeq = discovering.FabulaSeq }
                : new TimedNode(f.Id, "fact", f.Chapter, f.Chunk, discovering.From, discovering.Until, "propagated:discovering-event", discovering.FabulaSeq, 0, discovering.Id);
        }
        var timeline = Timeline(stage0, stage3, anchor, dates);
        return new TimelineStage4Result(Shape, stage0.BookId, anchor, summaries,
            dates.Values.OrderBy(d => d.Kind).ThenBy(d => int.Parse(d.Id[1..], System.Globalization.CultureInfo.InvariantCulture)).ToList(), timeline, 1, 1, rejected.Count,
            rejected.GroupBy(r => r.Tag).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count()));
    }
}
