using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Spectre.Console;
using Spectre.Console.Cli;
using TemporalGraph.Cli.Runs;
using TemporalGraph.Data;

namespace TemporalGraph.Cli;

/// <summary>
/// The graph's layout, computed once and stored in the book database, so the browser draws nodes where they are stored and
/// runs no force simulation. The step is a Node script, <c>web/scripts/layout.mjs</c>, run as a sidecar: it uses the libraries and
/// the force parameters the graph tabs used in the browser (d3-force-3d), over the whole graph, with a fixed seed and tick count.
/// Code only; no model.
/// </summary>
public static class GraphLayoutStep
{
    public const string FileName = "layout.json";

    /// <param name="Summary">What the script printed: algorithm, seed, iterations, nodes, edges, the settle alpha and radius of each
    /// layout, computedAt, milliseconds. Null when it did not run.</param>
    /// <param name="Missing">Why there is no layout (Node is not installed, the script or its packages are not there); null when there is one.</param>
    /// <param name="Error">The script ran and failed; null otherwise.</param>
    public sealed record Outcome(JsonObject? Summary, string? Missing = null, string? Error = null)
    {
        public bool Ok => Summary is not null;
    }

    public static string ScriptPath(string root) => Path.Combine(root, "web", "scripts", "layout.mjs");

    /// <summary>Adds the layout columns when the file lacks them, then runs the script on it. Never throws for a missing tool.</summary>
    public static Outcome Run(string root, string dbPath, TimeSpan? timeout = null)
    {
        var script = ScriptPath(root);
        if (!File.Exists(script)) return new(null, Missing: $"the layout script is not there ({Path.GetRelativePath(root, script)})");
        if (!Directory.Exists(Path.Combine(root, "web", "node_modules", "d3-force-3d")))
            return new(null, Missing: "the web packages are not installed (run npm install in web/)");
        if (!BookDatabase.HasCurrentSchema(dbPath) || !BookDatabase.HasLayoutColumns(dbPath)) BookDatabase.UpgradeInPlace(dbPath);
        try
        {
            var psi = new ProcessStartInfo("node") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = root };
            foreach (var a in new[] { "--no-warnings", script, Path.GetFullPath(dbPath) }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null) return new(null, Missing: "Node.js could not be started");
            var output = p.StandardOutput.ReadToEndAsync();
            var error = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeout ?? TimeSpan.FromMinutes(10)))
            {
                try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return new(null, Error: "the layout script did not finish in time");
            }
            if (p.ExitCode != 0) return new(null, Error: error.Result.Trim() is { Length: > 0 } e ? e : $"the layout script exited {p.ExitCode}");
            return JsonNode.Parse(output.Result.Trim().Split('\n')[^1]) is JsonObject summary ? new(summary) : new(null, Error: "the layout script printed no summary");
        }
        catch (Win32Exception)
        {
            return new(null, Missing: "Node.js is not installed (node is not on the PATH); the web then lays the graph out itself, in a worker");
        }
        catch (JsonException)
        {
            return new(null, Error: "the layout script's summary could not be read");
        }
    }

    /// <summary>What the provenance file records: <c>{ algorithm, seed, iterations, computedAt }</c> (and the measured values beside them).</summary>
    public static JsonObject Record(JsonObject summary) => (JsonObject)summary.DeepClone();

    /// <summary>One line for the console and run.log.</summary>
    public static string Line(Outcome o) => o.Summary is { } s
        ? $"layout: {s["nodes"]} nodes, {s["edges"]} edges · {s["algorithm"]}, seed {s["seed"]}, {s["iterations"]} ticks · settle alpha {s["alpha3"]} (3D), {s["alpha2"]} (2D) · {(s["milliseconds"]?.GetValue<long>() ?? 0) / 1000.0:0.0} s"
        : o.Missing is { } why ? $"layout missing: {why}" : $"layout failed: {o.Error}";
}

/// <summary>
/// <c>index layout --book &lt;id&gt; | --db &lt;path&gt;</c>: computes the layout of a book's graph and writes it into the database in place
/// (<see cref="GraphLayoutStep"/>). <c>--book</c> is the promoted file, <c>data/books/&lt;id&gt;.sqlite</c>, and its provenance file gets the
/// <c>layout</c> entry; no re-index. Exit 3 when Node.js or the web packages are missing.
/// </summary>
public sealed class IndexLayoutCommand : Command<IndexLayoutCommand.Settings>
{
    public sealed class Settings : GlobalSettings
    {
        [CommandOption("--book <ID>")][Description("A book id: the promoted data/books/<id>.sqlite is laid out in place and its provenance file updated.")] public string? Book { get; init; }
        [CommandOption("--db <PATH>")][Description("Any book database file, laid out in place.")] public string? Db { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings, CancellationToken ct) => Run(RepoPaths.FindRoot(), settings.Book, settings.Db);

    public static int Run(string root, string? book, string? db)
    {
        if ((book is null) == (db is null)) return IndexingShared.Fail("Give --book <id> or --db <path> (one of them).");
        var path = book is not null ? Path.Combine(RepoPaths.Books(root), book + ".sqlite") : Path.GetFullPath(db!);
        if (!File.Exists(path)) return IndexingShared.Fail($"Book database not found: {path}");

        var outcome = GraphLayoutStep.Run(root, path);
        if (outcome.Missing is not null) return CliContext.Problem(Exit.NotReady, GraphLayoutStep.Line(outcome), "layout-missing");
        if (!outcome.Ok) return CliContext.Problem(Exit.Failed, GraphLayoutStep.Line(outcome), "layout-failed");

        if (book is not null)
        {
            var provenance = Path.Combine(RepoPaths.Books(root), "provenance", book + ".json");
            if (File.Exists(provenance) && JsonNode.Parse(File.ReadAllText(provenance)) is JsonObject p)
            {
                p["layout"] = GraphLayoutStep.Record(outcome.Summary!);
                File.WriteAllText(provenance, p.ToJsonString(new JsonSerializerOptions(RunFolder.Json) { WriteIndented = true }) + "\n");
            }
        }
        AnsiConsole.MarkupLine(Markup.Escape(GraphLayoutStep.Line(outcome)));
        AnsiConsole.MarkupLine($"Wrote [bold]{Markup.Escape(Path.GetRelativePath(root, path))}[/]");
        return Exit.Ok;
    }
}
