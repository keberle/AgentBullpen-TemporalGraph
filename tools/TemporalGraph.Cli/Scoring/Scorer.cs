using TemporalGraph.Cli.Indexing;

namespace TemporalGraph.Cli.Scoring;

/// <summary>A gold entity (from gold/&lt;book&gt;/chNN-NN.json).</summary>
public sealed record GoldItem(int Index, string Type, string Name, IReadOnlyList<string> Aliases, int FirstChapter, string FirstQuote)
{
    public IReadOnlyList<string> Names => [Name, .. Aliases];
}

/// <summary>A run entity (from stage-2.json), with a locator quote for reports.</summary>
public sealed record RunItem(int Id, string Type, string CanonicalName, IReadOnlyList<string> Aliases, int FirstChapter, string FirstQuote)
{
    public IReadOnlyList<string> Names => [CanonicalName, .. Aliases];
}

public sealed record ScoreFailure(string Tag, string Name, string Type, int Chapter, string Quote, string Detail, int? RunEntity, int? GoldEntity);

public sealed record MatchedPair(int RunEntity, int GoldEntity, string RunName, string GoldName, IReadOnlyList<string> SharedNames,
    IReadOnlyList<string> WrongAliases, IReadOnlyList<string> MissingAliases);

public sealed record Prf(int TruePositives, int Predicted, int Gold)
{
    private double P => Predicted == 0 ? 0 : TruePositives / (double)Predicted;
    private double R => Gold == 0 ? 0 : TruePositives / (double)Gold;
    public double Precision => Math.Round(P, 4);
    public double Recall => Math.Round(R, 4);
    public double F1 => P + R == 0 ? 0 : Math.Round(2 * P * R / (P + R), 4);
}

public sealed record ScoreResult(
    Prf Overall, IReadOnlyDictionary<string, Prf> ByType, IReadOnlyList<MatchedPair> Matches, IReadOnlyList<ScoreFailure> Failures)
{
    public int Count(string tag) => Failures.Count(f => f.Tag == tag);
}

/// <summary>
/// Scores a run's entities against gold. Definitions:
/// <list type="bullet">
/// <item><b>Names</b> of an entity: its canonical name and its aliases, normalized as in Stage 2 (case-folded,
/// curly quotes straightened, whitespace collapsed, trailing punctuation and a possessive 's dropped).</item>
/// <item><b>Candidate</b>: a run entity and a gold entity of the same type that share at least one name.</item>
/// <item><b>Match</b> (one-to-one): candidates are taken greedily — same canonical name first, then most shared
/// names, then gold order, then run order — and a pair is kept only if neither side is already matched.</item>
/// <item><b>Precision</b> = matches ÷ run entities; <b>recall</b> = matches ÷ gold entities; F1 their harmonic mean.</item>
/// <item><b>Wrong alias</b>: a name of a matched run entity that isn't a name of its gold entity; <b>missing alias</b>
/// (reported with the same tag, kind "missing"): a gold name the matched run entity lacks.</item>
/// <item><b>Wrong merge</b>: a run entity that is a candidate for more than one gold entity.
/// <b>Missed merge</b>: a gold entity that is a candidate for more than one run entity.</item>
/// <item><b>Wrong type</b>: an unmatched run entity that shares a name with a gold entity of the other type.</item>
/// <item><b>Out of scope</b>: an unmatched run entity whose names include a name the gold review rejected.</item>
/// <item><b>Spurious entity</b>: any other unmatched run entity. <b>Missed entity</b>: an unmatched gold entity.</item>
/// </list>
/// Every unmatched run entity counts against precision, whatever its tag.
/// </summary>
public static class Scorer
{
    public static string Norm(string name) => Stage2.NormalizeName(name);

    public static ScoreResult Score(IReadOnlyList<RunItem> run, IReadOnlyList<GoldItem> gold, IReadOnlyList<(string Type, string Name)> rejected)
    {
        var runNames = run.ToDictionary(r => r.Id, r => r.Names.Select(Norm).ToHashSet());
        var goldNames = gold.ToDictionary(g => g.Index, g => g.Names.Select(Norm).ToHashSet());
        var rejectedNames = rejected.Select(x => Norm(x.Name)).ToHashSet();

        var candidates = (from r in run
                          from g in gold
                          where r.Type == g.Type
                          let shared = runNames[r.Id].Intersect(goldNames[g.Index]).ToList()
                          where shared.Count > 0
                          select (Run: r, Gold: g, Shared: shared)).ToList();

        // One-to-one, greedy.
        var matchedRun = new Dictionary<int, GoldItem>();
        var matchedGold = new HashSet<int>();
        var pairs = new List<MatchedPair>();
        foreach (var c in candidates
                     .OrderByDescending(c => Norm(c.Run.CanonicalName) == Norm(c.Gold.Name))
                     .ThenByDescending(c => c.Shared.Count)
                     .ThenBy(c => c.Gold.Index)
                     .ThenBy(c => c.Run.Id))
        {
            if (matchedRun.ContainsKey(c.Run.Id) || matchedGold.Contains(c.Gold.Index)) continue;
            matchedRun[c.Run.Id] = c.Gold;
            matchedGold.Add(c.Gold.Index);
            var wrong = c.Run.Names.Where(n => !goldNames[c.Gold.Index].Contains(Norm(n))).Distinct().ToList();
            var missing = c.Gold.Names.Where(n => !runNames[c.Run.Id].Contains(Norm(n))).Distinct().ToList();
            pairs.Add(new MatchedPair(c.Run.Id, c.Gold.Index, c.Run.CanonicalName, c.Gold.Name, c.Shared, wrong, missing));
        }

        var failures = new List<ScoreFailure>();
        ScoreFailure RunFailure(string tag, RunItem r, string detail) =>
            new(tag, r.CanonicalName, r.Type, r.FirstChapter, r.FirstQuote, detail, r.Id, null);
        ScoreFailure GoldFailure(string tag, GoldItem g, string detail) =>
            new(tag, g.Name, g.Type, g.FirstChapter, g.FirstQuote, detail, null, g.Index);

        // Merge errors.
        foreach (var r in run)
        {
            var golds = candidates.Where(c => c.Run.Id == r.Id).Select(c => c.Gold.Name).Distinct().ToList();
            if (golds.Count > 1) failures.Add(RunFailure(FailureTags.WrongMerge, r, $"matches {golds.Count} gold entities: {string.Join(", ", golds)}"));
        }
        foreach (var g in gold)
        {
            var runs = candidates.Where(c => c.Gold.Index == g.Index).Select(c => c.Run.CanonicalName).ToList();
            if (runs.Count > 1) failures.Add(GoldFailure(FailureTags.MissedMerge, g, $"split across {runs.Count} run entities: {string.Join(", ", runs)}"));
        }

        // Alias quality for matches.
        foreach (var p in pairs)
        {
            var r = run.Single(x => x.Id == p.RunEntity);
            foreach (var a in p.WrongAliases) failures.Add(RunFailure(FailureTags.WrongAlias, r, $"run name \"{a}\" is not a name of gold \"{p.GoldName}\""));
            var g = gold.Single(x => x.Index == p.GoldEntity);
            foreach (var a in p.MissingAliases) failures.Add(GoldFailure(FailureTags.WrongAlias, g, $"missing: gold name \"{a}\" is not a name of run \"{p.RunName}\""));
        }

        // Unmatched run entities.
        foreach (var r in run.Where(r => !matchedRun.ContainsKey(r.Id)))
        {
            var otherType = gold.Where(g => g.Type != r.Type && goldNames[g.Index].Overlaps(runNames[r.Id])).Select(g => $"{g.Type} {g.Name}").ToList();
            if (otherType.Count > 0)
                failures.Add(RunFailure(FailureTags.WrongType, r, $"name matches gold {string.Join(", ", otherType)}"));
            else if (runNames[r.Id].Overlaps(rejectedNames))
                failures.Add(RunFailure(FailureTags.OutOfScope, r, "the gold review rejected this name (out of the gold's scope)"));
            else if (candidates.Any(c => c.Run.Id == r.Id))
                failures.Add(RunFailure(FailureTags.SpuriousEntity, r, "shares a name with a gold entity that matched another run entity (see missed merge)"));
            else
                failures.Add(RunFailure(FailureTags.SpuriousEntity, r, "no gold entity has any of its names"));
        }

        // Unmatched gold entities.
        foreach (var g in gold.Where(g => !matchedGold.Contains(g.Index)))
        {
            var otherType = run.Where(r => r.Type != g.Type && runNames[r.Id].Overlaps(goldNames[g.Index])).Select(r => $"{r.Type} {r.CanonicalName}").ToList();
            failures.Add(GoldFailure(FailureTags.MissedEntity, g, otherType.Count > 0
                ? $"found only with the wrong type: {string.Join(", ", otherType)}"
                : candidates.Any(c => c.Gold.Index == g.Index) ? "its names are inside a run entity matched to another gold entity (see wrong merge)" : "no run entity has any of its names"));
        }

        var overall = new Prf(pairs.Count, run.Count, gold.Count);
        var byType = new[] { "Character", "Location" }.ToDictionary(t => t, t => new Prf(
            pairs.Count(p => gold.Single(g => g.Index == p.GoldEntity).Type == t), run.Count(r => r.Type == t), gold.Count(g => g.Type == t)));
        return new ScoreResult(overall, byType, pairs, failures);
    }
}
