using TemporalGraph.Cli.Indexing;

namespace TemporalGraph.Cli.Crosscheck;

/// <summary>A review prompt from a cross-check source — not truth.</summary>
/// <param name="Kind">"not in ours", "not in theirs" or "alias difference".</param>
public sealed record Disagreement(string Source, string Kind, MentionType? Type, string Name, int Chapter, int CharStart, string Detail);

public sealed record ComparisonResult(
    string Source, bool Available, string? Note, int SliceStart, int SliceEnd,
    int TheirNames, int OurEntitiesInSlice, IReadOnlyList<Disagreement> Disagreements);

/// <summary>
/// Compares our Stage 2 entities with LitBank by text offsets, inside LitBank's slice only:
/// a LitBank proper-name mention matches if one of our mentions overlaps it; an entity of ours matches if one of its
/// mentions in the slice overlaps any LitBank mention. Names are grouped case-insensitively.
/// The slice is LitBank's coverage intersected with the run's chapters [<c>runStart</c>, <c>runEnd</c>): a source is only
/// compared over the text the run covered. No overlap → not available (no disagreements).
/// </summary>
public static partial class Comparison
{
    public static ComparisonResult CompareLitBank(IReadOnlyList<Entity> ours, IReadOnlyList<LitBankEntity> theirs, LitBankAlignment alignment, Func<int, int> chapterOf,
        int runStart = 0, int runEnd = int.MaxValue)
    {
        int sliceStart = Math.Max(alignment.FirstCharStart, runStart), sliceEnd = Math.Min(alignment.LastCharEnd, runEnd);
        if (sliceStart >= sliceEnd)
            return new ComparisonResult("LitBank", false,
                $"LitBank covers [{alignment.FirstCharStart}, {alignment.LastCharEnd}) (ch. {Chapters(chapterOf(alignment.FirstCharStart), chapterOf(alignment.LastCharEnd - 1))}), outside the run's chapters; not compared.",
                0, 0, 0, 0, []);
        static bool Overlaps(int a0, int a1, int b0, int b1) => a0 < b1 && b0 < a1;
        var mapped = theirs.Where(t => t.CharStart is not null && t.CharEnd is not null && t.CharStart >= sliceStart && t.CharEnd <= sliceEnd).ToList();

        var ourMentions = ours.SelectMany(e => e.Mentions.Select(m => (Entity: e, Mention: m)))
            .Where(x => x.Mention.CharStart >= sliceStart && x.Mention.CharEnd <= sliceEnd).ToList();
        var disagreements = new List<Disagreement>();

        // Theirs, not ours: LitBank proper names (typed PER/LOC/GPE/FAC) with no overlapping mention of ours.
        var theirProper = mapped.Where(t => t.Proper && LitBank.TypeOf(t.Category) is not null).ToList();
        foreach (var g in theirProper.GroupBy(t => Stage2.NormalizeName(LitBank.Detokenize(t.Text))))
        {
            var hit = g.Any(t => ourMentions.Any(o => Overlaps(o.Mention.CharStart, o.Mention.CharEnd, t.CharStart!.Value, t.CharEnd!.Value)));
            if (hit) continue;
            var first = g.OrderBy(t => t.CharStart).First();
            disagreements.Add(new Disagreement("LitBank", "not in ours", LitBank.TypeOf(first.Category), LitBank.Detokenize(first.Text),
                chapterOf(first.CharStart!.Value), first.CharStart!.Value, $"LitBank {first.Category} ×{g.Count()}"));
        }

        // Ours, not theirs: entities with mentions in the slice, none overlapping any LitBank mention.
        foreach (var e in ourMentions.GroupBy(x => x.Entity.Id))
        {
            var entity = e.First().Entity;
            var hit = e.Any(o => mapped.Any(t => Overlaps(o.Mention.CharStart, o.Mention.CharEnd, t.CharStart!.Value, t.CharEnd!.Value)));
            if (hit) continue;
            var first = e.OrderBy(x => x.Mention.CharStart).First().Mention;
            disagreements.Add(new Disagreement("LitBank", "not in theirs", entity.Type, entity.CanonicalName, first.Chapter, first.CharStart,
                $"{e.Count()} mention(s) in the slice, none annotated by LitBank"));
        }

        // Alias / boundary differences: LitBank proper names overlapping an entity's mentions but not among its aliases.
        foreach (var e in ourMentions.GroupBy(x => x.Entity.Id))
        {
            var entity = e.First().Entity;
            var aliasKeys = entity.Aliases.Select(Stage2.NormalizeName).ToHashSet();
            var theirNames = theirProper
                .Where(t => e.Any(o => Overlaps(o.Mention.CharStart, o.Mention.CharEnd, t.CharStart!.Value, t.CharEnd!.Value)))
                .Select(t => LitBank.Detokenize(t.Text))
                .Where(n => !aliasKeys.Contains(Stage2.NormalizeName(n)))
                .Distinct().ToList();
            foreach (var n in theirNames)
                disagreements.Add(new Disagreement("LitBank", "alias difference", entity.Type, entity.CanonicalName, e.First().Mention.Chapter, e.First().Mention.CharStart,
                    $"LitBank span \"{n}\" overlaps; our aliases: {string.Join(", ", entity.Aliases)}"));
        }

        return new ComparisonResult("LitBank", true, null, sliceStart, sliceEnd,
            theirProper.Select(t => Stage2.NormalizeName(LitBank.Detokenize(t.Text))).Distinct().Count(),
            ourMentions.Select(x => x.Entity.Id).Distinct().Count(),
            disagreements.OrderBy(d => d.CharStart).ToList());
    }

    private static string Chapters(int first, int last) => first == last ? $"{first}" : $"{first}–{last}";

    public static ComparisonResult BookNlpUnavailable(string folder) => new("BookNLP", false,
        $"No BookNLP output in {folder}: BookNLP was not run (its model downloads are outside the allowed sources).",
        0, 0, 0, 0, []);
}
