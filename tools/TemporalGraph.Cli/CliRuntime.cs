using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Spectre.Console;
using Spectre.Console.Cli;
using TemporalGraph.Cli.Runs;
using TemporalGraph.Cli.Setup;

namespace TemporalGraph.Cli;

/// <summary>The options every command takes, declared once. Each command's settings derive from this.</summary>
public class GlobalSettings : CommandSettings
{
    [CommandOption("--json")]
    [Description("Print one JSON object on stdout: { ok, command, exit, result, problems }. Everything else goes to stderr.")]
    public bool Json { get; init; }

    [CommandOption("--quiet")]
    [Description("Print errors only.")]
    public bool Quiet { get; init; }

    [CommandOption("--no-color")]
    [Description("No colour in the output.")]
    public bool NoColor { get; init; }

    [CommandOption("--no-input")]
    [Description("Never ask a question, even at a terminal: a missing value is exit 2, with the option to pass.")]
    public bool NoInput { get; init; }

    [CommandOption("--verbose")]
    [Description("Echo the run log's lines (stage starts and ends, chunks, retries, warnings) to stderr as they happen.")]
    public bool Verbose { get; init; }

    [CommandOption("--debug")]
    [Description("As --verbose, plus the size and timing of each model call (never its content), and the stack trace of an unexpected error.")]
    public bool Debug { get; init; }

    [CommandOption("--repo <PATH>")]
    [Description("The repo root (data/books, queries, runs). Default: the nearest folder at or above the current one that holds TemporalGraph.slnx.")]
    public string? Repo { get; init; }
}

/// <summary>
/// The exit codes of every command. 0 ok · 1 a call, a download or a store failed · 2 bad input, or a prompt was needed and
/// there is no terminal · 3 not ready (no model configured, book not indexed, a stage's input missing) · 4 a check failed (a leak test,
/// a Stage 0 check, a vet: the work ran and its result is bad) · 130 cancelled (Ctrl+C, a closed terminal, or `index cancel`; the
/// POSIX convention for SIGINT, so "stopped" can be told from "failed").
/// </summary>
public static class Exit
{
    public const int Ok = 0, Failed = 1, BadInput = 2, NotReady = 3, CheckFailed = 4, Cancelled = 130;

    /// <summary>The problem type (a short slug, not a URL) for an exit code.</summary>
    public static string Slug(int code) => code switch
    {
        Ok => "ok",
        Failed => "failed",
        BadInput => "bad-input",
        NotReady => "not-ready",
        CheckFailed => "check-failed",
        Cancelled => "cancelled",
        _ => "error",
    };

    public static string Title(int code) => code switch
    {
        Failed => "A call, a download or a store failed",
        BadInput => "Bad input, or a prompt was needed and there is no terminal",
        NotReady => "Not ready",
        CheckFailed => "A check failed",
        Cancelled => "Cancelled",
        _ => "Error",
    };
}

/// <summary>One entry of the envelope's <c>problems</c>: the RFC 9457 field names the API uses; <c>type</c> is a slug here.</summary>
public sealed record CliProblem(string Type, string Title, string Detail);

/// <summary>
/// The state of one CLI invocation: whether <c>--json</c> or <c>--quiet</c> was given, the problems the command reported,
/// the result a command hands to the envelope, and whether a person is at a terminal. One command runs per process; the tests that
/// drive commands through their own writers never touch it.
/// </summary>
public static class CliContext
{
    public static bool Json { get; private set; }
    public static bool Quiet { get; private set; }
    public static List<CliProblem> Problems { get; } = [];

    /// <summary>The envelope's <c>result</c>, when the command sets it itself (else whatever JSON it printed on stdout).</summary>
    public static object? Result { get; set; }

    /// <summary>Tests: answer "is a person at a terminal" without a console. Null: ask the console.</summary>
    public static bool? InteractiveOverride { get; set; }

    /// <summary><c>--no-input</c>: never prompt, even at a terminal.</summary>
    public static bool NoInput { get; private set; }

    /// <summary><c>--verbose</c> (the run log's lines on stderr) and <c>--debug</c> (plus each model call's size and timing).</summary>
    public static bool Verbose { get; private set; }
    public static bool Debug { get; private set; }

    /// <summary>Tests: where the one-line notices (next steps, "Stopping…") go; null: <see cref="Console.Error"/>.</summary>
    public static TextWriter? StderrOverride { get => StderrOfTest.Value; set => StderrOfTest.Value = value; }
    public static TextWriter Stderr => StderrOverride ?? Console.Error;

    // Per test (it flows with the test's own calls), so tests that run side by side do not write into each other's.
    private static readonly AsyncLocal<TextWriter?> StderrOfTest = new();

    /// <summary>A person at a terminal and no <c>--json</c> / <c>--quiet</c> / <c>--no-input</c>: the only case in which a command may prompt (setup's test).</summary>
    public static bool Interactive => !Json && !Quiet && !NoInput && (InteractiveOverride ?? SetupRunner.IsInteractive());

    public static void Reset(bool json = false, bool quiet = false, bool noInput = false, bool verbose = false, bool debug = false)
    {
        Json = json;
        Quiet = quiet;
        NoInput = noInput;
        Verbose = verbose || debug;
        Debug = debug;
        Problems.Clear();
        Result = null;
    }

    /// <summary>Records a problem for the envelope (and for <c>--quiet</c>) and returns <paramref name="exit"/>.</summary>
    public static int Problem(int exit, string detail, string? type = null, string? title = null)
    {
        Problems.Add(new CliProblem(type ?? Exit.Slug(exit), title ?? Exit.Title(exit), detail));
        return exit;
    }

    /// <summary>Prints the message in red, records the problem, returns the exit code: the one way a command refuses.</summary>
    public static int Fail(int exit, string detail, string? type = null)
    {
        AnsiConsole.MarkupLine($"[red]{Markup.Escape(detail)}[/]");
        return Problem(exit, detail, type);
    }

    /// <summary>A command that was cancelled: one line, <c>Cancelled.</c>, the problem, exit 130.</summary>
    public static int Cancelled(string detail = "Cancelled.")
    {
        AnsiConsole.MarkupLine(Markup.Escape(detail));
        return Problem(Exit.Cancelled, detail, "cancelled");
    }

    /// <summary>The next command after a state change: one line on stderr, not with <c>--quiet</c> or <c>--json</c>.</summary>
    public static void Next(string text, TextWriter? stderr = null)
    {
        if (!Json && !Quiet) (stderr ?? Stderr).WriteLine("Next: " + text);
    }

    /// <summary>"A prompt was needed and there is no terminal": exit 2, with the command that does the same without prompts.</summary>
    public static int NeedsTerminal(string what, string commandLine) => NoInput
        ? Fail(Exit.BadInput, $"{what}, and --no-input was given. Pass it:\n  {SetupRunner.Cli} {commandLine}", "no-input")
        : Fail(Exit.BadInput, $"{what}, and there is no terminal to ask at. Run:\n  {SetupRunner.Cli} {commandLine}", "no-terminal");
}

/// <summary>Applies the global options before a command runs and maps Spectre's −1 (a parser error) to 2 after it.</summary>
public sealed class GlobalInterceptor : ICommandInterceptor
{
    public void Intercept(CommandContext context, CommandSettings settings)
    {
        if (settings is not GlobalSettings g) return;
        if (g.NoColor) AnsiConsole.Profile.Capabilities.ColorSystem = ColorSystem.NoColors;
        if (g.Repo is { Length: > 0 } repo)
        {
            var full = Path.GetFullPath(repo);
            if (!Directory.Exists(full)) throw new DirectoryNotFoundException($"--repo: no such folder: {repo}");
            RepoPaths.Override = full;
        }
        LogCommand(context, settings, null);
    }

    public void InterceptResult(CommandContext context, CommandSettings settings, ref int result)
    {
        if (result == -1) result = Exit.BadInput;
        LogCommand(context, settings, result);
    }

    /// <summary>
    /// Every command that works on a run folder (<c>--run &lt;id&gt;</c>: the stage commands, <c>index build-db</c>,
    /// <c>index promote</c>, <c>index score</c>, …) leaves its command line and its exit code in that run's run.log. Commands that
    /// only read a run (<c>index status</c>, <c>index watch</c>) and <c>index cancel</c> leave it as it is.
    /// </summary>
    private static void LogCommand(CommandContext context, CommandSettings settings, int? exit)
    {
        if (settings is IndexStatusCommand.Settings or IndexWatchCommand.Settings or IndexCancelCommand.Settings) return;
        try
        {
            var runs = settings.GetType().GetProperty("Run")?.GetValue(settings) switch { string one => [one], string[] many => many, _ => Array.Empty<string>() };
            foreach (var id in runs.Where(r => !string.IsNullOrWhiteSpace(r)))
            {
                var dir = Directory.Exists(id) ? id : Path.Combine(RepoPaths.Runs(RepoPaths.FindRoot()), id);
                if (!Directory.Exists(dir) || !File.Exists(Path.Combine(dir, RunFolder.RunJson))) continue;
                var line = string.Join(" ", context.Arguments.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
                new RunLog(Path.Combine(dir, RunLog.FileName)).Write(exit is null ? $"command: {line}" : $"command ended: exit {exit} · {line}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Reflection.AmbiguousMatchException or ArgumentException) { }
    }
}

/// <summary>
/// With <c>--json</c>, stdout carries exactly one JSON object and everything Spectre writes goes to stderr, so an
/// agent can pipe the output to a JSON tool. The command's own JSON (what it prints without the envelope) becomes <c>result</c>, unchanged.
/// With <c>--quiet</c> nothing is printed but the problems of a failed command.
/// </summary>
public sealed class OutputCapture : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly TextWriter _stdout = Console.Out;
    private readonly IAnsiConsole _console = AnsiConsole.Console;
    private readonly StringWriter _captured = new();
    private readonly StringBuilder _said = new();
    private readonly bool _json;

    public OutputCapture(bool json)
    {
        _json = json;
        Console.SetOut(_captured);
        // Spectre to stderr, plain: no colour, no spinner, no prompt. In quiet mode it is recorded and not shown.
        TextWriter target = json ? new Tee(Console.Error, _said) : new Tee(TextWriter.Null, _said);
        AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(target),
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.No,
        });
        AnsiConsole.Profile.Width = 200;
    }

    /// <summary>What the command printed on stdout.</summary>
    public string Captured => _captured.ToString();

    /// <summary>What the command said through Spectre, as plain text.</summary>
    public string Said => _said.ToString();

    public void Dispose()
    {
        Console.SetOut(_stdout);
        AnsiConsole.Console = _console;
    }

    /// <summary>The envelope. <paramref name="captured"/> is the command's stdout: JSON becomes <c>result</c> as it is; plain
    /// text becomes <c>{ "text": … }</c>; nothing, null. A failed command with no recorded problem gets one from what it said.</summary>
    public static string Envelope(string command, int exit, string captured, string said, object? result, IReadOnlyList<CliProblem> problems)
    {
        JsonNode? node = null;
        if (result is not null) node = result as JsonNode ?? JsonSerializer.SerializeToNode(result, result.GetType(), Json);
        else if (captured.Trim() is { Length: > 0 } text)
        {
            try { node = JsonNode.Parse(text); }
            catch (JsonException) { node = new JsonObject { ["text"] = text }; }
        }
        var list = problems.ToList();
        if (exit != Exit.Ok && list.Count == 0)
            list.Add(new CliProblem(Exit.Slug(exit), Exit.Title(exit), Tail(said) is { Length: > 0 } t ? t : $"The command exited with code {exit}."));
        var envelope = new JsonObject
        {
            ["ok"] = exit == Exit.Ok,
            ["command"] = command,
            ["exit"] = exit,
            ["result"] = node?.DeepClone(),
            ["problems"] = JsonSerializer.SerializeToNode(list, Json),
        };
        return envelope.ToJsonString(Json);
    }

    /// <summary>The last lines a command said (at most 1,200 characters), for a problem's detail.</summary>
    private static string Tail(string said)
    {
        var text = string.Join("\n", said.Replace("\r\n", "\n").Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0)).Trim();
        return text.Length <= 1200 ? text : "…" + text[^1200..];
    }

    private sealed class Tee(TextWriter shown, StringBuilder kept) : TextWriter
    {
        public override Encoding Encoding => shown.Encoding;
        public override void Write(char value) { shown.Write(value); kept.Append(value); }
        public override void Write(string? value) { shown.Write(value); kept.Append(value); }
        public override void Flush() => shown.Flush();
    }
}
