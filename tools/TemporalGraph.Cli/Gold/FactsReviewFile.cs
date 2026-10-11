using System.Text;
using System.Text.RegularExpressions;
using TemporalGraph.Cli.Indexing;
using TemporalGraph.Data;

namespace TemporalGraph.Cli.Gold;

// The draft facts review: the same checklist format as the entity review, one line per edge or Fact/Event.
//
// Edge lines:   - [ ] #3 · MET · Sherlock Holmes → Dr. Watson · "short evidence" · revealed: ch1 · story: — <!-- 3/3 runs -->
// Node lines:   - [ ] #7 · Fact · "fact text" · links: STATES ← Dr. Mortimer, ABOUT → Sir Charles · "short evidence" · revealed: ch2 · story: 1742~ · reasoning: "quote"
// Order lines:  - [ ] BEFORE · #7 → #9 · "evidence"
// Marks:        [x] accept · [~] edit (changed inline) · [-] reject
// No importer yet: facts gold is a later pass.

/// <summary>One run's input to the facts review draft.</summary>
public sealed record FactsReviewSource(string RunId, Stage2Result Stage2, Stage3Result Stage3, Stage4Result? Stage4);

public static partial class FactsReviewFile
{
    public const string EdgesHeading = "## Edges";
    public const string NodesHeading = "## Facts and events";
    public const string OrderHeading = "## Event order";
    public const string MissingHeading = "## Missing (add here)";
    private const string Sep = ReviewFile.Sep;

    /// <summary>An item of one run: an edge or a node, with its Stage 4 time (if any).</summary>
    public sealed record RunItem(int Run, string Id, GraphEdge? Edge, GraphNode? Node, TimedItem? Time)
    {
        public EvidenceMatch Evidence => Edge?.EvidenceMatch ?? Node!.EvidenceMatch;
        public int Chapter => Edge?.Chapter ?? Node!.Chapter;
    }

    /// <summary>The same item across runs (at most one member per run), numbered #1… in book order.</summary>
    public sealed record UnionItem(int Number, IReadOnlyList<RunItem> Members)
    {
        public RunItem First => Members[0];
    }

    /// <summary>
    /// Matches items across runs with the match rule (by evidence, not wording; <see cref="Stage3.SameItem(GraphEdge, GraphEdge)"/>),
    /// entities being matched with <see cref="ReviewFile.Union"/> (the entity review's union). Two runs' edges are the same
    /// edge when they have the same type, the same union entities at both ends (either order for MET and IDENTICAL_TO) and
    /// overlapping evidence spans; the RELATED_TO kind is ignored. Two nodes are the same node when they have the same
    /// label, link the same union entities (link types ignored) and have overlapping evidence spans; the text is ignored.
    /// The largest evidence overlap is taken first. Each run's item joins at most one union item.
    /// </summary>
    public static IReadOnlyList<UnionItem> Union(IReadOnlyList<FactsReviewSource> sources, out Func<int, int, int> entityOf, out IReadOnlyList<UnionEntity> entities)
    {
        var unionEntities = ReviewFile.Union(sources.Select(s => s.Stage2).ToList());
        var map = new Dictionary<(int Run, int Entity), int>();
        for (var u = 0; u < unionEntities.Count; u++)
            foreach (var m in unionEntities[u].Members) map[(m.Run, m.Entity.Id)] = u;
        int Of(int run, int entity) => map.TryGetValue((run, entity), out var u) ? u : -1 - entity;
        entityOf = Of;
        entities = unionEntities;

        var groups = new List<List<RunItem>>();
        for (var run = 0; run < sources.Count; run++)
        {
            var times = (sources[run].Stage4?.Items ?? []).ToDictionary(i => i.Id);
            var items = sources[run].Stage3.Edges.Select(e => new RunItem(run, e.Id, e, null, times.GetValueOrDefault(e.Id)))
                .Concat(sources[run].Stage3.Nodes.Select(n => new RunItem(run, n.Id, null, n, times.GetValueOrDefault(n.Id))))
                .OrderBy(i => i.Evidence.CharStart).ToList();
            var existing = groups.Count;
            var candidates = new List<(RunItem Item, int Group, double Score)>();
            foreach (var item in items)
                for (var g = 0; g < existing; g++)
                    if (Score(groups[g][0], item, Of) is { } score) candidates.Add((item, g, score));
            var assigned = new Dictionary<string, int>();
            var taken = new HashSet<int>();
            foreach (var c in candidates.OrderByDescending(c => c.Score).ThenBy(c => c.Group))
            {
                if (assigned.ContainsKey(c.Item.Id) || taken.Contains(c.Group)) continue;
                assigned[c.Item.Id] = c.Group;
                taken.Add(c.Group);
            }
            foreach (var item in items)
            {
                if (assigned.TryGetValue(item.Id, out var g)) groups[g].Add(item);
                else groups.Add([item]);
            }
        }
        return groups.OrderBy(g => g.Min(i => i.Evidence.CharStart)).ThenBy(g => g[0].Edge is null ? 1 : 0)
            .Select((g, i) => new UnionItem(i + 1, g)).ToList();
    }

    private static bool Overlaps(EvidenceMatch a, EvidenceMatch b) => a.CharStart < b.CharEnd && b.CharStart < a.CharEnd;

    /// <summary>Null when <paramref name="b"/> can't be the same item as <paramref name="a"/>; else a match score (higher is better).</summary>
    private static double? Score(RunItem a, RunItem b, Func<int, int, int> entityOf)
    {
        if (!Overlaps(a.Evidence, b.Evidence)) return null;
        if (a.Edge is { } ea && b.Edge is { } eb)
        {
            if (ea.Type != eb.Type) return null;
            var (as_, at) = (entityOf(a.Run, ea.Source), entityOf(a.Run, ea.Target));
            var (bs, bt) = (entityOf(b.Run, eb.Source), entityOf(b.Run, eb.Target));
            var same = (as_ == bs && at == bt) || (ea.Type is Ontology.Met or Ontology.IdenticalTo && as_ == bt && at == bs);
            return same ? 1.0 + OverlapShare(a.Evidence, b.Evidence) : null;
        }
        if (a.Node is { } na && b.Node is { } nb)
        {
            if (na.Label != nb.Label) return null;
            var linksA = na.Links.Select(l => entityOf(a.Run, l.Entity)).ToHashSet();
            var linksB = nb.Links.Select(l => entityOf(b.Run, l.Entity)).ToHashSet();
            return linksA.SetEquals(linksB) ? 1.0 + OverlapShare(a.Evidence, b.Evidence) : null;
        }
        return null;
    }

    private static double OverlapShare(EvidenceMatch a, EvidenceMatch b)
    {
        var overlap = Math.Min(a.CharEnd, b.CharEnd) - Math.Max(a.CharStart, b.CharStart);
        return overlap / (double)Math.Max(a.CharEnd - a.CharStart, b.CharEnd - b.CharStart);
    }

    /// <summary>The book's text of a span, whitespace collapsed, cut at about <paramref name="max"/> characters; " becomes '.</summary>
    public static string Short(string bookText, EvidenceMatch m, int max = 90)
    {
        var raw = string.Join(' ', bookText[m.CharStart..m.CharEnd].Split((char[])[' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries));
        var cut = raw.Length <= max ? raw : raw[..Math.Max(raw.LastIndexOf(' ', max), 1)] + "…";
        return cut.Replace("\"", "'");
    }

    private static string Flat(string s) => string.Join(' ', s.Split((char[])[' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)).Replace("\"", "'");

    /// <summary>"—", "1889~", or "1884 until 1889".</summary>
    public static string Story(TimedItem? t)
    {
        if (t is null || (t.StoryFrom is null && t.StoryUntil is null)) return "—";
        return t.StoryUntil is null ? t.StoryFrom!.Edtf : $"{t.StoryFrom?.Edtf ?? "?"} until {t.StoryUntil.Edtf}";
    }

    /// <param name="minRuns">List only items found by at least this many runs (and orderings found by that many runs between
    /// listed items).</param>
    /// <param name="rest">Instead, list exactly what <paramref name="minRuns"/> leaves out (the scratch file of single-run items).</param>
    public static string Generate(string bookTitle, string bookId, string chapters, IReadOnlyList<FactsReviewSource> sources, string bookText, Func<int, int> chapterOf,
        int minRuns = 1, bool rest = false)
    {
        var n = sources.Count;
        var all = Union(sources, out var entityOf, out var entities);
        var listedNumbers = all.Where(u => u.Members.Count >= minRuns).Select(u => u.Number).ToHashSet();
        var union = all.Where(u => listedNumbers.Contains(u.Number) != rest).ToList();
        var names = new Dictionary<int, string>();
        for (var u = 0; u < entities.Count; u++) names[u] = entities[u].CanonicalName;
        string Name(RunItem item, int entityId)
        {
            var u = entityOf(item.Run, entityId);
            return names.TryGetValue(u, out var name) ? name : $"entity {entityId}";
        }
        string Runs(UnionItem u, IEnumerable<string>? more = null)
        {
            var notes = new List<string>();
            if (n > 1) notes.Add($"{u.Members.Count}/{n} runs");
            notes.AddRange(more ?? []);
            return notes.Count == 0 ? "" : $" <!-- {string.Join(" · ", notes)} -->";
        }
        string TimeFields(RunItem item)
        {
            var s = $"{Sep}revealed: ch{chapterOf(item.Evidence.CharStart)}{Sep}story: {Story(item.Time)}";
            if (item.Time?.ReasoningMatch is { } r) s += $"{Sep}reasoning: \"{Short(bookText, r)}\"";
            return s;
        }
        IEnumerable<string> StoryNotes(UnionItem u)
        {
            var stories = u.Members.GroupBy(m => Story(m.Time)).ToList();
            return stories.Count > 1 ? [$"story: {string.Join(", ", stories.Select(g => $"{g.Key} {g.Count()}/{n}"))}"] : [];
        }

        var sb = new StringBuilder();
        sb.AppendLine($"# {bookTitle} · ch. {chapters} · draft facts for review");
        sb.AppendLine();
        sb.AppendLine(n == 1
            ? $"Book `{bookId}` · run `{sources[0].RunId}` · generated {DateTime.UtcNow:yyyy-MM-dd} by the tool, unreviewed; **gold is only what you mark here**."
            : $"Book `{bookId}` · union of {n} runs: {string.Join(", ", sources.Select(s => $"`{s.RunId}`"))} · generated {DateTime.UtcNow:yyyy-MM-dd} by the tool, unreviewed; **gold is only what you mark here**.");
        sb.AppendLine();
        sb.AppendLine("Mark every line: `[x]` accept · `[~]` edit (change the line inline, keep the format) · `[-]` reject.");
        sb.AppendLine("Edges: `#n · TYPE · source → target · \"evidence\" · revealed: chN · story: EDTF or — · reasoning: \"quote\"` (RELATED_TO shows its kind in brackets).");
        sb.AppendLine("Facts and events: `#n · Fact|Event · \"text\" · links: … · \"evidence\" · revealed · story · reasoning`. Links: `STATES ← speaker`, `PARTICIPATED_IN ← character`, `ABOUT → entity`, `LOCATED_AT → place`.");
        sb.AppendLine("`revealed` is the chapter of the evidence quote (set by code). `story` is the proposed story time in EDTF (`—` = no basis in the text); `reasoning` quotes the text it rests on.");
        sb.AppendLine("Event order lines refer to the `#n` numbers. Keep the ` · ` separators. Entity names are the entity review's union names.");
        if (n > 1)
            sb.AppendLine($"Each line ends with how many of the {n} runs found it (`<!-- k/{n} runs -->`), and the story times when runs disagree. Comments are ignored.");
        if (minRuns > 1)
            sb.AppendLine(rest
                ? $"**Not for review:** the {union.Count} items found by fewer than {minRuns} of the {n} runs (left out of the review file), with their union numbers."
                : $"**Only items found by at least {minRuns} of the {n} runs are listed** ({union.Count} of {all.Count}; the numbers have gaps where single-run items were left out). Orderings likewise need {minRuns} runs and two listed items.");
        sb.AppendLine("Items are the same across runs when they have the same type, the same entities and overlapping evidence; their wording may differ.");
        sb.AppendLine();

        sb.AppendLine(EdgesHeading);
        sb.AppendLine();
        var edges = union.Where(u => u.First.Edge is not null).ToList();
        if (edges.Count == 0) sb.AppendLine("_None._");
        foreach (var u in edges)
        {
            var (item, e) = (u.First, u.First.Edge!);
            var type = e.Kind is null ? e.Type : $"{e.Type} ({e.Kind})";
            sb.AppendLine($"- [ ] #{u.Number}{Sep}{type}{Sep}{Name(item, e.Source)} → {Name(item, e.Target)}{Sep}\"{Short(bookText, e.EvidenceMatch)}\"{TimeFields(item)}{Runs(u, StoryNotes(u))}");
        }
        sb.AppendLine();

        sb.AppendLine(NodesHeading);
        sb.AppendLine();
        var nodes = union.Where(u => u.First.Node is not null).ToList();
        if (nodes.Count == 0) sb.AppendLine("_None._");
        foreach (var u in nodes)
        {
            var (item, node) = (u.First, u.First.Node!);
            var links = string.Join(", ", node.Links.Select(l => l.Type is Ontology.States or Ontology.ParticipatedIn
                ? $"{l.Type} ← {Name(item, l.Entity)}" : $"{l.Type} → {Name(item, l.Entity)}"));
            sb.AppendLine($"- [ ] #{u.Number}{Sep}{node.Label}{Sep}\"{Flat(node.Text)}\"{Sep}links: {links}{Sep}\"{Short(bookText, node.EvidenceMatch)}\"{TimeFields(item)}{Runs(u, StoryNotes(u))}");
        }
        sb.AppendLine();

        sb.AppendLine(OrderHeading);
        sb.AppendLine();
        var number = new Dictionary<(int Run, string Id), int>();
        foreach (var u in all) foreach (var m in u.Members) number.TryAdd((m.Run, m.Id), u.Number);
        var orders = sources.SelectMany((s, run) => (s.Stage4?.Order ?? []).Select(o => (Run: run, o)))
            .Where(x => number.ContainsKey((x.Run, x.o.A)) && number.ContainsKey((x.Run, x.o.B)))
            .GroupBy(x => (A: number[(x.Run, x.o.A)], B: number[(x.Run, x.o.B)], x.o.Relation))
            .Where(g => (g.Select(x => x.Run).Distinct().Count() >= minRuns && listedNumbers.Contains(g.Key.A) && listedNumbers.Contains(g.Key.B)) != rest)
            .OrderBy(g => g.Min(x => x.o.EvidenceMatch.CharStart)).ToList();
        if (orders.Count == 0) sb.AppendLine("_None._");
        foreach (var g in orders)
        {
            var o = g.First().o;
            var runs = g.Select(x => x.Run).Distinct().Count();
            sb.AppendLine($"- [ ] {o.Relation}{Sep}#{g.Key.A} → #{g.Key.B}{Sep}\"{Short(bookText, o.EvidenceMatch)}\"{(n > 1 ? $" <!-- {runs}/{n} runs -->" : "")}");
        }
        sb.AppendLine();

        sb.AppendLine(MissingHeading);
        sb.AppendLine();
        sb.AppendLine("<!-- Add missing edges, facts, events or orderings here in the formats above (number new items #new1, #new2, …), marked [x]. -->");
        return sb.ToString();
    }

    /// <summary>Line counts by type: edges by edge type, Fact and Event lines, order lines.</summary>
    public static Dictionary<string, int> LineCounts(string content)
    {
        var counts = new Dictionary<string, int>();
        var section = "";
        foreach (var line in content.ReplaceLineEndings("\n").Split('\n'))
        {
            if (line.StartsWith("## ")) { section = line; continue; }
            if (!line.StartsWith("- [ ] ")) continue;
            var fields = line[6..].Split(Sep);
            var key = section switch
            {
                EdgesHeading => "edge " + fields[1].Split(' ')[0],
                NodesHeading => fields[1],
                OrderHeading => "order " + fields[0],
                _ => "other",
            };
            counts[key] = counts.GetValueOrDefault(key) + 1;
        }
        return counts.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToDictionary(kv => kv.Key, kv => kv.Value);
    }
}
