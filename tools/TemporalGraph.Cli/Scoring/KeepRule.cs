using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using TemporalGraph.Cli.Runs;

namespace TemporalGraph.Cli.Scoring;

/// <summary>The outcome of <see cref="KeepRule.Decide"/>, with every number it used.</summary>
/// <param name="MeanDiff">Mean of the paired tuning-set F1 differences (candidate − baseline).</param>
/// <param name="StandardError">Standard error of that mean: sample sd of the differences ÷ √N.</param>
/// <param name="Margin"><see cref="KeepRule.Multiplier"/> × <paramref name="StandardError"/>.</param>
/// <param name="HeldOutMeanDiff">Mean of the paired held-out F1 differences (aggregate F1 only).</param>
public sealed record KeepDecision(bool Keep, int Runs, double MeanDiff, double StandardError, double Margin, double HeldOutMeanDiff, string Reason);

/// <summary>
/// The improvement loop's keep rule, in one place. A paired comparison: the baseline and the candidate are each
/// run N times on the same chapters with the same chunk plan, and run i of one is paired with run i of the other.
/// <list type="bullet">
/// <item>d_i = candidate F1_i − baseline F1_i on the tuning chapters; SE = sd(d) / √N (sample sd).</item>
/// <item><b>Keep</b> only if mean(d) &gt; 2 × SE, <b>and</b> the held-out mean F1 difference ≥ −(2 × SE), the same margin.</item>
/// </list>
/// N is <c>Indexing:Loop:Runs</c> (default 5). With SE = 0 (identical differences in every pair) the margin is 0, so any
/// positive difference is kept.
/// </summary>
public static class KeepRule
{
    public const int DefaultRuns = 5;
    public const double Multiplier = 2.0;

    /// <summary>N from <c>Indexing:Loop:Runs</c>; at least 2 (a standard error needs two pairs).</summary>
    public static int Runs(IConfiguration config)
    {
        var n = config.GetSection("Indexing:Loop").GetValue("Runs", DefaultRuns);
        if (n < 2) throw new InvalidOperationException($"Indexing:Loop:Runs must be at least 2 (found {n}).");
        return n;
    }

    public static KeepDecision Decide(IReadOnlyList<double> baselineTune, IReadOnlyList<double> candidateTune,
        IReadOnlyList<double> baselineHeldOut, IReadOnlyList<double> candidateHeldOut)
    {
        var n = baselineTune.Count;
        if (n < 2) throw new ArgumentException("A paired comparison needs at least 2 runs per version.", nameof(baselineTune));
        if (candidateTune.Count != n || baselineHeldOut.Count != n || candidateHeldOut.Count != n)
            throw new ArgumentException($"Every list must have the same N runs (tuning {n}/{candidateTune.Count}, held-out {baselineHeldOut.Count}/{candidateHeldOut.Count}).");

        var d = candidateTune.Zip(baselineTune, (c, b) => c - b).ToList();
        var mean = d.Average();
        var sd = Math.Sqrt(d.Sum(x => (x - mean) * (x - mean)) / (n - 1));
        var se = sd / Math.Sqrt(n);
        var margin = Multiplier * se;
        var held = candidateHeldOut.Zip(baselineHeldOut, (c, b) => c - b).Average();

        var improves = mean > margin;
        var heldOk = held >= -margin;
        var reason = (improves, heldOk) switch
        {
            (true, true) => $"kept: tuning F1 +{mean:0.0000} > 2×SE {margin:0.0000}; held-out {held:+0.0000;-0.0000} ≥ −{margin:0.0000}",
            (false, _) => $"reverted: tuning F1 {mean:+0.0000;-0.0000} is not > 2×SE {margin:0.0000}",
            (true, false) => $"reverted: held-out F1 {held:+0.0000;-0.0000} fell by more than 2×SE {margin:0.0000}",
        };
        return new KeepDecision(improves && heldOk, n, mean, se, margin, held, reason);
    }

    /// <summary>
    /// "Same chapters, same chunk plan": the book, chapters, text hash and chunk settings in each run.json. Returns the
    /// differences (empty when every run matches the first).
    /// </summary>
    public static IReadOnlyList<string> PlanDifferences(IReadOnlyList<RunFolder> runs)
    {
        static string Plan(RunFolder r)
        {
            var j = JsonNode.Parse(File.ReadAllText(r.File(RunFolder.RunJson)))!;
            var s = j["settings"];
            return $"book {j["book"]} · chapters {j["chapters"]?["first"]}–{j["chapters"]?["last"]} · text {j["textSha256"]} · chunks {s?["chunkTokens"]}/{s?["overlapTokens"]} · glean {s?["glean"]}";
        }
        if (runs.Count == 0) return [];
        var first = Plan(runs[0]);
        return runs.Skip(1).Select(r => (r.RunId, Plan: Plan(r))).Where(x => x.Plan != first).Select(x => $"{x.RunId}: {x.Plan} (expected {first})").ToList();
    }
}
