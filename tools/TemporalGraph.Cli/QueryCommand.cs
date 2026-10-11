using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Spectre.Console;
using Spectre.Console.Cli;
using TemporalGraph.Cli.Runs;
using TemporalGraph.Cli.Telemetry;
using TemporalGraph.Query;

namespace TemporalGraph.Cli;

/// <summary>
/// <c>query --book &lt;id&gt; | --db &lt;path&gt; (--text "&lt;cypher&gt;" | --file &lt;path&gt;) [--chapter N] [--param name=value]… [--json]</c>:
/// runs an openCypher query (the TemporalGraph.Query subset) on a book database, read-only. <c>--chapter N</c> sets
/// <c>$pos</c> to the end of chapter N and <c>$chapter</c> to N. No hidden time filter: spoiler handling is in the query.
/// </summary>
public sealed class QueryCommand : AsyncCommand<QueryCommand.Settings>
{
    public sealed class Settings : GlobalSettings
    {
        [CommandOption("--book <ID>")][Description("Open data/books/<id>.sqlite.")] public string? Book { get; init; }
        [CommandOption("--db <PATH>")][Description("Open any book database file, e.g. runs/<runId>/book.sqlite.")] public string? Db { get; init; }
        [CommandOption("--text <CYPHER>")][Description("The query text.")] public string? Text { get; init; }
        [CommandOption("--file <PATH>")][Description("A query file, e.g. queries/hound-of-the-baskervilles/who-met.cypher.")] public string? File { get; init; }
        [CommandOption("--chapter <N>")][Description("Set $pos to the end of chapter N (chapters table) and $chapter to N.")] public int? Chapter { get; init; }
        [CommandOption("--spoilers")][Description("Set $spoilers to true (default false, when the query uses it).")] public bool Spoilers { get; init; }
        [CommandOption("--character <NAME>")][Description("Set $character.")] public string? Character { get; init; }
        [CommandOption("--param <NAME=VALUE>")][Description("A parameter; repeat. Values that parse as numbers become numbers; true/false become booleans.")] public string[] Param { get; init; } = [];
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        var root = RepoPaths.FindRoot();
        if ((settings.Book is null) == (settings.Db is null)) return IndexingShared.Fail("Give --book <id> or --db <path>.");
        if (settings.Text is not null && settings.File is not null) return IndexingShared.Fail("Give --text \"<cypher>\" or --file <path>, not both.");

        var dbPath = settings.Book is { } book ? Path.Combine(RepoPaths.Books(root), book + ".sqlite") : Resolve(root, settings.Db!);
        if (!System.IO.File.Exists(dbPath)) return CliContext.Fail(Exit.NotReady, $"Book database not found: {dbPath}", "book-not-indexed");

        // No query given → the console at a terminal; without one, the one-shot usage and exit 2.
        if (settings.Text is null && settings.File is null)
        {
            if (!CliContext.Interactive)
                return CliContext.NeedsTerminal("query without --text or --file opens the query console", $"query --book {settings.Book ?? "<id>"} --text \"<cypher>\" | --file <path> [--chapter N] [--spoilers] [--character <name>] [--json]");
            var open = BookGraph.Load(dbPath);
            if (open.Chapters.Count == 0) return CliContext.Fail(Exit.NotReady, "The book has no chapters yet (run `index run`).", "book-not-indexed");
            if (settings.Chapter is { } asked && !open.Chapters.ContainsKey(asked)) return IndexingShared.Fail($"No chapter {asked} in the chapters table.");
            var session = new Repl.QuerySession(open, settings.Book ?? BookOfRun(dbPath) ?? Path.GetFileNameWithoutExtension(dbPath), root,
                settings.Chapter ?? open.Chapters.Keys.Max(), settings.Spoilers, settings.Character);
            return await new Repl.QueryConsole(session, AnsiConsole.Console, new Repl.LineEditor()).RunAsync(ct);
        }
        string query;
        if (settings.File is { } file)
        {
            var path = Resolve(root, file);
            if (!System.IO.File.Exists(path)) return IndexingShared.Fail($"Query file not found: {file}");
            query = System.IO.File.ReadAllText(path);
        }
        else query = settings.Text!;

        using var span = IndexingTelemetry.Source.StartActivity("query");
        span?.SetTag(IndexingTelemetry.BookId, settings.Book ?? BookOfRun(dbPath));
        if (settings.File is not null) span?.SetTag("query.file", settings.File.Replace('\\', '/'));

        var watch = Stopwatch.StartNew();
        var graph = BookGraph.Load(dbPath);
        var loadMs = watch.Elapsed.TotalMilliseconds;

        var parameters = new Dictionary<string, object?>();
        if (settings.Chapter is { } n)
        {
            if (!graph.Chapters.TryGetValue(n, out var ch)) return IndexingShared.Fail($"No chapter {n} in the chapters table.");
            parameters["pos"] = (long)ch.CharEnd;
            parameters["chapter"] = (long)n;
        }
        foreach (var p in settings.Param)
        {
            var eq = p.IndexOf('=');
            if (eq <= 0) return IndexingShared.Fail($"--param must look like name=value: {p}");
            parameters[p[..eq]] = ParseValue(p[(eq + 1)..]);
        }
        // --spoilers and --character are $spoilers and $character; a query that uses $spoilers without one being given gets false.
        if (settings.Spoilers) parameters["spoilers"] = true;
        else if (!parameters.ContainsKey("spoilers") && Repl.QueryFiles.ParametersOf(query).Contains("spoilers")) parameters["spoilers"] = false;
        if (settings.Character is { } who) parameters["character"] = who;

        QueryResult result;
        watch.Restart();
        try { result = CypherEngine.Execute(graph, query, parameters); }
        catch (CypherException ex)
        {
            span?.SetStatus(ActivityStatusCode.Error, ex.Reason);
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Reason)}[/] at line {ex.Line}, column {ex.Column + 1}");
            var line = query.Replace("\r\n", "\n").Split('\n').ElementAtOrDefault(ex.Line - 1);
            if (line is not null)
            {
                AnsiConsole.WriteLine("  " + line);
                AnsiConsole.WriteLine("  " + new string(' ', Math.Max(0, ex.Column)) + "^");
            }
            return CliContext.Problem(Exit.BadInput, $"{ex.Reason} at line {ex.Line}, column {ex.Column + 1}", "query-error");
        }
        var queryMs = watch.Elapsed.TotalMilliseconds;
        span?.SetTag("query.rows", result.Rows.Count);
        span?.SetTag("query.elapsed_ms", Math.Round(queryMs, 2));
        span?.SetTag("query.load_ms", Math.Round(loadMs, 2));

        if (settings.Json)
        {
            Console.WriteLine(ResultJson(result, parameters, queryMs, loadMs).ToJsonString(new JsonSerializerOptions(RunFolder.Json) { WriteIndented = true }));
            return 0;
        }
        var table = new Table();
        foreach (var c in result.Columns) table.AddColumn(Markup.Escape(c));
        foreach (var row in result.Rows) table.AddRow(row.Select(v => Markup.Escape(CypherValues.Display(v))).ToArray());
        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"{result.Rows.Count} row(s) · query {queryMs:0.0} ms · load {loadMs:0.0} ms" +
            (parameters.Count > 0 ? " · " + Markup.Escape(string.Join(", ", parameters.Select(kv => $"${kv.Key} = {CypherValues.Display(kv.Value)}"))) : ""));
        return 0;
    }

    /// <summary>The <c>--json</c> result: { columns, rows, parameters, elapsedMs, loadMs }.</summary>
    public static JsonObject ResultJson(QueryResult result, IReadOnlyDictionary<string, object?> parameters, double queryMs, double? loadMs)
    {
        var json = new JsonObject
        {
            ["columns"] = new JsonArray(result.Columns.Select(c => (JsonNode?)c).ToArray()),
            ["rows"] = new JsonArray(result.Rows.Select(r => (JsonNode?)new JsonObject(result.Columns.Select((c, i) => KeyValuePair.Create(c, ToJson(r[i]))))).ToArray()),
            ["parameters"] = new JsonObject(parameters.Select(kv => KeyValuePair.Create(kv.Key, ToJson(kv.Value)))),
            ["elapsedMs"] = Math.Round(queryMs, 2),
        };
        if (loadMs is { } load) json["loadMs"] = Math.Round(load, 2);
        return json;
    }

    private static string Resolve(string root, string path) =>
        Path.IsPathRooted(path) || System.IO.File.Exists(path) ? path : Path.Combine(root, path);

    /// <summary>The book id from the run.json next to a run's book.sqlite, if there is one.</summary>
    public static string? BookOfRun(string dbPath)
    {
        var runJson = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dbPath))!, RunFolder.RunJson);
        try { return System.IO.File.Exists(runJson) ? JsonNode.Parse(System.IO.File.ReadAllText(runJson))?["book"]?.GetValue<string>() : null; }
        catch (JsonException) { return null; }
    }

    /// <summary>An integer, then a number, then true/false (e.g. --param spoilers=true), else the text as a string.</summary>
    public static object ParseValue(string text) =>
        text is "true" or "false" ? text == "true"
        : long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l) ? l
        : double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d
        : text;

    private static JsonNode? ToJson(object? v) => v switch
    {
        null => null,
        string s => s,
        bool b => b,
        long l => l,
        double d => d,
        NodeValue n => new JsonObject { ["id"] = n.Id, ["labels"] = new JsonArray(n.Labels.Select(x => (JsonNode?)x).ToArray()), ["properties"] = Map(n.Properties) },
        RelationshipValue r => new JsonObject { ["id"] = r.Id, ["type"] = r.Type, ["start"] = r.StartId, ["end"] = r.EndId, ["properties"] = Map(r.Properties) },
        IReadOnlyList<object?> list => new JsonArray(list.Select(ToJson).ToArray()),
        IReadOnlyDictionary<string, object?> m => Map(m),
        _ => v.ToString(),
    };

    private static JsonObject Map(IReadOnlyDictionary<string, object?> m) => new(m.Select(kv => KeyValuePair.Create(kv.Key, ToJson(kv.Value))));
}
