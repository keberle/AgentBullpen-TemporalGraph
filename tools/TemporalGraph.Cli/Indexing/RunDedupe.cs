using System.Text.RegularExpressions;
using TemporalGraph.Data;

namespace TemporalGraph.Cli.Indexing;

/// <summary>What the run-wide de-duplication removed (stage-3.json <c>dedupe</c>).</summary>
/// <param name="MetRemoved">MET and IDENTICAL_TO edges removed (one per unordered pair of entities is kept).</param>
/// <param name="RelatedRemoved">RELATED_TO edges removed (one per unordered pair and kind; a kind whose words contain the other's merges into the shorter).</param>
/// <param name="LocatedRemoved">LOCATED_AT edges removed (one per source and target, unless their story dates differ).</param>
/// <param name="NodesMerged">Fact/Event nodes merged into an earlier near-identical node.</param>
/// <param name="Threshold">The token-Jaccard threshold for node texts.</param>
public sealed record RunDedupeCounts(int MetRemoved, int RelatedRemoved, int LocatedRemoved, int NodesMerged, double Threshold);

/// <summary>One removal or merge: the kept item and the dropped one (ids as numbered after the dedupe for the kept item;
/// the dropped item's id from before it).</summary>
public sealed record RunDedupeMerge(string Item, string Rule, string KeptId, string DroppedId, string? KeptText, string? DroppedText,
    string? KeptKind, string? DroppedKind, EvidenceMatch KeptEvidence, EvidenceMatch DroppedEvidence, double? Jaccard);

/// <summary>
/// Run-wide de-duplication of Stage 3 output: a code post-step after voting and the adjacent-chunk overlap dedupe
/// (<see cref="Stage3.Dedupe"/>). Items are taken in book order (evidence start), so the kept item is always the earliest and
/// its revealed position (the evidence start) is the first time the thing is told.
/// <list type="bullet">
/// <item><b>MET, IDENTICAL_TO</b>: at most one per unordered pair of entities.</item>
/// <item><b>RELATED_TO</b>: one per unordered pair and kind. Kinds are normalized (case, whitespace); two kinds of one pair
/// merge when one kind's words contain the other's ("friend" / "personal friend and medical attendant"), keeping the earliest
/// edge with the shorter kind.</item>
/// <item><b>LOCATED_AT</b>: one per (source, target) unless both have non-null, different story dates. Stage 3 has no story
/// dates yet (Stage 4 assigns them), so at this point the rule keeps one placement per (source, target).</item>
/// <item><b>Fact/Event nodes</b>: a duplicate has the same label, the same linked-entity set (link types ignored, as the
/// match rule does) and a near-identical text: normalized token Jaccard ≥ the threshold, or the same tokens once stopwords
/// and punctuation are stripped. The later node's evidence goes into the kept node's <see cref="GraphNode.AlsoEvidence"/>.</item>
/// </list>
/// Ids are renumbered in book order afterwards (E1…, N1…).
/// </summary>
public static partial class RunDedupe
{
    public const double DefaultThreshold = 0.6;

    private static readonly HashSet<string> Stopwords = new(StringComparer.Ordinal)
    {
        "a", "an", "the", "and", "or", "but", "of", "to", "in", "on", "at", "by", "for", "with", "from", "as", "is", "was", "are", "were",
        "be", "been", "being", "that", "this", "these", "those", "it", "its", "his", "her", "their", "he", "she", "they", "him", "them",
        "has", "had", "have", "which", "who", "whom", "there", "then", "than", "into", "upon", "so", "not", "no",
    };

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex Word();

    /// <summary>Lower-case word tokens (letters and digits), punctuation dropped.</summary>
    public static IReadOnlyList<string> Tokens(string text) => Word().Matches(text.ToLowerInvariant()).Select(m => m.Value).ToList();

    /// <summary>Jaccard similarity of the two texts' token sets.</summary>
    public static double Jaccard(string a, string b)
    {
        var (x, y) = (Tokens(a).ToHashSet(), Tokens(b).ToHashSet());
        if (x.Count == 0 && y.Count == 0) return 1;
        return x.Intersect(y).Count() / (double)x.Union(y).Count();
    }

    /// <summary>The same tokens once stopwords and punctuation are stripped (order kept).</summary>
    public static bool SameWithoutStopwords(string a, string b) =>
        Tokens(a).Where(t => !Stopwords.Contains(t)).SequenceEqual(Tokens(b).Where(t => !Stopwords.Contains(t)));

    /// <summary>Near-identical node texts (see the class summary).</summary>
    public static bool NearIdentical(string a, string b, double threshold, out double jaccard)
    {
        jaccard = Jaccard(a, b);
        return jaccard >= threshold || SameWithoutStopwords(a, b);
    }

    /// <summary>A RELATED_TO kind, case and whitespace normalized.</summary>
    public static string NormalizeKind(string? kind) => string.Join(' ', (kind ?? "").ToLowerInvariant().Split((char[])[' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Two normalized kinds are the same relation when equal or when one's words contain all of the other's.</summary>
    public static bool SameKind(string a, string b)
    {
        if (a == b) return true;
        var (x, y) = (Tokens(a).ToHashSet(), Tokens(b).ToHashSet());
        return x.Count > 0 && y.Count > 0 && (x.IsSubsetOf(y) || y.IsSubsetOf(x));
    }

    private static (int, int) Pair(GraphEdge e) => e.Source <= e.Target ? (e.Source, e.Target) : (e.Target, e.Source);

    /// <param name="storyOf">The story date (EDTF) of an edge by id, when known (Stage 4 output); Stage 3 passes none.</param>
    public static (List<GraphEdge> Edges, List<GraphNode> Nodes, RunDedupeCounts Counts, List<RunDedupeMerge> Merges, Dictionary<string, string> NewIds)
        Apply(IReadOnlyList<GraphEdge> edges, IReadOnlyList<GraphNode> nodes, double threshold = DefaultThreshold, Func<string, string?>? storyOf = null)
    {
        storyOf ??= _ => null;
        var merges = new List<(RunDedupeMerge Merge, object Kept)>();
        int met = 0, related = 0, located = 0, merged = 0;

        // Edges, in book order.
        var kept = new List<GraphEdge>();
        foreach (var e in edges.OrderBy(e => e.EvidenceMatch.CharStart).ThenBy(e => e.Id, StringComparer.Ordinal))
        {
            GraphEdge? same = null;
            string rule = "";
            if (e.Type is Ontology.Met or Ontology.IdenticalTo)
            {
                same = kept.FirstOrDefault(k => k.Type == e.Type && Pair(k) == Pair(e));
                rule = $"{e.Type} once per pair";
                if (same is not null) met++;
            }
            else if (e.Type == Ontology.RelatedTo)
            {
                var kind = NormalizeKind(e.Kind);
                same = kept.FirstOrDefault(k => k.Type == e.Type && Pair(k) == Pair(e) && SameKind(NormalizeKind(k.Kind), kind));
                rule = "RELATED_TO once per pair and kind";
                if (same is not null)
                {
                    related++;
                    // The earliest edge stays; it takes the shorter kind.
                    var shorter = (same.Kind ?? "").Length <= (e.Kind ?? "").Length ? same.Kind : e.Kind;
                    if (shorter != same.Kind)
                    {
                        var i = kept.IndexOf(same);
                        kept[i] = same with { Kind = shorter };
                        merges = merges.Select(m => ReferenceEquals(m.Kept, same) ? (m.Merge, (object)kept[i]) : m).ToList();
                        same = kept[i];
                    }
                }
            }
            else if (e.Type == Ontology.LocatedAt)
            {
                same = kept.FirstOrDefault(k => k.Type == e.Type && k.Source == e.Source && k.Target == e.Target
                    && !(storyOf(k.Id) is { } a && storyOf(e.Id) is { } b && a != b));
                rule = "LOCATED_AT once per source and target";
                if (same is not null) located++;
            }
            if (same is null) { kept.Add(e); continue; }
            merges.Add((new RunDedupeMerge("edge", rule, same.Id, e.Id, null, null, same.Kind, e.Kind, same.EvidenceMatch, e.EvidenceMatch, null), same));
        }

        // Nodes, in book order.
        var keptNodes = new List<GraphNode>();
        foreach (var n in nodes.OrderBy(n => n.EvidenceMatch.CharStart).ThenBy(n => n.Id, StringComparer.Ordinal))
        {
            var entities = Stage3.LinkedEntities(n);
            var j = 0.0;
            var target = keptNodes.FirstOrDefault(k => k.Label == n.Label && Stage3.LinkedEntities(k).SetEquals(entities) && NearIdentical(k.Text, n.Text, threshold, out j));
            if (target is null) { keptNodes.Add(n); continue; }
            merged++;
            var jac = Jaccard(target.Text, n.Text);
            var also = (target.AlsoEvidence ?? []).Append([n.EvidenceMatch.CharStart, n.EvidenceMatch.CharEnd]).Concat(n.AlsoEvidence ?? []).ToList();
            var i = keptNodes.IndexOf(target);
            keptNodes[i] = target with { AlsoEvidence = also };
            merges = merges.Select(m => ReferenceEquals(m.Kept, target) ? (m.Merge, (object)keptNodes[i]) : m).ToList();
            merges.Add((new RunDedupeMerge("node", "near-identical text, same label and entities", target.Id, n.Id, target.Text, n.Text, null, null,
                target.EvidenceMatch, n.EvidenceMatch, Math.Round(jac, 3)), keptNodes[i]));
        }

        // Renumber in book order (as Stage3.Dedupe does) and point the merges at the kept items' new ids.
        var newEdges = kept.OrderBy(e => e.EvidenceMatch.CharStart).ThenBy(e => e.Chunk).Select((e, i) => (Old: e, New: e with { Id = $"E{i + 1}" })).ToList();
        var newNodes = keptNodes.OrderBy(n => n.EvidenceMatch.CharStart).ThenBy(n => n.Chunk).Select((n, i) => (Old: n, New: n with { Id = $"N{i + 1}" })).ToList();
        var newIds = newEdges.ToDictionary(x => x.Old.Id, x => x.New.Id);
        foreach (var x in newNodes) newIds[x.Old.Id] = x.New.Id;
        var keptNewId = new Dictionary<object, string>(ReferenceEqualityComparer.Instance);
        foreach (var x in newEdges) keptNewId[x.Old] = x.New.Id;
        foreach (var x in newNodes) keptNewId[x.Old] = x.New.Id;
        var outMerges = merges.Select(m => m.Merge with { KeptId = keptNewId.TryGetValue(m.Kept, out var id) ? id : m.Merge.KeptId }).ToList();
        return (newEdges.Select(x => x.New).ToList(), newNodes.Select(x => x.New).ToList(),
            new RunDedupeCounts(met, related, located, merged, threshold), outMerges, newIds);
    }

    /// <summary>A Stage 3 result with the run-wide dedupe applied (counts in <see cref="Stage3Result.Dedupe"/>).</summary>
    public static (Stage3Result Result, List<RunDedupeMerge> Merges, Dictionary<string, string> NewIds) Apply(Stage3Result r, double threshold = DefaultThreshold)
    {
        var (edges, nodes, counts, merges, newIds) = Apply(r.Edges, r.Nodes, threshold);
        return (r with { Edges = edges, Nodes = nodes, Dedupe = counts }, merges, newIds);
    }

    /// <summary>A failures.jsonl line (tag <c>duplicate</c>) for a merge.</summary>
    public static object FailureLine(RunDedupeMerge m, string runId, string bookId) => new
    {
        type = Scoring.FailureTags.Duplicate,
        stage = Stage3.StageName,
        runId,
        bookId,
        item = m.Item,
        rule = m.Rule,
        kept = m.KeptId,
        dropped = m.DroppedId,
        keptText = m.KeptText,
        droppedText = m.DroppedText,
        keptKind = m.KeptKind,
        droppedKind = m.DroppedKind,
        keptEvidence = new[] { m.KeptEvidence.CharStart, m.KeptEvidence.CharEnd },
        droppedEvidence = new[] { m.DroppedEvidence.CharStart, m.DroppedEvidence.CharEnd },
        jaccard = m.Jaccard,
    };
}
