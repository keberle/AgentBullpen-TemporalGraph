using System.Text.Json;
using Microsoft.Extensions.Configuration;
using TemporalGraph.Cli.Runs;
using TemporalGraph.Cli.Scoring;

namespace TemporalGraph.Data.Tests;

// The loop's paired keep rule on synthetic numbers.
public class KeepRuleTests
{
    private static readonly double[] Base = [0.70, 0.72, 0.71, 0.73, 0.69];

    [Fact]
    public void A_consistent_gain_larger_than_two_standard_errors_is_kept()
    {
        // d = +0.03, +0.02, +0.04, +0.03, +0.03 → mean 0.03, sd 0.00707, SE 0.00316, margin 0.00632.
        double[] cand = [0.73, 0.74, 0.75, 0.76, 0.72];
        var d = KeepRule.Decide(Base, cand, Base, Base);

        Assert.True(d.Keep, d.Reason);
        Assert.Equal(5, d.Runs);
        Assert.Equal(0.03, d.MeanDiff, 10);
        Assert.Equal(0.0070711 / Math.Sqrt(5), d.StandardError, 6);
        Assert.Equal(2 * d.StandardError, d.Margin, 12);
        Assert.Equal(0, d.HeldOutMeanDiff, 12);
    }

    [Fact]
    public void A_gain_within_the_noise_is_reverted()
    {
        // d = +0.06, −0.04, +0.05, −0.03, +0.01 → mean 0.01, SE ≈ 0.0207: not > 2×SE.
        double[] cand = [0.76, 0.68, 0.76, 0.70, 0.70];
        var d = KeepRule.Decide(Base, cand, Base, Base);

        Assert.False(d.Keep);
        Assert.Equal(0.01, d.MeanDiff, 10);
        Assert.StartsWith("reverted: tuning", d.Reason);
    }

    [Fact]
    public void A_held_out_fall_bigger_than_the_same_margin_reverts_a_real_gain()
    {
        double[] cand = [0.73, 0.74, 0.75, 0.76, 0.72];            // margin ≈ 0.0063
        double[] heldBase = [0.75, 0.75, 0.75, 0.75, 0.75];
        double[] heldSmallFall = [0.745, 0.745, 0.745, 0.745, 0.745]; // −0.005: within the margin
        double[] heldBigFall = [0.74, 0.74, 0.74, 0.74, 0.74];        // −0.01: beyond it

        Assert.True(KeepRule.Decide(Base, cand, heldBase, heldSmallFall).Keep);
        var d = KeepRule.Decide(Base, cand, heldBase, heldBigFall);
        Assert.False(d.Keep);
        Assert.StartsWith("reverted: held-out", d.Reason);
    }

    [Fact]
    public void A_drop_is_reverted_and_identical_runs_have_a_zero_margin()
    {
        Assert.False(KeepRule.Decide(Base, Base.Select(x => x - 0.01).ToList(), Base, Base).Keep);

        // Every pair differs by the same +0.001: SE 0, margin 0, so the gain is kept.
        var d = KeepRule.Decide(Base, Base.Select(x => x + 0.001).ToList(), Base, Base);
        Assert.True(d.Keep);
        Assert.Equal(0, d.Margin, 12);
        // No difference at all is not an improvement.
        Assert.False(KeepRule.Decide(Base, Base, Base, Base).Keep);
    }

    [Fact]
    public void The_lists_must_be_paired_and_N_is_configurable()
    {
        Assert.Throws<ArgumentException>(() => KeepRule.Decide([0.7], [0.8], [0.7], [0.8]));
        Assert.Throws<ArgumentException>(() => KeepRule.Decide(Base, Base.Take(4).ToList(), Base, Base));
        Assert.Throws<ArgumentException>(() => KeepRule.Decide(Base, Base, Base, Base.Take(3).ToList()));

        Assert.Equal(5, KeepRule.Runs(new ConfigurationBuilder().Build()));
        Assert.Equal(8, KeepRule.Runs(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Indexing:Loop:Runs"] = "8" }).Build()));
        Assert.Throws<InvalidOperationException>(() => KeepRule.Runs(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Indexing:Loop:Runs"] = "1" }).Build()));
        var template = new ConfigurationBuilder().AddJsonFile(Path.Combine(AppContext.BaseDirectory, "templates", "cli.appsettings.json")).Build();
        Assert.Equal(5, KeepRule.Runs(template));
    }

    [Fact]
    public void Index_keep_reads_tuning_f1_from_scores_and_refuses_held_out_runs()
    {
        var root = Path.Combine(Path.GetTempPath(), "TemporalGraph.Data.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            RunFolder Run(string id, int first, int last, double f1)
            {
                Directory.CreateDirectory(Path.Combine(root, id));
                File.WriteAllText(Path.Combine(root, id, RunFolder.RunJson), JsonSerializer.Serialize(new { book = "hound-of-the-baskervilles", chapters = new { first, last } }));
                File.WriteAllText(Path.Combine(root, id, TemporalGraph.Cli.ScoreCommand.ScoresJson), JsonSerializer.Serialize(new { overall = new { f1 } }));
                return RunFolder.Open(root, Path.Combine(root, id));
            }
            Assert.Equal(0.7123, TemporalGraph.Cli.KeepCommand.TuningF1(Run("tune", 1, 3, 0.7123)));
            var held = Run("held", 8, 8, 0.8);
            Assert.Throws<InvalidOperationException>(() => TemporalGraph.Cli.KeepCommand.TuningF1(held));
            Assert.Equal([0.75, 0.8], TemporalGraph.Cli.KeepCommand.ParseF1s("0.75, 0.8"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Runs_must_share_chapters_and_chunk_plan()
    {
        var root = Path.Combine(Path.GetTempPath(), "TemporalGraph.Data.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            RunFolder Run(string id, int last, int chunkTokens)
            {
                Directory.CreateDirectory(Path.Combine(root, id));
                File.WriteAllText(Path.Combine(root, id, RunFolder.RunJson), JsonSerializer.Serialize(new
                {
                    book = "hound-of-the-baskervilles", chapters = new { first = 1, last }, textSha256 = "abc",
                    settings = new { chunkTokens, overlapTokens = 100, glean = false },
                }));
                return RunFolder.Open(root, Path.Combine(root, id));
            }
            var a = Run("a", 3, 1000);
            var b = Run("b", 3, 1000);
            Assert.Empty(KeepRule.PlanDifferences([a, b]));
            Assert.Single(KeepRule.PlanDifferences([a, b, Run("c", 4, 1000)]));
            Assert.Single(KeepRule.PlanDifferences([a, Run("d", 3, 800)]));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}
