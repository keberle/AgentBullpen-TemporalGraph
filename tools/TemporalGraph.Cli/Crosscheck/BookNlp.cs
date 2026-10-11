using System.Globalization;
using TemporalGraph.Cli.Indexing;

namespace TemporalGraph.Cli.Crosscheck;

/// <summary>A BookNLP entity mention (from named-mentions.tsv, written by tools/crosscheck/booknlp_run.py).</summary>
/// <param name="Coref">BookNLP's coreference cluster id.</param>
/// <param name="Cat">PER, LOC, GPE or FAC.</param>
/// <param name="Prop">PROP (a name) or NOM (a common noun phrase).</param>
public sealed record BookNlpMention(string Coref, string Cat, string Prop, int CharStart, int CharEnd, string Text)
{
    public MentionType? Type => LitBank.TypeOf(Cat);
}

/// <summary>BookNLP output committed under data/crosscheck/booknlp/&lt;book&gt;/ (offsets are into the book's source text).</summary>
public static class BookNlp
{
    public const string MentionsFile = "named-mentions.tsv";

    public static string Folder(string root, string bookId) => Path.Combine(root, "data", "crosscheck", "booknlp", bookId);

    /// <summary>Null when BookNLP hasn't been run for this book.</summary>
    public static List<BookNlpMention>? Load(string root, string bookId)
    {
        var path = Path.Combine(Folder(root, bookId), MentionsFile);
        if (!File.Exists(path)) return null;
        return File.ReadLines(path).Skip(1).Where(l => l.Length > 0).Select(l =>
        {
            var c = l.Split('\t');
            return new BookNlpMention(c[0], c[1], c[2], int.Parse(c[3], CultureInfo.InvariantCulture), int.Parse(c[4], CultureInfo.InvariantCulture), c[5]);
        }).ToList();
    }
}

public static partial class Comparison
{
    /// <summary>
    /// Compares our Stage 2 entities with BookNLP inside [<paramref name="sliceStart"/>, <paramref name="sliceEnd"/>) (the run's
    /// chapters), by offsets, at the level of BookNLP's coreference clusters:
    /// "not in ours" — a cluster with a proper-name mention in the slice, none of whose slice mentions overlap ours;
    /// "not in theirs" — an entity of ours with slice mentions, none overlapping any BookNLP mention;
    /// "alias difference" — proper names of BookNLP clusters overlapping our entity that aren't among its aliases.
    /// </summary>
    public static ComparisonResult CompareBookNlp(IReadOnlyList<Entity> ours, IReadOnlyList<BookNlpMention> theirs, int sliceStart, int sliceEnd, Func<int, int> chapterOf)
    {
        static bool Overlaps(int a0, int a1, int b0, int b1) => a0 < b1 && b0 < a1;
        var inSlice = theirs.Where(t => t.Type is not null && t.CharStart >= sliceStart && t.CharEnd <= sliceEnd).ToList();
        var ourMentions = ours.SelectMany(e => e.Mentions.Select(m => (Entity: e, Mention: m)))
            .Where(x => x.Mention.CharStart >= sliceStart && x.Mention.CharEnd <= sliceEnd).ToList();
        var disagreements = new List<Disagreement>();

        var clusters = inSlice.GroupBy(t => t.Coref).ToList();
        foreach (var cluster in clusters.Where(c => c.Any(t => t.Prop == "PROP")))
        {
            if (cluster.Any(t => ourMentions.Any(o => Overlaps(o.Mention.CharStart, o.Mention.CharEnd, t.CharStart, t.CharEnd)))) continue;
            var names = cluster.Where(t => t.Prop == "PROP").Select(t => t.Text).Distinct().ToList();
            var first = cluster.OrderBy(t => t.CharStart).First();
            disagreements.Add(new Disagreement("BookNLP", "not in ours", first.Type, names[0], chapterOf(first.CharStart), first.CharStart,
                $"BookNLP {first.Cat} cluster {cluster.Key} ×{cluster.Count()}; names: {string.Join(", ", names)}"));
        }

        foreach (var e in ourMentions.GroupBy(x => x.Entity.Id))
        {
            var entity = e.First().Entity;
            var overlapping = inSlice.Where(t => e.Any(o => Overlaps(o.Mention.CharStart, o.Mention.CharEnd, t.CharStart, t.CharEnd))).ToList();
            var first = e.OrderBy(x => x.Mention.CharStart).First().Mention;
            if (overlapping.Count == 0)
            {
                disagreements.Add(new Disagreement("BookNLP", "not in theirs", entity.Type, entity.CanonicalName, first.Chapter, first.CharStart,
                    $"{e.Count()} mention(s) in the slice, none tagged by BookNLP"));
                continue;
            }
            var aliasKeys = entity.Aliases.Select(Stage2.NormalizeName).ToHashSet();
            var clusterIds = overlapping.Select(t => t.Coref).ToHashSet();
            var theirNames = inSlice.Where(t => clusterIds.Contains(t.Coref) && t.Prop == "PROP").Select(t => t.Text)
                .Where(n => !aliasKeys.Contains(Stage2.NormalizeName(n))).Distinct().ToList();
            if (theirNames.Count > 0)
                disagreements.Add(new Disagreement("BookNLP", "alias difference", entity.Type, entity.CanonicalName, first.Chapter, first.CharStart,
                    $"BookNLP cluster(s) {string.Join(", ", clusterIds.Order())} also use: {string.Join(", ", theirNames)}; our aliases: {string.Join(", ", entity.Aliases)}"));
        }

        return new ComparisonResult("BookNLP", true, null, sliceStart, sliceEnd,
            clusters.Count(c => c.Any(t => t.Prop == "PROP")), ourMentions.Select(x => x.Entity.Id).Distinct().Count(),
            disagreements.OrderBy(d => d.CharStart).ToList());
    }
}
