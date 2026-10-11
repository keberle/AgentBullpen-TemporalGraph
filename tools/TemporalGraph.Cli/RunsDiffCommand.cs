using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using TemporalGraph.Cli.Runs;

namespace TemporalGraph.Cli;

/// <summary><c>runs diff &lt;runA&gt; &lt;runB&gt;</c>: differences in checks, counts, chapter boundaries, and Stage 1/2 counts, entities and merges. Exit 0 = none, 1 = some.</summary>
public sealed class RunsDiffCommand : Command<RunsDiffCommand.Settings>
{
    public sealed class Settings : GlobalSettings
    {
        [CommandArgument(0, "<runA>")]
        [Description("Run id (under runs/) or run folder path.")]
        public string RunA { get; init; } = "";

        [CommandArgument(1, "<runB>")]
        [Description("Run id (under runs/) or run folder path.")]
        public string RunB { get; init; } = "";
    }

    public override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var runs = RepoPaths.Runs(RepoPaths.FindRoot());
        var a = RunDiff.Resolve(settings.RunA, runs);
        var b = RunDiff.Resolve(settings.RunB, runs);
        foreach (var dir in new[] { a, b })
            if (!Directory.Exists(dir))
            {
                AnsiConsole.MarkupLine($"[red]Run not found:[/] {Markup.Escape(dir)}");
                return 2;
            }

        var diffs = RunDiff.Compare(a, b);
        if (diffs.Count == 0)
        {
            AnsiConsole.MarkupLine($"[green]No differences[/] between {Markup.Escape(Path.GetFileName(a))} and {Markup.Escape(Path.GetFileName(b))} (checks, counts, chapter boundaries).");
            return 0;
        }

        var table = new Table().AddColumn($"Differences: A = {Markup.Escape(Path.GetFileName(a))}, B = {Markup.Escape(Path.GetFileName(b))}");
        foreach (var d in diffs) table.AddRow(Markup.Escape(d));
        AnsiConsole.Write(table);
        return 1;
    }
}
