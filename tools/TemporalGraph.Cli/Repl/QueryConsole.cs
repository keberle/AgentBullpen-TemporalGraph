using System.Collections;
using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Spectre.Console;
using Spectre.Console.Cli;
using Spectre.Console.Json;
using TemporalGraph.Chat;
using TemporalGraph.Cli.Runs;
using TemporalGraph.Query;

namespace TemporalGraph.Cli.Repl;

/// <summary>One committed query file: its name, its header comment (the first line is the question) and the parameters it uses.</summary>
public sealed record QueryFile(string Name, string File, string Group, string Question, string Comment, IReadOnlyList<string> Parameters, string Text);

public static partial class QueryFiles
{
    [GeneratedRegex(@"\$([A-Za-z_][A-Za-z0-9_]*)")]
    private static partial Regex Parameter();

    [GeneratedRegex(@"'(?:[^'\\]|\\.)*'|""(?:[^""\\]|\\.)*""")]
    private static partial Regex StringLiteral();

    /// <summary>The <c>$name</c> parameters a query uses, in order of first use (comments and string literals left out); a query that
    /// calls <c>visible()</c> also needs <c>$pos</c> and <c>$spoilers</c>.</summary>
    public static IReadOnlyList<string> ParametersOf(string query)
    {
        var body = StringLiteral().Replace(ChatTools.StripHeader(query), "''");
        body = string.Join("\n", body.Split('\n').Select(l => l.IndexOf("//", StringComparison.Ordinal) is var i and >= 0 ? l[..i] : l));
        var names = Parameter().Matches(body).Select(m => m.Groups[1].Value).ToList();
        if (body.Contains("visible(", StringComparison.Ordinal)) names.AddRange(["pos", "spoilers"]);
        return names.Distinct().ToList();
    }

    /// <summary><c>queries/&lt;book&gt;/samples/*.cypher</c> (group "sample"), then <c>queries/&lt;book&gt;/*.cypher</c> (group "tool"), by name.</summary>
    public static IReadOnlyList<QueryFile> For(string root, string bookId)
    {
        var dir = Path.Combine(root, "queries", bookId);
        var files = new List<QueryFile>();
        foreach (var (folder, group) in new[] { (Path.Combine(dir, "samples"), "sample"), (dir, "tool") })
        {
            if (!Directory.Exists(folder)) continue;
            foreach (var path in Directory.GetFiles(folder, "*.cypher").Order(StringComparer.Ordinal))
            {
                var text = File.ReadAllText(path);
                var comment = text.Replace("\r\n", "\n").Split('\n').TakeWhile(l => l.TrimStart().StartsWith("//")).Select(l => l.TrimStart()[2..].Trim()).ToList();
                files.Add(new QueryFile(Path.GetFileNameWithoutExtension(path), Path.GetRelativePath(root, path).Replace('\\', '/'), group,
                    comment.FirstOrDefault() ?? "", string.Join("\n", comment), ParametersOf(text), ChatTools.StripHeader(text)));
            }
        }
        return files;
    }
}

/// <summary>The state of a query console: the open graph and what <c>$pos</c>, <c>$spoilers</c> and <c>$character</c> are bound to.</summary>
public sealed class QuerySession(BookGraph graph, string bookId, string root, int chapter, bool spoilers, string? character)
{
    /// <summary>The API's row cap for a query result.</summary>
    public const int MaxRows = 500;

    public BookGraph Graph { get; } = graph;
    public string BookId { get; } = bookId;
    public string Root { get; } = root;
    public int Chapter { get; set; } = chapter;
    public bool Spoilers { get; set; } = spoilers;
    public string? Character { get; set; } = character;
    public bool Json { get; set; }
    public string? LastQuery { get; set; }

    public Dictionary<string, object?> Parameters()
    {
        var p = new Dictionary<string, object?> { ["pos"] = (long)Graph.Chapters[Chapter].CharEnd, ["chapter"] = (long)Chapter, ["spoilers"] = Spoilers };
        if (Character is not null) p["character"] = Character;
        return p;
    }

    public string Prompt => $"{BookId} › ch {Chapter} › spoilers {(Spoilers ? "on" : "off")}{(Character is null ? "" : $" › {Character}")} › ";
}

/// <summary>
/// The query REPL. A query may run over several lines and ends with ";" or a blank line; a line starting with "." is a
/// command. It only collects text and calls <see cref="CypherEngine.Execute"/>, as the one-shot <c>query</c> does.
/// </summary>
public sealed class QueryConsole(QuerySession session, IAnsiConsole console, ILineReader lines, Func<IReadOnlyList<QueryFile>, QueryFile?>? pickSample = null)
{
    public const string Help = """
        A query ends with ";" or a blank line. Commands:
          .chapter N         read up to the end of chapter N ($pos, $chapter)
          .spoilers on|off   $spoilers
          .character <name>  $character (.character alone clears it)
          .samples           pick one of the book's query files into the buffer (Enter runs it)
          .params            show $pos, $chapter, $spoilers, $character
          .schema            node labels and edge types of the open graph, with counts
          .timeline          the book's timeline rows (anchor, chapters, events)
          .json on|off       results as JSON instead of a table
          .save <name>       the last query to queries/<book>/<name>.cypher
          .help              this text
          .quit              leave
        """;

    private readonly List<string> _buffer = [];

    /// <summary>Ctrl+C at the input line says "Cancelled." and leaves, as <c>.quit</c> does (exit 130).</summary>
    public async Task<int> RunAsync(CancellationToken ct)
    {
        console.MarkupLine($"[bold]{Markup.Escape(session.BookId)}[/]: {session.Graph.Nodes.Count:N0} nodes, {session.Graph.Relationships.Count:N0} edges. [grey].help lists the commands; .quit (or Ctrl+C) leaves.[/]");
        try
        {
            while (true)
            {
                var line = lines.Read(_buffer.Count == 0 ? session.Prompt : "… ", ct);
                if (line is null) return Exit.Ok;
                if (!await HandleAsync(line, ct)) return Exit.Ok;
            }
        }
        catch (OperationCanceledException)
        {
            return CliContext.Cancelled();
        }
    }

    /// <summary>One input line. False: leave.</summary>
    public async Task<bool> HandleAsync(string line, CancellationToken ct)
    {
        var trimmed = line.Trim();
        if (_buffer.Count == 0 && trimmed.StartsWith('.')) return await CommandAsync(trimmed, ct);
        if (trimmed.Length == 0)
        {
            if (_buffer.Count > 0) Execute();
            return true;
        }
        _buffer.Add(line);
        if (trimmed.EndsWith(';')) Execute();
        return true;
    }

    private void Execute()
    {
        var query = string.Join("\n", _buffer).Trim().TrimEnd(';').Trim();
        _buffer.Clear();
        if (query.Length == 0) return;
        session.LastQuery = query;
        lines.Remember(query.Replace("\n", " "));
        QueryResult result;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try { result = CypherEngine.Execute(session.Graph, query, session.Parameters()); }
        catch (CypherException ex)
        {
            console.MarkupLine($"[red]{Markup.Escape(ex.Reason)}[/] at line {ex.Line}, column {ex.Column + 1}");
            return;
        }
        var shown = result.Rows.Take(QuerySession.MaxRows).ToList();
        if (session.Json)
            console.Write(new JsonText(QueryCommand.ResultJson(new QueryResult(result.Columns, shown), session.Parameters(), watch.Elapsed.TotalMilliseconds, null).ToJsonString()));
        else
        {
            var table = new Table().Border(TableBorder.Rounded);
            foreach (var c in result.Columns) table.AddColumn(Markup.Escape(c));
            foreach (var row in shown) table.AddRow(row.Select(v => Markup.Escape(CypherValues.Display(v))).ToArray());
            console.Write(table);
        }
        console.WriteLine();
        console.MarkupLine($"{result.Rows.Count} row(s) · {watch.Elapsed.TotalMilliseconds:0.0} ms"
            + (result.Rows.Count > QuerySession.MaxRows ? $" · [yellow]truncated: the first {QuerySession.MaxRows} are shown[/]" : ""));
        console.Write(new Rule());
    }

    private async Task<bool> CommandAsync(string line, CancellationToken ct)
    {
        var space = line.IndexOf(' ');
        var (name, arg) = space < 0 ? (line, "") : (line[..space], line[(space + 1)..].Trim());
        switch (name)
        {
            case ".quit" or ".exit" or ".q":
                return false;
            case ".help":
                console.WriteLine(Help);
                break;
            case ".chapter":
                if (int.TryParse(arg, out var n) && session.Graph.Chapters.ContainsKey(n)) session.Chapter = n;
                else console.MarkupLine($"[red].chapter needs a number from {session.Graph.Chapters.Keys.Min()} to {session.Graph.Chapters.Keys.Max()}.[/]");
                break;
            case ".spoilers":
                if (arg is "on" or "off") session.Spoilers = arg == "on";
                else console.MarkupLine("[red].spoilers on | off[/]");
                break;
            case ".json":
                if (arg is "on" or "off") session.Json = arg == "on";
                else console.MarkupLine("[red].json on | off[/]");
                break;
            case ".character":
                session.Character = arg.Length == 0 ? null : arg;
                break;
            case ".params":
                foreach (var (k, v) in session.Parameters()) console.MarkupLine($"  ${Markup.Escape(k)} = {Markup.Escape(CypherValues.Display(v))}");
                if (session.Character is null) console.MarkupLine("  $character is not set (.character <name>)");
                break;
            case ".schema":
                Schema();
                break;
            case ".timeline":
                Timeline();
                break;
            case ".samples":
                await SamplesAsync(ct);
                break;
            case ".save":
                Save(arg);
                break;
            default:
                console.MarkupLine($"[red]Unknown command {Markup.Escape(name)}.[/] .help lists them.");
                break;
        }
        return true;
    }

    private void Schema()
    {
        var labels = new Table().Border(TableBorder.Rounded).AddColumn("Node label").AddColumn(new TableColumn("Nodes").RightAligned());
        foreach (var g in session.Graph.Nodes.SelectMany(n => n.Labels).GroupBy(l => l).OrderBy(g => g.Key)) labels.AddRow(Markup.Escape(g.Key), g.Count().ToString("N0"));
        console.Write(labels);
        var types = new Table().Border(TableBorder.Rounded).AddColumn("Edge type").AddColumn(new TableColumn("Edges").RightAligned());
        foreach (var g in session.Graph.Relationships.GroupBy(r => r.Type).OrderBy(g => g.Key)) types.AddRow(Markup.Escape(g.Key), g.Count().ToString("N0"));
        console.Write(types);
    }

    private void Timeline()
    {
        var rows = session.Graph.Timeline;
        if (rows.Count == 0)
        {
            console.MarkupLine("This book has no timeline (it was indexed before the full schema).");
            return;
        }
        var table = new Table().Border(TableBorder.Rounded).AddColumns("Kind", "Label").AddColumn(new TableColumn("Revealed at").RightAligned()).AddColumns("Story from", "Story until");
        const int cap = 60;
        foreach (var r in rows.Take(cap))
            table.AddRow(Markup.Escape(r.Kind), Markup.Escape(r.Label is { Length: > 70 } l ? l[..70] + "…" : r.Label ?? ""), r.RevealedPos?.ToString("N0") ?? "-", Markup.Escape(r.StoryFrom ?? "-"), Markup.Escape(r.StoryUntil ?? "-"));
        console.Write(table);
        console.MarkupLine($"{rows.Count} row(s){(rows.Count > cap ? $", the first {cap} shown" : "")}: {string.Join(", ", rows.GroupBy(r => r.Kind).Select(g => $"{g.Count()} {Markup.Escape(g.Key)}"))}."
            + (rows.FirstOrDefault(r => r.Kind == "anchor")?.Note is { } note ? "\n" + Markup.Escape(note) : ""));
    }

    // The list is shown by the console at a terminal only (the interactive path); the tests pass their own pick.
    private async Task SamplesAsync(CancellationToken ct)
    {
        var files = QueryFiles.For(session.Root, session.BookId);
        if (files.Count == 0)
        {
            console.MarkupLine($"queries/{Markup.Escape(session.BookId)}/ has no .cypher files.");
            return;
        }
        QueryFile? picked;
        if (pickSample is not null) picked = pickSample(files);
        else
        {
            // "← Back" last (and Esc): the buffer stays as it is.
            var back = new QueryFile(Prompts.Back, "", "", "", "", [], "");
            var choice = await console.AskAsync(new SelectionPrompt<QueryFile>().Title($"Which query? {Prompts.EscBack}").PageSize(12).MoreChoicesText("[grey](more below)[/]")
                .UseConverter(f => Markup.Escape(ReferenceEquals(f, back) ? f.Name : $"{f.Name} — {f.Question}")).AddChoices(files).AddChoices(back).AddCancelResult(back), ct);
            picked = ReferenceEquals(choice, back) ? null : choice;
        }
        if (picked is null) return;
        _buffer.Clear();
        _buffer.AddRange(picked.Text.Replace("\r\n", "\n").Split('\n'));
        console.WriteLine(picked.Text);
        console.MarkupLine("[grey]In the buffer: Enter runs it; type more lines to add to it.[/]");
    }

    private void Save(string name)
    {
        if (session.LastQuery is null)
        {
            console.MarkupLine("[red]No query has been run yet.[/]");
            return;
        }
        name = name.EndsWith(".cypher", StringComparison.OrdinalIgnoreCase) ? name[..^7] : name;
        if (name.Length == 0 || name.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
        {
            console.MarkupLine("[red].save <name>: letters, digits, '-' and '_' (it becomes queries/<book>/<name>.cypher).[/]");
            return;
        }
        var path = Path.Combine(session.Root, "queries", session.BookId, name + ".cypher");
        if (File.Exists(path))
        {
            console.MarkupLine($"[red]{Markup.Escape(Path.GetRelativePath(session.Root, path))} exists; choose another name.[/]");
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $"// Saved from the query console.\n// Parameters: {string.Join(", ", QueryFiles.ParametersOf(session.LastQuery).Select(p => "$" + p))}.\n{session.LastQuery}\n");
        console.MarkupLine($"Saved {Markup.Escape(Path.GetRelativePath(session.Root, path).Replace('\\', '/'))}");
    }
}

/// <summary><c>query explain</c>: a query's parsed tree as JSON, without running it.</summary>
public static class QueryExplain
{
    /// <summary>Records become { "type": name, property… }, lists arrays, enums and primitives their values.</summary>
    public static JsonNode? Tree(object? value) => value switch
    {
        null => null,
        string s => s,
        bool b => b,
        int i => i,
        long l => l,
        double d => d,
        Enum e => e.ToString(),
        IEnumerable list => new JsonArray(list.Cast<object?>().Select(Tree).ToArray()),
        _ => Record(value),
    };

    private static JsonObject Record(object value)
    {
        var node = new JsonObject { ["type"] = value.GetType().Name };
        foreach (var p in value.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.GetIndexParameters().Length == 0 && p.Name != "EqualityContract"))
            node[char.ToLowerInvariant(p.Name[0]) + p.Name[1..]] = Tree(p.GetValue(value));
        return node;
    }

    /// <summary>The clause kinds in order ("MatchClause", "WithClause", …), found in the tree.</summary>
    public static IReadOnlyList<string> Clauses(JsonNode? tree)
    {
        var found = new List<string>();
        void Walk(JsonNode? n)
        {
            if (n is JsonObject o)
            {
                if (o["type"]?.GetValue<string>() is { } t && t.EndsWith("Clause", StringComparison.Ordinal)) found.Add(t);
                foreach (var kv in o) Walk(kv.Value);
            }
            else if (n is JsonArray a) foreach (var x in a) Walk(x);
        }
        Walk(tree);
        return found;
    }
}

public sealed class QuerySamplesCommand : Command<QuerySamplesCommand.Settings>
{
    public sealed class Settings : GlobalSettings
    {
        [CommandOption("--book <ID>")][Description("The book whose queries/<id>/ files to list.")] public string? Book { get; init; }
    }

    public override int Execute(CommandContext context, Settings s, CancellationToken cancellationToken)
    {
        var root = RepoPaths.FindRoot();
        if (string.IsNullOrWhiteSpace(s.Book)) return CliContext.Fail(Exit.BadInput, "Give --book <id>.");
        var files = QueryFiles.For(root, s.Book);
        if (files.Count == 0) return CliContext.Fail(Exit.NotReady, $"queries/{s.Book}/ has no .cypher files.", "no-queries");
        CliContext.Result = new { book = s.Book, queries = files.Select(f => new { f.Name, f.File, f.Group, f.Question, f.Comment, f.Parameters }) };
        var table = new Table().Border(TableBorder.Rounded).AddColumns("Name", "Group", "Question", "Parameters");
        foreach (var f in files) table.AddRow(Markup.Escape(f.Name), f.Group, Markup.Escape(f.Question), Markup.Escape(string.Join(" ", f.Parameters.Select(p => "$" + p))));
        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"Run one: [bold]query --book {Markup.Escape(s.Book)} --file {Markup.Escape(files[0].File)} --chapter 3[/]");
        return Exit.Ok;
    }
}

public sealed class QueryExplainCommand : Command<QueryExplainCommand.Settings>
{
    public sealed class Settings : GlobalSettings
    {
        [CommandOption("--book <ID>")][Description("Optional: a --file path may then be relative to queries/<id>/.")] public string? Book { get; init; }
        [CommandOption("--text <CYPHER>")][Description("The query text.")] public string? Text { get; init; }
        [CommandOption("--file <PATH>")][Description("A query file.")] public string? File { get; init; }
    }

    public override int Execute(CommandContext context, Settings s, CancellationToken cancellationToken)
    {
        var root = RepoPaths.FindRoot();
        if ((s.Text is null) == (s.File is null)) return CliContext.Fail(Exit.BadInput, "Give --text \"<cypher>\" or --file <path>.");
        string query;
        if (s.File is { } file)
        {
            var path = new[] { file, Path.Combine(root, file), Path.Combine(root, "queries", s.Book ?? "", file) }.FirstOrDefault(System.IO.File.Exists);
            if (path is null) return CliContext.Fail(Exit.BadInput, $"Query file not found: {file}");
            query = System.IO.File.ReadAllText(path);
        }
        else query = s.Text!;

        QueryAst ast;
        try { ast = AstBuilder.Parse(query); }
        catch (CypherException ex) { return CliContext.Fail(Exit.BadInput, $"{ex.Reason} at line {ex.Line}, column {ex.Column + 1}", "query-error"); }
        var tree = QueryExplain.Tree(ast);
        var parameters = QueryFiles.ParametersOf(query);
        var clauses = QueryExplain.Clauses(tree);
        CliContext.Result = new JsonObject
        {
            ["parameters"] = new JsonArray(parameters.Select(p => (JsonNode?)p).ToArray()),
            ["clauses"] = new JsonArray(clauses.Select(c => (JsonNode?)c).ToArray()),
            ["ast"] = tree,
        };
        AnsiConsole.MarkupLine($"Parses. Parameters it needs: {(parameters.Count == 0 ? "none" : Markup.Escape(string.Join(", ", parameters.Select(p => "$" + p))))}.");
        AnsiConsole.MarkupLine($"Clauses: {Markup.Escape(string.Join(" → ", clauses.Select(c => c.Replace("Clause", "").ToUpperInvariant())))}");
        if (!CliContext.Json) AnsiConsole.Write(new JsonText(tree?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "null"));
        return Exit.Ok;
    }
}
