using TemporalGraph.Data;

namespace TemporalGraph.Query;

/// <summary>One leak test's outcome: how many things it looked at, and what failed (empty = passed).</summary>
public sealed record LeakCheck(string Name, int Checked, IReadOnlyList<string> Failures)
{
    public bool Passed => Failures.Count == 0;
}

/// <summary>
/// The leak tests, run on every <c>index build-db</c> and before every <c>index promote</c>, on the graph as a query
/// sees it. "Reachable" is what the spoiler rule lets through at a position (<see cref="SpoilerGate"/>): a node that passes, an edge
/// that passes whose two ends pass, and from a Fact the entities named in its <c>subject</c>.
/// <list type="number">
/// <item><b>Spoilers off</b>, at every chapter boundary: nothing reachable has <c>revealedPos</c> after the boundary; and no
/// reachable edge or Fact points at a node the reader has not been shown.</item>
/// <item><b>Spoilers on</b>, at every chapter boundary: nothing reachable has <c>storyFromLo</c> after <c>story(pos)</c>; and no
/// reachable edge or Fact points at a node the story has not reached.</item>
/// <item><b>Identity</b>: while an IDENTICAL_TO edge is hidden (either mode), no chain of visible IDENTICAL_TO edges joins its two
/// characters, and neither character carries the other's names.</item>
/// <item><b>Referenced events</b>: each has exactly one MENTIONED_IN edge, to a narrated event, and is revealed no earlier than it.</item>
/// <item><b>Clocks</b>: every node and every edge has <c>revealedPos</c> and <c>storyFrom</c>; a <c>retractedPos</c> is after its <c>revealedPos</c>.</item>
/// </list>
/// A book indexed before the full schema has no clocks on its nodes and no timeline; the tests are for the full schema.
/// </summary>
public static class LeakTests
{
    public const int MaxFailuresListed = 20;

    private static double? Number(object? v) => v switch { long l => l, double d => d, int i => i, _ => null };

    /// <summary>The ids in a Fact's <c>subject</c> property.</summary>
    public static IEnumerable<long> Subjects(NodeValue fact) =>
        fact.Properties.GetValueOrDefault("subject") is IReadOnlyList<object?> list ? list.Select(Number).Where(n => n is not null).Select(n => (long)n!.Value) : [];

    /// <summary>What the rule lets through at <paramref name="pos"/>: the visible nodes, and the visible edges both of whose ends are visible.</summary>
    public static (HashSet<long> Nodes, List<RelationshipValue> Edges, List<RelationshipValue> Dangling) View(BookGraph graph, long pos, bool spoilers)
    {
        var story = spoilers ? graph.StoryAt(pos) : null;
        var nodes = graph.Nodes.Where(n => SpoilerGate.Visible(n.Properties, pos, spoilers, story)).Select(n => n.Id).ToHashSet();
        var passing = graph.Relationships.Where(r => SpoilerGate.Visible(r.Properties, pos, spoilers, story)).ToList();
        return (nodes, passing.Where(r => nodes.Contains(r.StartId) && nodes.Contains(r.EndId)).ToList(), passing.Where(r => !nodes.Contains(r.StartId) || !nodes.Contains(r.EndId)).ToList());
    }

    public static IReadOnlyList<LeakCheck> Run(BookGraph graph)
    {
        var checks = new List<LeakCheck>();
        var boundaries = graph.Chapters.OrderBy(c => c.Key).Select(c => (Chapter: c.Key, Pos: (long)c.Value.CharEnd)).ToList();
        static List<string> Listed(List<string> failures) =>
            failures.Count <= MaxFailuresListed ? failures : [.. failures.Take(MaxFailuresListed), $"… {failures.Count - MaxFailuresListed} more"];

        foreach (var spoilers in new[] { false, true })
        {
            var failures = new List<string>();
            var looked = 0;
            foreach (var (chapter, pos) in boundaries)
            {
                var story = spoilers ? graph.StoryAt(pos) : null;
                var (nodes, edges, dangling) = View(graph, pos, spoilers);
                looked += nodes.Count + edges.Count + dangling.Count;
                string Where() => spoilers ? $"ch. {chapter} (story {story ?? "—"})" : $"ch. {chapter} (pos {pos})";
                bool Late(IReadOnlyDictionary<string, object?> p) => spoilers
                    ? story is not null && p.GetValueOrDefault("storyFromLo") is string lo && string.CompareOrdinal(lo, story) > 0
                    : Number(p.GetValueOrDefault("revealedPos")) is { } r && r > pos;
                foreach (var id in nodes.Where(id => Late(graph.Node(id).Properties))) failures.Add($"{Where()}: node {id} is reachable but later");
                foreach (var e in edges.Where(e => Late(e.Properties))) failures.Add($"{Where()}: edge {e.Id} ({e.Type}) is reachable but later");
                foreach (var e in dangling) failures.Add($"{Where()}: edge {e.Id} ({e.Type}) passes but an end ({(nodes.Contains(e.StartId) ? e.EndId : e.StartId)}) is hidden");
                foreach (var id in nodes)
                {
                    var node = graph.Node(id);
                    if (!node.Labels.Contains(Ontology.Fact)) continue;
                    foreach (var subject in Subjects(node).Where(s => !nodes.Contains(s))) failures.Add($"{Where()}: fact {id} names subject {subject}, which is hidden");
                }
            }
            checks.Add(new LeakCheck(spoilers ? "spoilers-on" : "spoilers-off", looked, Listed(failures)));
        }

        // Identity: while the edge is hidden, the two characters are unrelated.
        {
            var failures = new List<string>();
            var identities = graph.Relationships.Where(r => r.Type == Ontology.IdenticalTo).ToList();
            var looked = 0;
            foreach (var edge in identities)
            {
                var (a, b) = (graph.Node(edge.StartId), graph.Node(edge.EndId));
                static HashSet<string> Names(NodeValue n) => (n.Properties.GetValueOrDefault("aliases") as IReadOnlyList<object?> ?? []).OfType<string>()
                    .Append(n.Properties.GetValueOrDefault("name") as string ?? "").Where(s => s.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var shared = Names(a).Intersect(Names(b), StringComparer.OrdinalIgnoreCase).ToList();
                if (shared.Count > 0) failures.Add($"identity {edge.Id}: nodes {a.Id} and {b.Id} share the name(s) {string.Join(", ", shared)}");
                foreach (var spoilers in new[] { false, true })
                    foreach (var (chapter, pos) in boundaries)
                    {
                        var story = spoilers ? graph.StoryAt(pos) : null;
                        if (SpoilerGate.Visible(edge.Properties, pos, spoilers, story)) continue;
                        looked++;
                        var (_, edges, _) = View(graph, pos, spoilers);
                        var links = edges.Where(e => e.Type == Ontology.IdenticalTo).ToList();
                        var reached = new HashSet<long> { a.Id };
                        for (var grew = true; grew;)
                        {
                            grew = false;
                            foreach (var l in links)
                                if (reached.Contains(l.StartId) != reached.Contains(l.EndId)) { reached.Add(l.StartId); reached.Add(l.EndId); grew = true; }
                        }
                        if (reached.Contains(b.Id)) failures.Add($"identity {edge.Id}: at ch. {chapter}, spoilers {(spoilers ? "on" : "off")}, nodes {a.Id} and {b.Id} are joined before the reveal");
                    }
            }
            checks.Add(new LeakCheck("identity", looked, Listed(failures)));
        }

        // Referenced events.
        {
            var failures = new List<string>();
            var referenced = graph.Nodes.Where(n => n.Labels.Contains(Ontology.Event) && n.Properties.GetValueOrDefault("kind") as string == EventKinds.Referenced).ToList();
            foreach (var e in referenced)
            {
                var mentioned = graph.Outgoing(e.Id).Where(r => r.Type == Ontology.MentionedIn).ToList();
                if (mentioned.Count != 1) { failures.Add($"referenced event {e.Id} has {mentioned.Count} MENTIONED_IN edges"); continue; }
                var narrating = graph.Node(mentioned[0].EndId);
                if (narrating.Properties.GetValueOrDefault("kind") as string != EventKinds.Narrated) failures.Add($"referenced event {e.Id} is mentioned in {narrating.Id}, which is not a narrated event");
                if (Number(e.Properties.GetValueOrDefault("revealedPos")) is not { } own || Number(narrating.Properties.GetValueOrDefault("revealedPos")) is not { } theirs || own < theirs)
                    failures.Add($"referenced event {e.Id} is revealed before its narrating event {narrating.Id}");
            }
            checks.Add(new LeakCheck("referenced-events", referenced.Count, Listed(failures)));
        }

        // Clocks: nothing undated.
        {
            var failures = new List<string>();
            void Clock(string what, IReadOnlyDictionary<string, object?> p)
            {
                if (Number(p.GetValueOrDefault("revealedPos")) is not { } revealed) { failures.Add($"{what} has no revealedPos"); return; }
                if (p.GetValueOrDefault("storyFrom") is not string || p.GetValueOrDefault("storyFromLo") is not string) failures.Add($"{what} has no story time");
                if (Number(p.GetValueOrDefault("retractedPos")) is { } retracted && retracted <= revealed) failures.Add($"{what} is retracted at {retracted}, not after its reveal at {revealed}");
            }
            foreach (var n in graph.Nodes) Clock($"node {n.Id}", n.Properties);
            foreach (var r in graph.Relationships) Clock($"edge {r.Id} ({r.Type})", r.Properties);
            checks.Add(new LeakCheck("clocks", graph.Nodes.Count + graph.Relationships.Count, Listed(failures)));
        }
        return checks;
    }
}
