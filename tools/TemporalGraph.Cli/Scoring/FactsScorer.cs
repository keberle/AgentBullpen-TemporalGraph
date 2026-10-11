using TemporalGraph.Cli.Gold;
using TemporalGraph.Cli.Indexing;
using TemporalGraph.Data;

namespace TemporalGraph.Cli.Scoring;

// Facts scoring: a run's Stage 3/4 items against facts gold, with the match rule (by evidence, not wording).

/// <summary>A run item as the scorer sees it.</summary>
/// <param name="Kind">"edge" or "node".</param>
/// <param name="Type">Edge type, or node label.</param>
/// <param name="Entities">Edge ends, or the node's linked entities (run entity ids).</param>
/// <param name="HasStates">A node with a STATES link (a claim).</param>
/// <param name="UnstableDate">Stage 4 voting left the date null because the samples disagreed.</param>
public sealed record FactPrediction(string Id, string Kind, string Type, int? Source, int? Target, IReadOnlySet<int> Entities, bool HasStates,
    int EvidenceStart, int EvidenceEnd, int RevealedChapter, string? StoryFrom, string? StoryUntil, bool UnstableDate);

public sealed record FactFailure(string Tag, string? GoldNumber, string? RunId, string Detail);

/// <param name="Story">Over matched items: "exact" (same normalized EDTF, or both undated), "compatible" (both dated and the
/// intervals overlap) or "wrong".</param>
public sealed record FactMatch(string GoldNumber, string RunId, bool RevealedCorrect, string Story);

public sealed record FactsScoreResult(Prf Overall, IReadOnlyList<FactMatch> Matches, IReadOnlyList<FactFailure> Failures)
{
    public int Count(string tag) => Failures.Count(f => f.Tag == tag);
    public double RevealedAccuracy => Matches.Count == 0 ? 0 : Math.Round(Matches.Count(m => m.RevealedCorrect) / (double)Matches.Count, 4);
    public int Story(string kind) => Matches.Count(m => m.Story == kind);
}

public static class FactsScorer
{
    /// <summary>Run items from Stage 3 (and Stage 4, when present). The revealed chapter comes from the evidence start.</summary>
    public static List<FactPrediction> Predictions(Stage3Result stage3, Stage4Result? stage4, Func<int, int> chapterOf)
    {
        var times = (stage4?.Items ?? []).ToDictionary(i => i.Id);
        FactPrediction P(string id, string kind, string type, int? s, int? t, IEnumerable<int> entities, bool states, EvidenceMatch m)
        {
            times.TryGetValue(id, out var time);
            return new FactPrediction(id, kind, type, s, t, entities.ToHashSet(), states, m.CharStart, m.CharEnd, chapterOf(m.CharStart),
                time?.StoryFrom?.Edtf, time?.StoryUntil?.Edtf, time?.UnstableDate ?? false);
        }
        return stage3.Edges.Select(e => P(e.Id, "edge", e.Type, e.Source, e.Target, [e.Source, e.Target], false, e.EvidenceMatch))
            .Concat(stage3.Nodes.Select(n => P(n.Id, "node", n.Label, null, null, n.Links.Select(l => l.Entity), n.Links.Any(l => l.Type == Ontology.States), n.EvidenceMatch)))
            .ToList();
    }

    /// <summary>Gold entity name → the run's entity ids with that name (canonical or alias), by <see cref="Stage2.NormalizeName"/>.</summary>
    public static Func<string, IReadOnlySet<int>> EntityIds(Stage2Result stage2)
    {
        var byName = new Dictionary<string, HashSet<int>>();
        foreach (var e in stage2.Entities)
            foreach (var name in e.Aliases.Append(e.CanonicalName))
            {
                var key = Stage2.NormalizeName(name);
                if (!byName.TryGetValue(key, out var set)) byName[key] = set = [];
                set.Add(e.Id);
            }
        return name => byName.TryGetValue(Stage2.NormalizeName(name), out var ids) ? ids : new HashSet<int>();
    }

    /// <summary>
    /// The scoring rules:
    /// <list type="number">
    /// <item><b>Match</b> (true positive): the same kind and type, overlapping evidence spans, and the same endpoints: an
    /// edge's source and target (either order for MET and IDENTICAL_TO), or a node's set of linked entities. A gold name
    /// stands for every run entity with that canonical name or alias. One-to-one; the largest overlap first.</item>
    /// <item>Of the rest, pairs with overlapping evidence are explained, in this order: <c>stated as fact</c> (the gold item
    /// is a Fact with a STATES link; the run's item shares an entity but has no STATES link); <c>wrong type</c> (the same
    /// entities, another type); <c>wrong endpoint</c> (the same type, other entities). Each such pair counts as a miss and
    /// a spurious item.</item>
    /// <item>Gold items left are <c>missed fact</c>; run items left are <c>spurious fact</c>.</item>
    /// </list>
    /// Precision = matches / run items; recall = matches / gold items. Over matches: the revealed chapter must equal the
    /// gold's, and the story date is exact, compatible or wrong (<see cref="StoryAgreement"/>); wrong dates are tagged
    /// <c>invented date</c> (run dated, gold undated), <c>unstable date</c> (gold dated, run left it null by voting) or
    /// <c>wrong date</c>.
    /// </summary>
    public static FactsScoreResult Score(IReadOnlyList<FactPrediction> run, IReadOnlyList<GoldFact> gold, Func<string, IReadOnlySet<int>> idsOf)
    {
        static bool Overlap(FactPrediction p, GoldFact g) => p.EvidenceStart < g.EvidenceEnd && g.EvidenceStart < p.EvidenceEnd;
        static int OverlapLength(FactPrediction p, GoldFact g) => Math.Min(p.EvidenceEnd, g.EvidenceEnd) - Math.Max(p.EvidenceStart, g.EvidenceStart);
        bool SameEntities(FactPrediction p, GoldFact g)
        {
            var names = g.EntityNames;
            if (names.Count != p.Entities.Count) return false;
            var used = new HashSet<int>();
            foreach (var name in names)
            {
                var id = idsOf(name).Where(p.Entities.Contains).Where(i => !used.Contains(i)).Cast<int?>().FirstOrDefault();
                if (id is null) return false;
                used.Add(id.Value);
            }
            return true;
        }
        bool SameEnds(FactPrediction p, GoldFact g)
        {
            if (p.Kind != "edge" || g.Kind != "edge") return SameEntities(p, g);
            var (s, t) = (idsOf(g.Source!), idsOf(g.Target!));
            return (s.Contains(p.Source!.Value) && t.Contains(p.Target!.Value))
                || (g.Type is Ontology.Met or Ontology.IdenticalTo && s.Contains(p.Target!.Value) && t.Contains(p.Source!.Value));
        }
        bool SharesEntity(FactPrediction p, GoldFact g) => g.EntityNames.Any(n => idsOf(n).Any(p.Entities.Contains));

        var matches = new List<FactMatch>();
        var failures = new List<FactFailure>();
        var freeRun = run.ToList();
        var freeGold = gold.ToList();

        void Pair(Func<FactPrediction, GoldFact, bool> rule, Action<FactPrediction, GoldFact> onPair)
        {
            var candidates = (from g in freeGold from p in freeRun where Overlap(p, g) && rule(p, g) select (p, g))
                .OrderByDescending(x => OverlapLength(x.p, x.g)).ThenBy(x => x.p.EvidenceStart).ToList();
            foreach (var (p, g) in candidates)
            {
                if (!freeRun.Contains(p) || !freeGold.Contains(g)) continue;
                freeRun.Remove(p);
                freeGold.Remove(g);
                onPair(p, g);
            }
        }

        Pair((p, g) => p.Kind == g.Kind && p.Type == g.Type && SameEnds(p, g), (p, g) =>
        {
            var story = StoryAgreement(p.StoryFrom, p.StoryUntil, g.StoryFrom, g.StoryUntil);
            var revealed = p.RevealedChapter == g.RevealedChapter;
            matches.Add(new FactMatch(g.Number, p.Id, revealed, story));
            if (story == "wrong")
            {
                var goldDated = g.StoryFrom is not null || g.StoryUntil is not null;
                var runDated = p.StoryFrom is not null || p.StoryUntil is not null;
                var tag = !goldDated ? FailureTags.InventedDate : !runDated && p.UnstableDate ? FailureTags.UnstableDate : FailureTags.WrongDate;
                failures.Add(new FactFailure(tag, g.Number, p.Id, $"run {Show(p.StoryFrom, p.StoryUntil)}, gold {Show(g.StoryFrom, g.StoryUntil)}"));
            }
        });
        Pair((p, g) => g.Kind == "node" && g.Type == Ontology.Fact && g.Links.Any(l => l.Type == Ontology.States) && !p.HasStates && SharesEntity(p, g),
            (p, g) => failures.Add(new FactFailure(FailureTags.StatedAsFact, g.Number, p.Id, $"gold is a claim (STATES); run has {p.Type}")));
        Pair((p, g) => (p.Kind != g.Kind || p.Type != g.Type) && SameEntities(p, g),
            (p, g) => failures.Add(new FactFailure(FailureTags.WrongType, g.Number, p.Id, $"run {p.Type}, gold {g.Type}")));
        Pair((p, g) => p.Kind == g.Kind && p.Type == g.Type,
            (p, g) => failures.Add(new FactFailure(FailureTags.WrongEndpoint, g.Number, p.Id, $"{g.Type}: gold {string.Join(", ", g.EntityNames)}")));
        failures.AddRange(freeGold.Select(g => new FactFailure(FailureTags.MissedFact, g.Number, null, $"{g.Type} {g.Text ?? $"{g.Source} → {g.Target}"}")));
        failures.AddRange(freeRun.Select(p => new FactFailure(FailureTags.SpuriousFact, null, p.Id, p.Type)));

        return new FactsScoreResult(new Prf(matches.Count, run.Count, gold.Count), matches, failures);
    }

    private static string Show(string? from, string? until) => from is null && until is null ? "—" : until is null ? from! : $"{from ?? "?"} until {until}";

    /// <summary>
    /// "exact": both undated, or the same normalized EDTF (<see cref="Edtf.Normalize"/>) for from and until; "compatible":
    /// both dated and the intervals overlap (from's lo to until's hi, or from's hi without an until; an unknown bound is
    /// open); "wrong" otherwise (one dated and the other not, or no overlap).
    /// </summary>
    public static string StoryAgreement(string? runFrom, string? runUntil, string? goldFrom, string? goldUntil)
    {
        var runDated = runFrom is not null || runUntil is not null;
        var goldDated = goldFrom is not null || goldUntil is not null;
        if (!runDated && !goldDated) return "exact";
        if (runDated != goldDated) return "wrong";
        static string? N(string? x) => x is null ? null : Edtf.Normalize(x);
        if (N(runFrom) == N(goldFrom) && N(runUntil) == N(goldUntil)) return "exact";
        static (DateOnly? Lo, DateOnly? Hi) Range(string? from, string? until)
        {
            var f = from is null ? null : Edtf.Parse(from);
            var u = until is null ? null : Edtf.Parse(until);
            return (f?.Lo, u is null ? f?.Hi : u.Hi);
        }
        var (a, b) = (Range(runFrom, runUntil), Range(goldFrom, goldUntil));
        var overlap = (a.Lo is null || b.Hi is null || a.Lo <= b.Hi) && (b.Lo is null || a.Hi is null || b.Lo <= a.Hi);
        return overlap ? "compatible" : "wrong";
    }
}
