using System.Text;
using Microsoft.Extensions.AI;
using TemporalGraph.Llm;
using TemporalGraph.Cli.Runs;
using TemporalGraph.Cli.Scoring;
using TemporalGraph.Cli.Telemetry;
using TemporalGraph.Data;

namespace TemporalGraph.Cli.Indexing;

// Stage 4 — time (skills/assign-story-time.md).
// - revealed_pos is set by code: the start offset of the item's evidence quote (reader time). No LLM.
// - Story time: one structured-output call per chapter, given the text from the run's first chapter to the end of that
//   chapter and the chapter's Stage 3 items. Per item: story_from / story_until in EDTF (or null), a verbatim reasoning
//   quote, and a fabula_seq rank among the chapter's undated items. Plus event_order relations the text states, each
//   with evidence. Code checks every EDTF string (Edtf.TryParse), every quote (the evidence checker) and every relation
//   (TemporalRelations.All); rejects go to failures.jsonl and the field stays null.

/// <summary>Structured output of one Stage 4 call. Strict mode: every field required; nullable fields are T|null.</summary>
public sealed record TimeAssignment(List<ItemTime> Items, List<OrderProposal> Order);

/// <param name="Id">The item id (E… or N…).</param>
/// <param name="StoryFrom">EDTF, or null when the text gives no basis.</param>
/// <param name="StoryUntil">EDTF, only where the text supports an end; else null.</param>
/// <param name="Reasoning">A verbatim quote that gives the date; null when both dates are null.</param>
/// <param name="FabulaSeq">For undated items only: story-order rank among the chapter's undated items (1 = earliest).</param>
public sealed record ItemTime(string Id, string? StoryFrom, string? StoryUntil, string? Reasoning, int? FabulaSeq);

/// <param name="Relation">A TLINK relation about time (TemporalRelations.Ordering): "A BEFORE B" = A happens first.</param>
/// <param name="Evidence">A verbatim quote that states the ordering.</param>
public sealed record OrderProposal(string A, string B, string Relation, string Evidence);

/// <summary>A checked EDTF value with its derived bounds (ISO yyyy-MM-dd, null where unbounded).</summary>
public sealed record StoryDate(string Edtf, string? Lo, string? Hi);

/// <param name="RevealedPos">Start of the item's evidence quote in the book text (set by code).</param>
/// <param name="FabulaSeq">Rank among the chapter's undated items, story order; scope: that chapter only.</param>
/// <param name="Returned">Whether the model returned an entry for the item.</param>
/// <param name="DateVotes">With voting: how many samples gave the kept (normalized) date, or, for an undated item, no date.</param>
/// <param name="UnstableDate">With voting: some samples gave a date but none had the votes; the date is null (logged as unstable date).</param>
public sealed record TimedItem(string Id, string Kind, int Chapter, int RevealedPos, StoryDate? StoryFrom, StoryDate? StoryUntil,
    string? Reasoning, EvidenceMatch? ReasoningMatch, double? FabulaSeq, bool Returned, int DateVotes = 1, bool UnstableDate = false);

/// <param name="Votes">With voting: how many samples proposed this (a, b, relation).</param>
public sealed record OrderRelation(string A, string B, string Relation, int Chapter, string Evidence, EvidenceMatch EvidenceMatch, int Votes = 1);

/// <param name="Sample">The sample (0-based) the reject came from; -1 for a voting outcome (unstable date).</param>
public sealed record Stage4Reject(string Tag, string Reason, int Chapter, string? Id, ItemTime? Item, OrderProposal? Order, EvidenceMatch? BestMatch, int Sample = 0);

public sealed record Stage4Chapter(int Chapter, int Items, int Returned, int Dated, int Orders, int Rejected, int TextStart, int TextEnd);

public sealed record Stage4Result(string BookId, double EvidenceThreshold, IReadOnlyList<Stage4Chapter> Chapters,
    IReadOnlyList<TimedItem> Items, IReadOnlyList<OrderRelation> Order, IReadOnlyList<Stage4Reject> Rejected, int Samples = 1, int MinVotes = 1,
    int UnreadableReplies = 0);

public static class Stage4
{
    public const string StageName = "stage-4";

    /// <summary>One line per item in the prompt.</summary>
    public static string ItemLine(GraphEdge e, IReadOnlyDictionary<int, EntityRef> entities) =>
        $"- {e.Id} · {e.Type}{(e.Kind is null ? "" : $" ({e.Kind})")} · {Name(entities, e.Source)} → {Name(entities, e.Target)} · evidence: \"{Flat(e.Evidence)}\"";

    public static string ItemLine(GraphNode n, IReadOnlyDictionary<int, EntityRef> entities) =>
        $"- {n.Id} · {n.Label} · \"{Flat(n.Text)}\" · {LinkList(n, entities)} · evidence: \"{Flat(n.Evidence)}\"";

    public static string LinkList(GraphNode n, IReadOnlyDictionary<int, EntityRef> entities) =>
        string.Join("; ", n.Links.Select(l => l.Type is Ontology.States or Ontology.ParticipatedIn
            ? $"{l.Type} ← {Name(entities, l.Entity)}" : $"{l.Type} → {Name(entities, l.Entity)}"));

    private static string Name(IReadOnlyDictionary<int, EntityRef> entities, int id) => entities.TryGetValue(id, out var e) ? e.Name : $"#{id}";
    private static string Flat(string s) => string.Join(' ', s.Split((char[])[' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries));

    public static string ChapterPrompt(int firstChapter, int chapter, string text, IReadOnlyList<string> itemLines) =>
        $"The text of chapters {firstChapter}–{chapter}:\n<<<\n{text}\n>>>\n" +
        $"Items from chapter {chapter} (id · type · what · evidence):\n{string.Join("\n", itemLines)}\n" +
        "Return one entry per item, and the orderings between these items that the text states.";

    public static async Task<Stage4Result> RunAsync(
        IChatClient client, LlmCallContext? context, SkillFile skill, Stage0Result stage0, string bookText, Stage3Result stage3,
        IReadOnlyList<EntityRef> entities, double threshold, RunFolder? run, CancellationToken ct, VotingSettings? voting = null)
    {
        voting ??= new VotingSettings();
        if (voting.Validate() is [var problem, ..]) throw new ArgumentException(problem.Replace("Stage1", "Stage4"), nameof(voting));
        var byId = entities.ToDictionary(e => e.Id);
        var first = stage0.Chapters.Single(c => c.Number == stage3.FirstChapter);
        var items = new List<TimedItem>();
        var order = new List<OrderRelation>();
        var rejected = new List<Stage4Reject>();
        var chapters = new List<Stage4Chapter>();
        var unreadable = 0;

        for (var number = stage3.FirstChapter; number <= stage3.LastChapter; number++)
        {
            var chapter = stage0.Chapters.Single(c => c.Number == number);
            var edges = stage3.Edges.Where(e => e.Chapter == number).ToList();
            var nodes = stage3.Nodes.Where(n => n.Chapter == number).ToList();
            if (edges.Count + nodes.Count == 0) continue;

            using var span = IndexingTelemetry.Source.StartActivity($"chapter {number}");
            span?.SetTag(IndexingTelemetry.RunId, run?.RunId);
            span?.SetTag(IndexingTelemetry.Stage, StageName);
            span?.SetTag(IndexingTelemetry.BookId, stage0.BookId);
            span?.SetTag(IndexingTelemetry.Chapter, number);
            span?.SetTag(IndexingTelemetry.SkillName, skill.Name);
            span?.SetTag(IndexingTelemetry.SkillVersion, skill.Version);
            if (context is not null)
            {
                context.BookId = stage0.BookId;
                context.Chapter = number;
                context.ChunkIndex = null;
                context.SkillName = skill.Name;
                context.SkillVersion = skill.Version.ToString();
            }

            var (textStart, textEnd) = (first.CharStart, chapter.CharEnd);
            var text = bookText[textStart..textEnd];
            var lines = edges.Select(e => (e.EvidenceMatch.CharStart, Line: ItemLine(e, byId)))
                .Concat(nodes.Select(n => (n.EvidenceMatch.CharStart, Line: ItemLine(n, byId))))
                .OrderBy(x => x.CharStart).Select(x => x.Line).ToList();
            List<ChatMessage> messages =
            [
                new(ChatRole.System, skill.Text),
                new(ChatRole.User, ChapterPrompt(stage3.FirstChapter, number, text, lines)),
            ];
            // k independent samples of the same request, in parallel; each is checked, then they vote (k = 1: no voting).
            // A reply that can't be read (cut off, e.g. by the provider's content filter) drops that sample; the others
            // vote, with m unchanged. With no readable sample the chapter's items get no time.
            var replies = await Task.WhenAll(Enumerable.Range(0, voting.Samples).Select(async _ =>
            {
                try { return ((TimeAssignment?)(await client.GetResponseAsync<TimeAssignment>(messages, Stage1.Json, Stage1.StrictOptions(), useJsonSchemaResponseFormat: true, ct)).Result, (string?)null); }
                catch (System.Text.Json.JsonException ex) { return (null, ex.Message); }
            }));
            foreach (var (_, error) in replies.Where(r => r.Item1 is null))
                run?.AppendFailure(new { type = FailureTags.UnreadableReply, stage = StageName, runId = run.RunId, bookId = stage0.BookId, chapter = number, reason = error, traceId = span?.TraceId.ToString() });
            var outputs = replies.Where(r => r.Item1 is not null).Select(r => r.Item1!).ToList();
            if (outputs.Count == 0) outputs.Add(new TimeAssignment([], []));
            span?.SetTag("samples.unreadable", replies.Length - outputs.Count(o => o.Items.Count > 0 || o.Order.Count > 0));

            var targets = edges.Select(e => (e.Id, Kind: "edge", e.EvidenceMatch.CharStart))
                .Concat(nodes.Select(n => (n.Id, Kind: "node", n.EvidenceMatch.CharStart))).ToList();
            var checkedChapter = Vote(outputs.Select(o => Check(o, number, targets, text, textStart, threshold)).ToList(), voting.MinVotes, number);
            unreadable += replies.Count(r => r.Item1 is null);
            items.AddRange(checkedChapter.Items);
            order.AddRange(checkedChapter.Order);
            rejected.AddRange(checkedChapter.Rejected);
            foreach (var r in checkedChapter.Rejected)
                run?.AppendFailure(new
                {
                    type = r.Tag,
                    stage = StageName,
                    runId = run.RunId,
                    bookId = stage0.BookId,
                    chapter = number,
                    id = r.Id,
                    item = r.Item,
                    order = r.Order,
                    sample = r.Sample,
                    reason = r.Reason,
                    bestMatch = r.BestMatch,
                    traceId = span?.TraceId.ToString(),
                });

            var summary = new Stage4Chapter(number, targets.Count, checkedChapter.Items.Count(i => i.Returned),
                checkedChapter.Items.Count(i => i.StoryFrom is not null || i.StoryUntil is not null), checkedChapter.Order.Count, checkedChapter.Rejected.Count, textStart, textEnd);
            chapters.Add(summary);
            span?.SetTag("items", summary.Items);
            span?.SetTag("items.dated", summary.Dated);
            span?.SetTag("order.relations", summary.Orders);
            span?.SetTag("items.rejected", summary.Rejected);
            span?.SetTag("stage4.samples", voting.Samples);
        }
        return new Stage4Result(stage0.BookId, threshold, chapters, items.OrderBy(i => i.RevealedPos).ThenBy(i => i.Id, StringComparer.Ordinal).ToList(), order, rejected,
            voting.Samples, voting.MinVotes, unreadable);
    }

    public sealed record CheckedChapter(List<TimedItem> Items, List<OrderRelation> Order, List<Stage4Reject> Rejected);

    /// <summary>
    /// The code checks on one chapter's output. Per item (the first entry for an id; unknown ids are rejected):
    /// <list type="bullet">
    /// <item>each EDTF string must parse (<see cref="Edtf.TryParse"/>), and story_until must not end before story_from starts;</item>
    /// <item>a date needs a reasoning quote that matches the given text, and that gives each full year unless the date is
    /// marked approximate (<see cref="UnquotedYear"/>); otherwise the dates are dropped (quote mismatch);</item>
    /// <item>fabula_seq is kept only for items left without a date.</item>
    /// </list>
    /// Orderings need two different known items of this chapter, a relation from <see cref="TemporalRelations.Ordering"/> and
    /// evidence that matches the text; the same (a, b, relation) is kept once. Items the model left out get no time.
    /// </summary>
    public static CheckedChapter Check(TimeAssignment output, int chapter, IReadOnlyList<(string Id, string Kind, int RevealedPos)> targets,
        string text, int textStart, double threshold)
    {
        var result = new CheckedChapter([], [], []);
        var byId = targets.ToDictionary(t => t.Id);
        var entries = new Dictionary<string, ItemTime>();
        foreach (var it in output.Items)
        {
            if (!byId.ContainsKey(it.Id)) { result.Rejected.Add(new(FailureTags.BadEndpoint, $"unknown item id '{it.Id}'", chapter, it.Id, it, null, null)); continue; }
            entries.TryAdd(it.Id, it);
        }

        string? QuoteProblem(string? quote, out EvidenceMatch? match)
        {
            match = null;
            if (string.IsNullOrWhiteSpace(quote)) return "no quote";
            match = EvidenceChecker.Find(quote, text, textStart);
            if (match is null) return "empty quote";
            if (match.Score < threshold) return $"quote not in the text (best {match.Method} score {match.Score:0.###} < {threshold})";
            return null;
        }

        foreach (var (id, kind, revealed) in targets)
        {
            if (!entries.TryGetValue(id, out var it))
            {
                result.Items.Add(new TimedItem(id, kind, chapter, revealed, null, null, null, null, null, false));
                continue;
            }
            StoryDate? Date(string? edtf, string field, out EdtfValue? value)
            {
                value = null;
                if (edtf is null) return null;
                if (!Edtf.TryParse(edtf, out value, out var error))
                {
                    result.Rejected.Add(new(FailureTags.InvalidEdtf, $"{field} '{edtf}': {error}", chapter, id, it, null, null));
                    return null;
                }
                return new StoryDate(value!.Text, value.LoIso, value.HiIso);
            }
            var from = Date(it.StoryFrom, "storyFrom", out var fromValue);
            var until = Date(it.StoryUntil, "storyUntil", out var untilValue);
            if (until is not null && fromValue?.Lo is { } fl && untilValue?.Hi is { } uh && uh < fl)
            {
                result.Rejected.Add(new(FailureTags.InvalidEdtf, $"storyUntil '{until.Edtf}' ends before storyFrom '{from!.Edtf}' starts", chapter, id, it, null, null));
                until = null;
            }

            string? reasoning = null;
            EvidenceMatch? reasoningMatch = null;
            if (from is not null || until is not null)
            {
                if (QuoteProblem(it.Reasoning, out var m) is { } problem)
                {
                    result.Rejected.Add(new(FailureTags.QuoteMismatch, $"reasoning for {from?.Edtf ?? until!.Edtf}: {problem}; dates dropped", chapter, id, it, null, m));
                    from = until = null;
                }
                else if (((from is null ? null : UnquotedYear(from.Edtf, it.Reasoning!)) ?? (until is null ? null : UnquotedYear(until.Edtf, it.Reasoning!))) is { } year)
                {
                    result.Rejected.Add(new(FailureTags.QuoteMismatch, $"reasoning for {from?.Edtf ?? until!.Edtf}: the quote doesn't give the year {year}, and the date isn't marked approximate (~); dates dropped", chapter, id, it, null, m));
                    from = until = null;
                }
                else (reasoning, reasoningMatch) = (it.Reasoning, m);
            }
            double? fabula = from is null && until is null && it.FabulaSeq is { } f ? f : null;
            result.Items.Add(new TimedItem(id, kind, chapter, revealed, from, until, reasoning, reasoningMatch, fabula, true));
        }

        var seen = new HashSet<(string, string, string)>();
        foreach (var o in output.Order)
        {
            if (!TemporalRelations.Ordering.Contains(o.Relation))
            {
                var why = o.Relation == TemporalRelations.Identity ? "IDENTITY says two items are the same event, not an order in time (a repeat is a duplicate)"
                    : $"relation '{o.Relation}' is not one of the TLINK relations about time ({string.Join(", ", TemporalRelations.Ordering)})";
                result.Rejected.Add(new(FailureTags.TypeOutsideOntology, why, chapter, null, null, o, null));
                continue;
            }
            if (!byId.ContainsKey(o.A) || !byId.ContainsKey(o.B)) { result.Rejected.Add(new(FailureTags.BadEndpoint, $"unknown item id in '{o.A} {o.Relation} {o.B}'", chapter, null, null, o, null)); continue; }
            if (o.A == o.B) { result.Rejected.Add(new(FailureTags.BadEndpoint, $"'{o.A}' ordered against itself", chapter, null, null, o, null)); continue; }
            if (QuoteProblem(o.Evidence, out var m) is { } problem) { result.Rejected.Add(new(FailureTags.QuoteMismatch, $"ordering evidence: {problem}", chapter, null, null, o, m)); continue; }
            if (!seen.Add((o.A, o.B, o.Relation))) continue;
            result.Order.Add(new OrderRelation(o.A, o.B, o.Relation, chapter, o.Evidence, m!));
        }
        return result;
    }


    /// <summary>
    /// Self-consistency voting over one chapter's checked samples. Per item, each sample's story date is the pair
    /// (storyFrom, storyUntil) after its checks, normalized with <see cref="Edtf.Normalize"/>. The most common dated pair
    /// (ties: the earliest sample) is kept when at least <paramref name="minVotes"/> samples give it; then fabula_seq is null.
    /// Otherwise the item is undated; if any sample gave a date, it is logged as <c>unstable date</c>. An undated item's
    /// fabula_seq is the median of the samples' values when at least m samples give one, else null. An ordering is kept when
    /// at least m samples propose the same (a, b, relation). One sample is returned unchanged.
    /// </summary>
    public static CheckedChapter Vote(IReadOnlyList<CheckedChapter> samples, int minVotes, int chapter)
    {
        if (samples.Count == 1) return samples[0];
        var result = new CheckedChapter([], [], samples.SelectMany((c, s) => c.Rejected.Select(r => r with { Sample = s })).ToList());
        static string? Key(TimedItem i) => i.StoryFrom is null && i.StoryUntil is null ? null
            : $"{(i.StoryFrom is null ? "" : Edtf.Normalize(i.StoryFrom.Edtf))}|{(i.StoryUntil is null ? "" : Edtf.Normalize(i.StoryUntil.Edtf))}";
        static StoryDate? Normalized(StoryDate? d) => d is null ? null : Edtf.Parse(Edtf.Normalize(d.Edtf)) is var v ? new StoryDate(v.Text, v.LoIso, v.HiIso) : null;

        foreach (var first in samples[0].Items)
        {
            var all = samples.Select(c => c.Items.Single(i => i.Id == first.Id)).ToList();
            var dated = all.Select((item, s) => (item, s, key: Key(item))).Where(x => x.key is not null).ToList();
            var best = dated.GroupBy(x => x.key).OrderByDescending(g => g.Count()).ThenBy(g => g.Min(x => x.s)).FirstOrDefault();
            var returned = all.Any(i => i.Returned);
            if (best is not null && best.Count() >= minVotes)
            {
                var chosen = best.OrderBy(x => x.s).First().item;
                result.Items.Add(chosen with { StoryFrom = Normalized(chosen.StoryFrom), StoryUntil = Normalized(chosen.StoryUntil), FabulaSeq = null, Returned = returned, DateVotes = best.Count() });
                continue;
            }
            var unstable = dated.Count > 0;
            if (unstable)
                result.Rejected.Add(new(FailureTags.UnstableDate,
                    $"no date had {minVotes} of {samples.Count} votes: " + string.Join(", ", dated.GroupBy(x => x.key).Select(g => $"{g.Key!.TrimEnd('|').Replace("|", " until ")} ×{g.Count()}")) +
                    $", no date ×{samples.Count - dated.Count}", chapter, first.Id, null, null, null, -1));
            var fabulas = all.Where(i => Key(i) is null && i.FabulaSeq is not null).Select(i => i.FabulaSeq!.Value).Order().ToList();
            double? fabula = fabulas.Count >= minVotes ? (fabulas.Count % 2 == 1 ? fabulas[fabulas.Count / 2] : (fabulas[fabulas.Count / 2 - 1] + fabulas[fabulas.Count / 2]) / 2) : null;
            result.Items.Add(first with
            {
                StoryFrom = null, StoryUntil = null, Reasoning = null, ReasoningMatch = null, FabulaSeq = fabula, Returned = returned,
                DateVotes = samples.Count - dated.Count, UnstableDate = unstable,
            });
        }

        foreach (var g in samples.SelectMany((c, s) => c.Order.Select(o => (o, s))).GroupBy(x => (x.o.A, x.o.B, x.o.Relation)))
        {
            var votes = g.Select(x => x.s).Distinct().Count();
            if (votes >= minVotes) result.Order.Add(g.OrderBy(x => x.s).First().o with { Votes = votes });
        }
        return result;
    }

    /// <summary>
    /// The no-invented-precision guard on years: each date in <paramref name="edtf"/> (both ends of an interval) that has a
    /// full year (four digits) must have that year written in the reasoning quote, unless it is marked approximate (<c>~</c> or
    /// <c>%</c>), which is how the skill marks a date worked out from the text. Returns the first unsupported year, or null.
    /// </summary>
    public static string? UnquotedYear(string edtf, string reasoning)
    {
        foreach (var side in edtf.Split('/'))
        {
            if (side.Length < 4 || !side[..4].All(char.IsAsciiDigit) || side.EndsWith('~') || side.EndsWith('%')) continue;
            if (!reasoning.Contains(side[..4], StringComparison.Ordinal)) return side[..4];
        }
        return null;
    }

    /// <summary>Counts for stage-4.json and the stage span.</summary>
    public static Dictionary<string, object> Counts(Stage4Result r) => new()
    {
        ["chapters"] = r.Chapters.Count,
        ["items"] = r.Items.Count,
        ["returned"] = r.Items.Count(i => i.Returned),
        ["dated"] = r.Items.Count(i => i.StoryFrom is not null || i.StoryUntil is not null),
        ["null"] = r.Items.Count(i => i.StoryFrom is null && i.StoryUntil is null),
        ["withStoryUntil"] = r.Items.Count(i => i.StoryUntil is not null),
        ["withFabulaSeq"] = r.Items.Count(i => i.FabulaSeq is not null),
        ["edtfInvalid"] = r.Rejected.Count(x => x.Tag == FailureTags.InvalidEdtf),
        ["order"] = r.Order.Count,
        ["orderByRelation"] = r.Order.GroupBy(o => o.Relation).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count()),
        ["rejected"] = r.Rejected.Count,
        ["rejectedByReason"] = r.Rejected.GroupBy(x => x.Tag).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count()),
        ["samples"] = r.Samples,
        ["minVotes"] = r.MinVotes,
        ["unstableDates"] = r.Items.Count(i => i.UnstableDate),
        ["unreadableReplies"] = r.UnreadableReplies,
    };
}
