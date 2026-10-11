namespace TemporalGraph.Query;

/// <summary>One row of a book's <c>timeline</c> table, as loaded with the graph.</summary>
/// <param name="Kind"><c>anchor</c>, <c>chapter</c> or <c>event</c>.</param>
/// <param name="RefId">The chapter number or the Event node id; null for the anchor.</param>
public sealed record TimelineEntry(string Kind, long? RefId, string? Label, int? RevealedPos, int? RevealedPosEnd,
    string? StoryFrom, string? StoryUntil, string? StoryFromLo, string? StoryFromHi, string? StoryUntilLo, string? StoryUntilHi,
    double? FabulaSeq, string? Note);

/// <summary>
/// The spoiler rules, in one place. The engine's <c>visible()</c> function, the chat tools' post-filters and the
/// tests all call this; the browser's gate (web/src/graph/model.ts) is the same rule in TypeScript.
/// <list type="bullet">
/// <item><b>Spoilers off</b> (the reader's clock): visible iff <c>revealedPos ≤ pos</c> and (<c>retractedPos</c> is null or
/// <c>retractedPos &gt; pos</c>).</item>
/// <item><b>Spoilers on</b> (the story's clock): visible iff <c>storyFromLo ≤ story(pos)</c> and (<c>storyUntilLo</c> is null or
/// <c>storyUntilLo &gt; story(pos)</c>). The reveal position is ignored.</item>
/// </list>
/// An element without the clock a mode needs is visible in that mode: a book indexed before the full schema has no story clock, so
/// Spoilers on hides nothing there, as before.
/// </summary>
public static class SpoilerGate
{
    /// <summary>
    /// <c>story(pos)</c>: the story-time <c>hi</c> bound of the latest timeline row (chapter or event) whose
    /// <c>revealed_pos ≤ pos</c>. "Latest" is in story time: the row that starts latest (the greatest <c>story_from_lo</c>; ties:
    /// the greater <c>story_from_hi</c>) among those the reader has been shown — the furthest point the story clock has reached.
    /// So a flashback told late in a chapter never turns the clock back, and a date known only to the year ("1889", whose upper
    /// bound is 31 December) does not carry the clock to the year's end while day-precise rows stand later than its start. The
    /// value is that row's <c>story_from_hi</c>. An event row counts from its <c>revealed_pos</c>; a chapter row once the chapter
    /// has been read to its end (<c>revealed_pos_end ≤ pos</c>). Dates are ISO yyyy-MM-dd and compare as text. Null when no row qualifies.
    /// </summary>
    public static string? StoryAt(IReadOnlyList<TimelineEntry> timeline, long pos)
    {
        TimelineEntry? latest = null;
        foreach (var row in timeline)
        {
            if (row.Kind is not ("chapter" or "event") || row.RevealedPos is not { } revealed || revealed > pos) continue;
            // A chapter row carries the story span of the whole chapter, so it counts once the chapter has been read to its end
            // (the end of chapter N is the start of chapter N + 1, which has not been read). Inside a chapter its events carry the clock.
            if (row.Kind == "chapter" && (row.RevealedPosEnd ?? revealed) > pos) continue;
            if (row.StoryFromLo is null || row.StoryFromHi is null) continue;
            var order = latest is null ? 1 : string.CompareOrdinal(row.StoryFromLo, latest.StoryFromLo);
            if (order > 0 || (order == 0 && string.CompareOrdinal(row.StoryFromHi, latest!.StoryFromHi) > 0)) latest = row;
        }
        return latest?.StoryFromHi;
    }

    /// <summary>Whether an element with these properties (a node's or a relationship's, as <see cref="BookGraph"/> exposes them)
    /// is visible. <paramref name="story"/> is <c>story(pos)</c>; it is used only with <paramref name="spoilers"/> on.</summary>
    public static bool Visible(IReadOnlyDictionary<string, object?> properties, long pos, bool spoilers, string? story)
    {
        if (!spoilers)
        {
            if (Number(properties.GetValueOrDefault("revealedPos")) is { } revealed && revealed > pos) return false;
            return Number(properties.GetValueOrDefault("retractedPos")) is not { } retracted || retracted > pos;
        }
        if (story is null) return true;
        if (properties.GetValueOrDefault("storyFromLo") is string from && string.CompareOrdinal(from, story) > 0) return false;
        return properties.GetValueOrDefault("storyUntilLo") is not string until || string.CompareOrdinal(until, story) > 0;
    }

    private static double? Number(object? v) => v switch { long l => l, double d => d, int i => i, _ => null };
}
