using System.ComponentModel;
using System.Globalization;
using System.Text.Json.Nodes;
using Spectre.Console;
using Spectre.Console.Cli;
using TemporalGraph.Cli.Runs;
using TemporalGraph.Cli.Scoring;

namespace TemporalGraph.Cli;

/// <summary>
/// <c>index keep --baseline &lt;run&gt;… --candidate &lt;run&gt;… --baseline-heldout f1,… --candidate-heldout f1,…</c>:
/// applies <see cref="KeepRule"/> to scored runs. Tuning F1 comes from each run's scores.json, paired in the order given
/// (baseline i with candidate i). Held-out F1 values are passed in (from <c>index score --hide-holdout-details</c>), so no
/// held-out run folder is read; held-out runs are refused as tuning runs.
/// </summary>
public sealed class KeepCommand : Command<KeepCommand.Settings>
{
    public sealed class Settings : GlobalSettings
    {
        [CommandOption("--baseline <RUN>")][Description("Scored baseline runs on the tuning chapters, in pair order.")] public string[] Baseline { get; init; } = [];
        [CommandOption("--candidate <RUN>")][Description("Scored candidate runs, in the same pair order.")] public string[] Candidate { get; init; } = [];
        [CommandOption("--baseline-heldout <F1S>")][Description("Comma-separated held-out F1 per baseline run.")] public string BaselineHeldOut { get; init; } = "";
        [CommandOption("--candidate-heldout <F1S>")][Description("Comma-separated held-out F1 per candidate run.")] public string CandidateHeldOut { get; init; } = "";
    }

    public override int Execute(CommandContext context, Settings settings, CancellationToken ct)
    {
        var root = RepoPaths.FindRoot();
        try
        {
            var baseline = settings.Baseline.Select(r => RunFolder.Open(RepoPaths.Runs(root), r)).ToList();
            var candidate = settings.Candidate.Select(r => RunFolder.Open(RepoPaths.Runs(root), r)).ToList();
            var plan = KeepRule.PlanDifferences([.. baseline, .. candidate]);
            if (plan.Count > 0) return IndexingShared.Fail("Runs differ in chapters or chunk plan:\n" + string.Join("\n", plan));
            var decision = KeepRule.Decide(baseline.Select(TuningF1).ToList(), candidate.Select(TuningF1).ToList(),
                ParseF1s(settings.BaselineHeldOut), ParseF1s(settings.CandidateHeldOut));
            AnsiConsole.MarkupLine(Markup.Escape(
                $"{(decision.Keep ? "KEEP" : "REVERT")} · N {decision.Runs} · mean diff {decision.MeanDiff:+0.0000;-0.0000} · SE {decision.StandardError:0.0000} · margin {decision.Margin:0.0000} · held-out diff {decision.HeldOutMeanDiff:+0.0000;-0.0000}"));
            AnsiConsole.MarkupLine(Markup.Escape(decision.Reason));
            return 0;
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or FileNotFoundException or ArgumentException or InvalidOperationException or FormatException)
        {
            return IndexingShared.Fail(ex.Message);
        }
    }

    /// <summary>A tuning run's overall F1 from scores.json; held-out runs are refused.</summary>
    public static double TuningF1(RunFolder run)
    {
        var runJson = JsonNode.Parse(File.ReadAllText(run.File(RunFolder.RunJson)))!;
        var (first, last) = (runJson["chapters"]!["first"]!.GetValue<int>(), runJson["chapters"]!["last"]!.GetValue<int>());
        if (HoldOut.Touches(runJson["book"]!.GetValue<string>(), first, last))
            throw new InvalidOperationException($"{run.RunId} is a held-out run; pass its F1 with --baseline-heldout/--candidate-heldout instead.");
        if (!File.Exists(run.File(ScoreCommand.ScoresJson))) throw new FileNotFoundException($"{run.RunId} has no scores.json; run index score first.");
        return JsonNode.Parse(File.ReadAllText(run.File(ScoreCommand.ScoresJson)))!["overall"]!["f1"]!.GetValue<double>();
    }

    public static List<double> ParseF1s(string text) =>
        text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => double.Parse(x, NumberStyles.Float, CultureInfo.InvariantCulture)).ToList();
}
