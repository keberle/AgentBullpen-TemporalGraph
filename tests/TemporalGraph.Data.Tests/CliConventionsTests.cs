using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Spectre.Console;
using Spectre.Console.Cli;
using Spectre.Console.Rendering;
using Spectre.Console.Testing;
using TemporalGraph.Cli;
using TemporalGraph.Cli.Books;
using TemporalGraph.Cli.Indexing;
using TemporalGraph.Cli.Repl;
using TemporalGraph.Cli.Runs;
using TemporalGraph.Llm;
using TemporalGraph.Testing;

namespace TemporalGraph.Data.Tests;

/// <summary>
/// The CLI's conventions. Ctrl+C everywhere (one token, exit 130, "Cancelled."), prompts that can be cancelled and left,
/// book pickers that can add a book, a run that can be watched and stopped (the fake stage runner: no model), the run log, the next
/// step after a state change, help with examples, <c>--version</c>, <c>NO_COLOR</c>, <c>--no-input</c>, dry runs and timeouts.
/// </summary>
public partial class CliDoorTests
{
    // ── helpers ────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The time limit of every async test here: a prompt or a run that is not left is a failed test with its name, not a test run that never ends.</summary>
    private const int TestTimeout = 120_000;
    private static readonly TimeSpan Soon = TimeSpan.FromSeconds(30);

    /// <summary>Runs the CLI as at a terminal, on <paramref name="console"/>; stderr's one-line notices are returned.</summary>
    private static async Task<(int Exit, string Stderr)> RunAtTerminal(IAnsiConsole console, string[] args, CancellationToken ct = default, bool terminal = true)
    {
        var (shown, was) = (AnsiConsole.Console, CliContext.InteractiveOverride);
        var stderr = new StringWriter();
        AnsiConsole.Console = console;
        CliContext.InteractiveOverride = terminal;
        CliContext.StderrOverride = stderr;
        try { return (await CliApp.RunAsync(args, ct), stderr.ToString()); }
        finally
        {
            AnsiConsole.Console = shown;
            CliContext.InteractiveOverride = was;
            CliContext.StderrOverride = null;
        }
    }

    private static TestConsole Wide()
    {
        var console = new TestConsole();
        console.Profile.Width = 240;
        return console;
    }

    /// <summary>A terminal at which nobody types: a prompt waits for a key until its token is cancelled, as Spectre's own input does.</summary>
    private sealed class SilentTerminal : IAnsiConsole
    {
        private readonly TestConsole _inner = new();

        public SilentTerminal()
        {
            _inner.Profile.Width = 200;
            _inner.Interactive();
        }

        public string Output => _inner.Output;
        public Profile Profile => _inner.Profile;
        public IAnsiConsoleCursor Cursor => _inner.Cursor;
        public IAnsiConsoleInput Input { get; } = new NoKeys();
        public IExclusivityMode ExclusivityMode => _inner.ExclusivityMode;
        public RenderPipeline Pipeline => _inner.Pipeline;
        public void Clear(bool home) => _inner.Clear(home);
        public void Write(IRenderable renderable) => _inner.Write(renderable);
        public void WriteAnsi(Action<AnsiWriter> action) => _inner.WriteAnsi(action);

        private sealed class NoKeys : IAnsiConsoleInput
        {
            public bool IsKeyAvailable() => false;
            public ConsoleKeyInfo? ReadKey(bool intercept) => throw new InvalidOperationException("A synchronous read: the prompt would never see Ctrl+C.");

            public async Task<ConsoleKeyInfo?> ReadKeyAsync(bool intercept, CancellationToken cancellationToken)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return null;
            }
        }
    }

    /// <summary>The fake stage runner's Stage 1 and 2 leave what a continued run copies (stage-1.json with its skill, stage-2.json).</summary>
    private static void LeaveStageFiles(int stage, RunFolder run)
    {
        if (stage == 1) Stage1File.WriteStage1(run, new Stage1Result("tiny-tale", 1, 3, false, 0.9, [], [], [], 0), new { name = "extract-mentions", version = 1 });
        if (stage == 2) File.WriteAllText(run.File(RunFolder.Stage2Json), "{}\n");
    }

    private static JsonNode RunJsonOf(string root, string runId) => JsonNode.Parse(File.ReadAllText(Path.Combine(RepoPaths.Runs(root), runId, RunFolder.RunJson)))!;

    private static string NewestIndexRun(string root) =>
        Directory.GetDirectories(RepoPaths.Runs(root)).Where(d => File.Exists(Path.Combine(d, RunLog.FileName)) && File.ReadAllText(Path.Combine(d, RunFolder.RunJson)).Contains("index run"))
            .OrderBy(d => File.GetLastWriteTimeUtc(Path.Combine(d, RunFolder.RunJson))).ThenBy(d => d, StringComparer.Ordinal).Select(Path.GetFileName).Last()!;

    // ── No synchronous prompt is left ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Spectre's synchronous prompts never see a cancellation token, so Ctrl+C could not leave them. Every prompt goes through
    /// <see cref="Prompts.AskAsync{T}(IAnsiConsole, IPrompt{T}, CancellationToken)"/>; this test keeps it so.
    /// </summary>
    [Fact]
    public void No_synchronous_prompt_is_left_in_the_cli_source()
    {
        var source = Path.Combine(RepoPaths.FindRoot(), "tools", "TemporalGraph.Cli");
        var files = Directory.GetFiles(source, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Replace('\\', '/').Contains("/obj/") && !f.Replace('\\', '/').Contains("/bin/")).ToList();
        Assert.True(files.Count > 50);
        // console.Prompt(…), AnsiConsole.Ask<T>(…), console.Confirm(…), prompt.Show(console), Console.ReadLine().
        var sync = new Regex(@"\.(Prompt|Ask|Confirm)\s*(<[^>(]*>)?\s*\(|\.Show\s*\(\s*[^)\s]|Console\.ReadLine\s*\(");
        var hits = new List<string>();
        foreach (var file in files)
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
                if (sync.IsMatch(lines[i]) && !lines[i].TrimStart().StartsWith("//")) hits.Add($"{Path.GetRelativePath(source, file)}:{i + 1}: {lines[i].Trim()}");
        }
        Assert.Empty(hits);
        // The one place that reads keys itself asks whether a key is there first (a blocking read never sees Ctrl+C).
        var readers = files.Where(f => File.ReadAllText(f).Contains("Console.ReadKey(")).Select(Path.GetFileName).ToList();
        Assert.Equal(["LineEditor.cs"], readers);
        Assert.Contains("Console.KeyAvailable", File.ReadAllText(files.Single(f => Path.GetFileName(f) == "LineEditor.cs")));
        // Every list has a cancel result (Esc = Back) and says so at the end of its title.
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            var lists = Regex.Matches(text, @"new (Multi)?SelectionPrompt<").Count;
            if (lists == 0) continue;
            Assert.True(Regex.Matches(text, @"\.AddCancelResult\(").Count >= lists, $"{Path.GetFileName(file)}: a list without AddCancelResult");
            Assert.True(Regex.Matches(text, @"Prompts\.Esc(Back|Exit)").Count >= lists, $"{Path.GetFileName(file)}: a list whose title does not end with (Esc: back)");
        }
        Assert.Equal(("[grey](Esc: back)[/]", "[grey](Esc: exit)[/]"), (Prompts.EscBack, Prompts.EscExit));
        // A prompt in a non-terminal is a crash in Spectre, so the helper is only called where a terminal was checked: every file
        // that asks something also tests "interactive" (CliContext.Interactive or the runner's own flag); the helper's file aside.
        foreach (var file in files.Where(f => Path.GetFileName(f) != "Interrupts.cs" && Regex.IsMatch(File.ReadAllText(f), @"[cC]onsole\.(AskAsync|ConfirmAsync)\(")))
            Assert.Matches("(?i)interactive", File.ReadAllText(file));
        // And the helper is what is used: the prompts of the menu, the books, the run, the consoles and setup.
        Assert.True(files.Sum(f => Regex.Matches(File.ReadAllText(f), @"[cC]onsole\.(AskAsync|ConfirmAsync)\(").Count) >= 20);
    }

    // ── Ctrl+C ───────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_first_ctrl_c_cancels_and_says_so_and_the_second_force_quits_with_130()
    {
        var stderr = new StringWriter();
        CliContext.StderrOverride = stderr;
        var quit = new List<int>();
        var force = Interrupts.ForceQuit;
        Interrupts.ForceQuit = quit.Add;
        try
        {
            using var cts = new CancellationTokenSource();
            using (Interrupts.Listen(cts))
            {
                Assert.False(Interrupts.Pressed);
                Interrupts.Press();
                Assert.True(cts.IsCancellationRequested);
                Assert.True(Interrupts.Pressed);
                Assert.Equal("Stopping… (Ctrl+C again to quit at once)" + Environment.NewLine, stderr.ToString());
                Assert.Empty(quit);
                Interrupts.Press();
                Assert.Equal([Exit.Cancelled], quit);
                Assert.Equal(130, Exit.Cancelled);
            }

            // At a prompt the press prints nothing itself: the prompt's "Cancelled." is the one line.
            stderr.GetStringBuilder().Clear();
            using var second = new CancellationTokenSource();
            using (Interrupts.Listen(second))
            using (Interrupts.Prompting())
            {
                Interrupts.Press();
                Assert.True(second.IsCancellationRequested);
                Assert.Equal("", stderr.ToString());
            }

            // A call scope (the chat console's model call) takes one press for itself; the next one goes to the process.
            using var third = new CancellationTokenSource();
            using (Interrupts.Listen(third))
            using (var call = Interrupts.Call(third.Token))
            {
                Interrupts.Press();
                Assert.True(call.Interrupted);
                Assert.True(call.Token.IsCancellationRequested);
                Assert.False(third.IsCancellationRequested);
                Interrupts.Press();
                Assert.True(third.IsCancellationRequested);
            }

            // A closed terminal or SIGTERM is the first press, and never the force quit.
            quit.Clear();
            using var fourth = new CancellationTokenSource();
            using (Interrupts.Listen(fourth))
            {
                Interrupts.Terminate();
                Interrupts.Terminate();
                Assert.True(fourth.IsCancellationRequested);
                Assert.Empty(quit);
            }
        }
        finally
        {
            Interrupts.ForceQuit = force;
            CliContext.StderrOverride = null;
        }
    }

    /// <summary>"any prompt or menu": the prompt throws, the command returns 130 with the one line "Cancelled.".</summary>
    [Fact(Timeout = TestTimeout)]
    public async Task Ctrl_c_at_the_home_menu_exits_130_with_one_line_cancelled()
    {
        var terminal = new SilentTerminal();
        using var ctrlC = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));

        var (exit, _) = await RunAtTerminal(terminal, ["--repo", RepoPaths.FindRoot()], ctrlC.Token);

        Assert.Equal(Exit.Cancelled, exit);
        Assert.Contains("What would you like to do?", terminal.Output);
        Assert.Contains("Exit", terminal.Output);                                   // the menu's own way out
        Assert.Single(Regex.Matches(terminal.Output, "Cancelled\\."));
        Assert.Equal("cancelled", Assert.Single(CliContext.Problems).Type);
        CliContext.Reset();
    }

    /// <summary>Every kind of prompt leaves with an OperationCanceledException when its token is cancelled.</summary>
    [Fact(Timeout = TestTimeout)]
    public async Task Every_kind_of_prompt_is_left_by_a_cancelled_token()
    {
        var prompts = new Func<IAnsiConsole, CancellationToken, Task>[]
        {
            (c, ct) => c.AskAsync(new SelectionPrompt<string>().Title("pick").AddChoices("a", "b"), ct),
            (c, ct) => c.AskAsync(new MultiSelectionPrompt<string>().Title("tick").AddChoices("a", "b"), ct),
            (c, ct) => c.AskAsync(new TextPrompt<string>("say"), ct),
            (c, ct) => c.AskAsync(new TextPrompt<string>("secret").Secret(null), ct),
            (c, ct) => Prompts.ConfirmAsync(c, "sure?", false, ct),
        };
        foreach (var prompt in prompts)
        {
            using var ctrlC = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => prompt(new SilentTerminal(), ctrlC.Token));
        }
        // A token cancelled before the prompt is shown never shows it.
        var never = new SilentTerminal();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Prompts.ConfirmAsync(never, "never shown", true, new CancellationToken(true)));
        Assert.DoesNotContain("never shown", never.Output);
    }

    // ── --no-input ───────────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory(Timeout = TestTimeout)]
    [InlineData("index run", "--book <id>")]
    [InlineData("books add", "books add <gutenberg-number | url | file.txt>")]
    [InlineData("books remove tiny-tale", "books remove tiny-tale --yes")]
    [InlineData("query --book hound-of-the-baskervilles", "--text")]
    [InlineData("", "--help")]
    public async Task No_input_never_prompts_even_at_a_terminal_and_is_exit_2_with_the_option_to_pass(string command, string names)
    {
        var console = Wide();
        console.Interactive();   // no key is pushed: a prompt would throw

        var (exit, _) = await RunAtTerminal(console, [.. command.Split(' ', StringSplitOptions.RemoveEmptyEntries), "--no-input"], TestContext.Current.CancellationToken);

        Assert.Equal(Exit.BadInput, exit);
        var problem = Assert.Single(CliContext.Problems);
        Assert.Equal("no-input", problem.Type);
        Assert.Contains("--no-input was given", problem.Detail);
        Assert.Contains(names, problem.Detail);
        Assert.False(CliContext.Interactive);
        CliContext.Reset();
        Assert.False(CliContext.NoInput);
    }

    // ── A run that is stopped ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The fake stage runner honours the token in the middle of Stage 3: run.json says cancelled and the stage reached, the exit code
    /// is 130, the line says where it stopped and how to continue, nothing is promoted; and that command does continue it.
    /// </summary>
    [Fact(Timeout = TestTimeout)]
    public async Task A_run_cancelled_mid_stage_exits_130_records_it_and_prints_the_command_that_continues_it()
    {
        using var root = await RootWithTinyTale();
        using var ctrlC = new CancellationTokenSource();
        var staged = new List<int>();
        var stderr = new StringWriter();
        var console = Wide();
        CliContext.Reset();
        var runner = new IndexRunner(root.Path, console, false, ConfigOf(IndexingP), async (stage, run, ct) =>
        {
            staged.Add(stage);
            LeaveStageFiles(stage, run);
            if (stage != 1) return Exit.Ok;
            // Stage 1: the call of the first chunk and of the second, then Ctrl+C while it works.
            foreach (var chunk in new[] { 0, 1 })
                run.AppendCall(new { type = "call", timestamp = DateTimeOffset.UtcNow, stage = "stage-1", chapter = chunk + 1, chunkIndex = chunk, usage = new { input = 1000, output = 200 }, latencyMs = 3200.0 });
            run.AppendFailure(new { type = "quote mismatch", stage = "stage-1", chapter = 1, chunkIndex = 0 });
            run.AppendFailure(new { type = "quote mismatch", stage = "stage-1", chapter = 2, chunkIndex = 1 });
            await Task.Delay(150, CancellationToken.None);   // the watcher's next look
            ctrlC.Cancel();
            await Task.Delay(Timeout.Infinite, ct);
            return Exit.Ok;
        }, stderr, poll: TimeSpan.FromMilliseconds(20), heartbeat: TimeSpan.FromMilliseconds(40));

        var exit = await runner.RunAsync(new IndexRunOptions("tiny-tale", Promote: true), ctrlC.Token);

        Assert.Equal(Exit.Cancelled, exit);
        Assert.Equal([1], staged);
        var runId = NewestIndexRun(root.Path);
        var runJson = RunJsonOf(root.Path, runId);
        Assert.True(runJson["cancelled"]!.GetValue<bool>());
        Assert.Equal(("cancelled", 1, 1, Exit.Cancelled), (runJson["state"]!.GetValue<string>(), runJson["stoppedAtStage"]!.GetValue<int>(), runJson["currentStage"]!.GetValue<int>(), runJson["exit"]!.GetValue<int>()));
        Assert.NotNull(runJson["finishedAt"]);
        Assert.Equal((0, 5, 3, 0), (runJson["plan"]!["firstStage"]!.GetValue<int>(), runJson["plan"]!["lastStage"]!.GetValue<int>(), runJson["plan"]!["chunks"]!.GetValue<int>(), runJson["plan"]!["firstChunk"]!.GetValue<int>()));
        Assert.Equal(3, runJson["plan"]!["plannedCalls"]!["stage-1"]!.GetValue<long>());   // one call per chunk
        // The line: where it stopped and the command that goes on from there.
        // Stage 1 reads the text itself, so there is nothing of this run to go on from: the command starts the book again.
        var continueWith = "index run --book tiny-tale --stages 0-5 --promote";
        Assert.Contains($"Stopped at stage 1, chunk 2/3. Continue: {CliName.Wrapper} {continueWith}", console.Output.Replace("\n", ""));
        var problem = Assert.Single(CliContext.Problems);
        Assert.Equal(("cancelled", "Cancelled"), (problem.Type, problem.Title));
        Assert.Equal($"Cancelled. Stopped at stage 1, chunk 2/3. Continue: {CliName.Wrapper} {continueWith}", problem.Detail);
        var result = RebuildResult();
        Assert.True(result["cancelled"]!.GetValue<bool>());
        Assert.False(result["promoted"]!.GetValue<bool>());
        Assert.Equal((1, continueWith, $"runs/{runId}/run.log"), (result["stoppedAtStage"]!.GetValue<int>(), result["continueWith"]!.GetValue<string>(), result["log"]!.GetValue<string>()));
        Assert.False(File.Exists(IndexPrepareCommand.ProvenancePath(root.Path, "tiny-tale")));   // nothing promoted
        // The three lines at the start, before the first stage.
        var said = console.Output.Replace("\n", "");
        Assert.Contains($"Run {runId} · tiny-tale · stages 0-5 · 3 chapters, 3 chunks · model model-p", said);
        Assert.Contains($"Estimate: about 379,200 tokens (basis: {IndexPlan.EstimateBasisShort}); cost not known (no price for p)", said);
        Assert.Contains($"Log runs/{runId}/run.log · Watch: {CliName.Wrapper} index watch --run {runId} · Stop: Ctrl+C, or {CliName.Wrapper} index cancel --run {runId}", said);
        // Without a terminal: no bars, a line per stage start and end, and a line now and then that says where it is.
        Assert.Contains("stage 1 extract: started", stderr.ToString());
        Assert.Contains("stage 0 prepare: done", stderr.ToString());
        Assert.Contains("stage 1: 2/3 calls · 2.0k/400 tokens", stderr.ToString());
        Assert.DoesNotContain("Next:", stderr.ToString());
        // The log has the stages, the chunk that was done, the warnings one by one and the stop; the terminal counts the warnings.
        var log = File.ReadAllText(Path.Combine(RepoPaths.Runs(root.Path), runId, RunLog.FileName));
        Assert.Matches(@"(?m)^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}Z run " + runId, log);
        Assert.Contains("stage 1 extract: start", log);
        Assert.Contains("stage 0 prepare: done", log);
        Assert.Contains("stage-1: chunk 1/3 done", log);
        Assert.Equal(2, Regex.Matches(log, "warning stage-1: quote mismatch").Count);
        Assert.Contains("cancelled: Stopped at stage 1, chunk 2/3.", log);
        Assert.Contains("stage-1: 2 × quote mismatch — see run.log", stderr.ToString());
        Assert.DoesNotContain("tokens in, ", log);   // each call's size and timing is --debug's

        // The command it printed runs the book (without --promote here), and the next step follows on stderr.
        staged.Clear();
        stderr.GetStringBuilder().Clear();
        CliContext.Reset();
        var again = new IndexRunner(root.Path, Wide(), false, ConfigOf(IndexingP), (stage, run, _) => { staged.Add(stage); return Task.FromResult(Exit.Ok); }, stderr, TimeSpan.FromMilliseconds(20));
        Assert.Equal(Exit.Ok, await again.RunAsync(new IndexRunOptions("tiny-tale", Stages: "0-5"), TestContext.Current.CancellationToken));
        Assert.Equal([1, 2, 3, 4, 5, 6], staged);
        var continued = NewestIndexRun(root.Path);
        Assert.NotEqual(runId, continued);
        Assert.Equal(("finished", "tiny-tale"), (RunJsonOf(root.Path, continued)["state"]!.GetValue<string>(), RunJsonOf(root.Path, continued)["book"]!.GetValue<string>()));
        Assert.Contains($"Next: index promote --run {continued}   ·   query --db runs/{continued}/book.sqlite", stderr.ToString());
        Assert.Equal(0, CallLogs(root.Path) - 1);   // the two lines the fake wrote in the first run; no model was called
        CliContext.Reset();
    }

    /// <summary><c>index cancel</c>'s file stops the run as Ctrl+C does (the same exit code), and continuing the run removes the file.</summary>
    [Fact(Timeout = TestTimeout)]
    public async Task Index_cancel_from_another_process_stops_the_run_with_130_and_continuing_it_removes_the_flag()
    {
        using var root = await RootWithTinyTale();
        var console = Wide();
        var stderr = new StringWriter();
        CliContext.Reset();
        var runner = new IndexRunner(root.Path, console, false, ConfigOf(IndexingP), async (stage, run, ct) =>
        {
            LeaveStageFiles(stage, run);
            if (stage != 4) return Exit.Ok;
            File.WriteAllText(run.File(RunFolder.Stage3Json), "{}\n");
            // What `index cancel --run <id>` does, from the other terminal.
            Assert.Equal(Exit.Ok, new IndexRunner(root.Path, new TestConsole(), false, ConfigOf(IndexingP)).Cancel(run.RunId));
            await Task.Delay(Timeout.Infinite, ct);
            return Exit.Ok;
        }, stderr, poll: TimeSpan.FromMilliseconds(20));

        var exit = await runner.RunAsync(new IndexRunOptions("tiny-tale"), TestContext.Current.CancellationToken);

        Assert.Equal(Exit.Cancelled, exit);
        var runId = NewestIndexRun(root.Path);
        Assert.StartsWith($"Cancelled by `index cancel --run {runId}`. Stopped at stage 4, chapter 0/3. Continue: {CliName.Wrapper} index run --from-run {runId} --stages 4-5", Assert.Single(CliContext.Problems).Detail);
        Assert.Contains("Stopped at stage 4, chapter 0/3.", console.Output);
        Assert.True(File.Exists(Path.Combine(RepoPaths.Runs(root.Path), runId, IndexRunner.CancelFile)));
        Assert.True(RunJsonOf(root.Path, runId)["cancelled"]!.GetValue<bool>());

        CliContext.Reset();
        var again = new IndexRunner(root.Path, Wide(), false, ConfigOf(IndexingP), (_, _, _) => Task.FromResult(Exit.Ok), new StringWriter(), TimeSpan.FromMilliseconds(20));
        Assert.Equal(Exit.Ok, await again.RunAsync(new IndexRunOptions(null, Stages: "4-5", FromRun: runId), TestContext.Current.CancellationToken));
        Assert.False(File.Exists(Path.Combine(RepoPaths.Runs(root.Path), runId, IndexRunner.CancelFile)));
        CliContext.Reset();
    }

    /// <summary>The rehearsal aid for the terminal checks: a held stage can be stopped, and says where.</summary>
    [Fact(Timeout = TestTimeout)]
    public async Task A_run_held_at_stage_0_can_be_stopped_and_says_how_to_start_it_again()
    {
        using var root = await RootWithTinyTale();
        var console = Wide();
        IndexRunner.HoldForTest.Value = 30;   // what TEMPORALGRAPH_STAGE_HOLD_SECONDS=30 does, for this test's calls only
        try
        {
            using var ctrlC = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
            CliContext.Reset();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var exit = await new IndexRunner(root.Path, console, false, ConfigOf(IndexingP), stderr: new StringWriter(), poll: TimeSpan.FromMilliseconds(20)).RunAsync(new IndexRunOptions("tiny-tale", Stages: "0-0"), ctrlC.Token);

            Assert.Equal(Exit.Cancelled, exit);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10));
            Assert.Contains($"Stopped at stage 0. Continue: {CliName.Wrapper} index run --book tiny-tale --stages 0-0", console.Output.Replace("\n", ""));
            Assert.Equal("TEMPORALGRAPH_STAGE_HOLD_SECONDS", IndexRunner.HoldVariable);
        }
        finally { IndexRunner.HoldForTest.Value = null; }
        CliContext.Reset();
    }

    /// <summary>"index rebuild": the current book stops as a run does, the later books are not started, the table says "stopped".</summary>
    [Fact(Timeout = TestTimeout)]
    public async Task A_rebuild_that_is_stopped_does_not_start_the_later_books_and_says_stopped()
    {
        using var root = await RootWithTwoBooks();
        using var ctrlC = new CancellationTokenSource();
        var staged = new List<(string Run, int Stage)>();
        var console = Wide();
        CliContext.Reset();
        var runner = new IndexRunner(root.Path, console, false, ConfigOf(IndexingP), async (stage, run, ct) =>
        {
            staged.Add((run.RunId, stage));
            if (stage != 2) return Exit.Ok;
            ctrlC.Cancel();
            await Task.Delay(Timeout.Infinite, ct);
            return Exit.Ok;
        }, new StringWriter(), TimeSpan.FromMilliseconds(20));

        var exit = await runner.RebuildAsync(new RebuildOptions([], Yes: true), ctrlC.Token);

        Assert.Equal(Exit.Cancelled, exit);
        Assert.Equal([1, 2], staged.Select(s => s.Stage));
        Assert.Single(staged.Select(s => s.Run).Distinct());
        var books = RebuildResult()["books"]!.AsArray();
        Assert.Equal(Exit.Cancelled, books[0]!["exit"]!.GetValue<int>());
        Assert.Null(books[1]!["exit"]);
        Assert.Contains("stopped", console.Output);
        Assert.Contains("not started", console.Output);
        Assert.Contains("The rebuild was stopped at tiny-tale; 1 book was not started.", console.Output.Replace("\n", ""));
        CliContext.Reset();
    }

    // ── The progress display ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>At a terminal: the bars with the calls, the tokens and the cost as they land, and the footer under them.</summary>
    [Fact(Timeout = TestTimeout)]
    public async Task At_a_terminal_the_progress_shows_calls_tokens_cost_and_where_the_run_is()
    {
        using var root = await RootWithTinyTale();
        var console = Terminal(input => input.PushKey(ConsoleKey.Enter));   // "Start?"
        console.Profile.Width = 240;
        var stderr = new StringWriter();
        CliContext.Reset();
        var runner = new IndexRunner(root.Path, console, interactive: true, ConfigOf(PricedP), async (stage, run, _) =>
        {
            if (stage != 1) return Exit.Ok;
            for (var i = 0; i < 3; i++)
                run.AppendCall(new { type = "call", timestamp = DateTimeOffset.UtcNow.AddSeconds(-10), stage = "stage-1", chapter = 2, chunkIndex = 1,
                    usage = new { input = 2000, output = 3000, cache_read_input_tokens = 9000, cache_creation_input_tokens = 1000 }, latencyMs = 3200.0 });
            run.AppendCall(new { type = "retry", timestamp = DateTimeOffset.UtcNow, stage = "stage-1", attempt = 1, delayMs = 2000.0, reason = "429" });
            await Task.Delay(400, CancellationToken.None);   // long enough for the watcher and a redraw
            return Exit.Ok;
        }, stderr, poll: TimeSpan.FromMilliseconds(20));

        var exit = await runner.RunAsync(new IndexRunOptions("tiny-tale", Llm: "p"), TestContext.Current.CancellationToken);

        Assert.Equal(Exit.Ok, exit);
        var output = console.Output;
        // The description as the calls land: calls done / planned, every token in / out, the cost so far (per million: 6,000 at 1, 27,000 read
        // from the cache at 0.1, 3,000 written to it at 2, and 9,000 out at 5).
        Assert.Contains("1 extract · 3/3 calls · 36k in / 9.0k out · $0.06", output);
        // Every stage and build-db is a bar from the start.
        foreach (var bar in new[] { "0 prepare", "2 resolve", "3 link", "4 time · 0/4 calls", "5 perceive · 0/3 calls", "build-db" }) Assert.Contains(bar, output);
        // The footer, through the render hook: where it is, the last call, the share of the input read from the cache, the run's ETA, how to stop.
        Assert.Matches(@"chapter 2/3 · chunk 2/3 · last call 3\.2 s · cache hits 75 % · run ETA \d[^·]* · Ctrl\+C to stop", output);
        var log = File.ReadAllText(Path.Combine(RepoPaths.Runs(root.Path), NewestIndexRun(root.Path), RunLog.FileName));
        Assert.Contains("stage-1: retry 1 after 429, waiting 2 s", log);
        Assert.Contains("stage-1: chunk 2/3 done", log);
        CliContext.Reset();
    }

    [Fact]
    public void The_progress_words_come_from_the_plan_and_the_calls_logged()
    {
        var plan = new RunPlanInfo(0, 5, 4, 6, 12, 30, new Dictionary<string, long> { ["stage-1"] = 60, ["stage-3"] = 60, ["stage-4"] = 60, ["stage-5"] = 15 },
            new Dictionary<string, long> { ["stage-1"] = 1000, ["stage-2"] = 1000, ["stage-3"] = 4000, ["stage-4"] = 2000, ["stage-5"] = 2000 });
        var stage = new StageProgress("stage-1") { Calls = 30, InputTokens = 310_412, OutputTokens = 41_000, Chapter = 5, ChunkIndex = 35, LastLatencyMs = 3210 };

        Assert.Equal("chunk 6/12", RunProgress.Unit(1, plan, stage));
        Assert.Equal("chapter 2/3", RunProgress.Unit(4, plan, new StageProgress("stage-4") { Calls = 2, Chapter = 5 }));
        Assert.Equal("7 calls", RunProgress.Unit(3, plan, new StageProgress("stage-3") { Calls = 7 }));
        Assert.Equal("chapter 2/3", RunProgress.Unit(5, plan, new StageProgress("stage-5") { Calls = 4, Chapter = 5 }));
        Assert.Equal("17 calls", RunProgress.Unit(2, plan, new StageProgress("stage-2") { Calls = 17 }));
        Assert.Equal("Stopped at stage 1, chunk 6/12.", RunProgress.StoppedAt(1, plan, stage));
        Assert.Equal("Stopped at stage 0.", RunProgress.StoppedAt(0, plan, null));
        Assert.Equal("Stopped at build-db.", RunProgress.StoppedAt(6, plan, null));
        Assert.Equal("chapter 2/3 · chunk 6/12 · last call 3.2 s", RunProgress.Where(1, plan, stage));
        Assert.Equal("stage 1: 30/60 calls · 310k/41k tokens", RunProgress.Line(1, plan, stage));
        // The calls in flight out of the limit, and the share of the input read from the prompt cache.
        var gate = new StageRunner(8);
        Assert.Equal("in flight 0/8 · cache hits 62 %", RunProgress.Flight(gate, new StageProgress("stage-1") { InputTokens = 100_000, CacheReadTokens = 62_400 }));
        Assert.Equal("in flight 0/8", RunProgress.Flight(gate, null));
        Assert.Equal("cache hits 0 %", RunProgress.Flight(null, stage));
        Assert.Equal("", RunProgress.Flight(null, null));
        Assert.Equal("stage 2: 0 calls · 0/0 tokens", RunProgress.Line(2, plan, null));
        Assert.Equal(("950", "1.2k", "48k", "1.5M", "22M"), (RunMonitor.Short(950), RunMonitor.Short(1_200), RunMonitor.Short(48_000), RunMonitor.Short(1_500_000), RunMonitor.Short(22_266_437)));
        Assert.Equal(("38 s", "4 min 10 s", "1 h 12 min"), (RunMonitor.Span(TimeSpan.FromSeconds(38)), RunMonitor.Span(TimeSpan.FromSeconds(250)), RunMonitor.Span(TimeSpan.FromMinutes(72))));
        Assert.Equal(("run ETA not known yet", "run ETA 12 min 0 s"), (RunProgress.EtaText(null), RunProgress.EtaText(TimeSpan.FromMinutes(12))));

        // A line of calls.jsonl: the fields progress needs, whatever the request and the reply hold.
        var line = """{"type":"call","timestamp":"2026-10-10T18:02:00.3+00:00","runId":"r","stage":"stage-1","bookId":"b","chapter":1,"chunkIndex":0,"request":[{"role":"system","text":"a \"quoted\" {text}"}],"response":{"modelId":"m","messages":[{"role":"assistant","text":"[1,2]"}]},"usage":{"input":50850,"output":25841,"total":76691},"latencyMs":30612.5,"error":null}""";
        var call = RunMonitor.Parse(System.Text.Encoding.UTF8.GetBytes(line))!;
        Assert.Equal(("call", "stage-1", 1, 0, 50850L, 25841L, 30612.5, false), (call.Type, call.Stage, call.Chapter, call.ChunkIndex, call.InputTokens, call.OutputTokens, call.LatencyMs, call.Failed));
        var retry = RunMonitor.Parse("""{"type":"retry","timestamp":"2026-10-10T18:02:00Z","attempt":2,"delayMs":4000,"reason":"429","error":"ClientResultException"}"""u8)!;
        Assert.Equal(("retry", 2, 4000.0, "429"), (retry.Type, retry.Attempt, retry.DelayMs, retry.Reason));
        Assert.Null(RunMonitor.Parse("""{"type":"call","usage":{"input":"""u8));   // a line still being written
        var cached = RunMonitor.Parse("""{"type":"call","stage":"stage-1","usage":{"input":57,"output":4939,"total":15340,"cache_read_input_tokens":9100,"cache_creation_input_tokens":1244}}"""u8)!;
        Assert.Equal((57L, 4939L, 9100L, 1244L), (cached.InputTokens, cached.OutputTokens, cached.CacheReadTokens, cached.CacheWriteTokens));
    }

    // ── Index watch and index status ─────────────────────────────────────────────────────────────────────────────────────

    [Fact(Timeout = TestTimeout)]
    public async Task Index_watch_on_a_finished_run_exits_0_at_once_and_follows_a_running_one_until_it_ends()
    {
        using var root = await RootWithTinyTale();
        CliContext.Reset();
        // A finished run.
        var first = new IndexRunner(root.Path, Wide(), false, ConfigOf(IndexingP), (_, _, _) => Task.FromResult(Exit.Ok), new StringWriter(), TimeSpan.FromMilliseconds(20));
        Assert.Equal(Exit.Ok, await first.RunAsync(new IndexRunOptions("tiny-tale"), TestContext.Current.CancellationToken));
        var finished = NewestIndexRun(root.Path);
        var console = Wide();
        var watch = System.Diagnostics.Stopwatch.StartNew();

        Assert.Equal(Exit.Ok, await new IndexRunner(root.Path, console, false, ConfigOf(IndexingP), poll: TimeSpan.FromSeconds(30)).WatchAsync(finished, TestContext.Current.CancellationToken));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10));
        Assert.Contains("The run has finished.", console.Output);
        Assert.Contains($"Run {finished}", console.Output);
        Assert.False(RebuildResult()["running"]!.GetValue<bool>());
        Assert.Equal(Exit.BadInput, await new IndexRunner(root.Path, Wide(), false, ConfigOf(IndexingP)).WatchAsync("no-such-run", TestContext.Current.CancellationToken));

        // A run in progress, as another process sees it: the watch shows the stage and the chunk and ends when run.json has finishedAt.
        CliContext.Reset();
        var release = new TaskCompletionSource();
        var inStage3 = new TaskCompletionSource<string>();
        var running = new IndexRunner(root.Path, Wide(), false, ConfigOf(IndexingP), async (stage, run, _) =>
        {
            if (stage != 1) return Exit.Ok;
            run.AppendCall(new { type = "call", timestamp = DateTimeOffset.UtcNow.AddSeconds(-5), stage = "stage-1", chapter = 1, chunkIndex = 0, usage = new { input = 1000, output = 200 }, latencyMs = 900.0 });
            inStage3.SetResult(run.RunId);
            await release.Task;
            return Exit.Ok;
        }, new StringWriter(), TimeSpan.FromMilliseconds(20)).RunAsync(new IndexRunOptions("tiny-tale"), TestContext.Current.CancellationToken);
        // If the run ends before it reaches Stage 1, its own failure is what this test reports.
        if (await Task.WhenAny(inStage3.Task, running).WaitAsync(Soon, TestContext.Current.CancellationToken) == running)
            Assert.Fail($"The run ended before Stage 1 with exit {await running}: {string.Join(" | ", CliContext.Problems.Select(p => p.Detail))}");
        var runId = await inStage3.Task;

        var state = new RunView(root.Path, runId).Read();
        Assert.False(state.Finished);
        Assert.Equal(("1 extract", "chunk 1/3", 1L), (state.CurrentStage, state.CurrentChunk, state.CallsSoFar));
        Assert.Equal(1 / 3.0, state.Share!.Value, 3);
        Assert.NotNull(state.RemainingSeconds);
        Assert.NotNull(state.ElapsedSeconds);
        // index status --run: the one-shot, with the same numbers.
        var status = Wide();
        CliContext.Reset();
        Assert.Equal(Exit.Ok, new IndexRunner(root.Path, status, false, ConfigOf(IndexingP)).Status(null, runId));
        Assert.Contains("running, stage 1 extract, chunk 1/3 · 1 call so far · elapsed", status.Output.Replace("\n", ""));
        var result = RebuildResult();
        Assert.Equal((true, "1 extract", "chunk 1/3", 1L), (result["running"]!.GetValue<bool>(), result["currentStage"]!.GetValue<string>(), result["currentChunk"]!.GetValue<string>(), result["callsSoFar"]!.GetValue<long>()));
        Assert.NotNull(result["elapsedSeconds"]);

        // Ctrl+C leaves the watch and not the run.
        using var ctrlC = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var watching = new StringWriter();
        var left = await new IndexRunner(root.Path, Wide(), false, ConfigOf(IndexingP), stderr: watching, poll: TimeSpan.FromMilliseconds(20)).WatchAsync(runId, ctrlC.Token);
        Assert.Equal(Exit.Cancelled, left);
        Assert.Contains(IndexRunner.WatchingNotice, watching.ToString());
        Assert.Equal("watching only — Ctrl+C leaves the watch, the run continues", IndexRunner.WatchingNotice);
        Assert.Contains("stage 1 extract, chunk 1/3", watching.ToString());
        Assert.Contains($"run {runId} continues", CliContext.Problems[^1].Detail);
        Assert.False(running.IsCompleted);

        // At a terminal: the live table with the stage's bar and the notice; it ends by itself when the run does.
        CliContext.Reset();
        var live = Terminal(_ => { });
        live.Profile.Width = 200;
        live.Profile.Height = 60;
        var followed = new IndexRunner(root.Path, live, true, ConfigOf(IndexingP), poll: TimeSpan.FromMilliseconds(20)).WatchAsync(runId, TestContext.Current.CancellationToken);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.False(followed.IsCompleted);
        release.SetResult();
        Assert.Equal(Exit.Ok, await followed.WaitAsync(Soon, TestContext.Current.CancellationToken));
        Assert.Equal(Exit.Ok, await running.WaitAsync(Soon, TestContext.Current.CancellationToken));
        Assert.Contains("1 extract", live.Output);
        Assert.Contains("33%", live.Output);
        Assert.Contains(IndexRunner.WatchingNotice, live.Output);
        Assert.Contains("The run has finished.", live.Output);
        Assert.Contains("run.log", live.Output);                       // the log panel under the progress
        Assert.Contains("stage 2 resolve: done", live.Output);
        CliContext.Reset();
    }

    // ── The book pickers ───────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact(Timeout = TestTimeout)]
    public async Task Every_book_picker_has_add_a_book_first_and_back_last()
    {
        using var root = await RootWithTinyTale();
        var ct = TestContext.Current.CancellationToken;
        Assert.Equal("+ Add a book from Project Gutenberg…", BookPicker.AddLabel);
        Assert.Equal("← Back", Prompts.Back);

        static void HasBoth(TestConsole console, string book)
        {
            var output = console.Output;
            Assert.Contains("Which book?", output);
            var (add, at, back) = (output.IndexOf(BookPicker.AddLabel, StringComparison.Ordinal), output.IndexOf(book, StringComparison.Ordinal), output.LastIndexOf(Prompts.Back, StringComparison.Ordinal));
            Assert.True(add >= 0 && back > add, output);
            if (book.Length > 0) Assert.True(at > add && back > at, output);   // the entry to add first, the books, then Back
        }

        // The menu's "Query a book": End, Enter is "← Back".
        CliContext.Reset();
        var query = Terminal(input => { input.PushKey(ConsoleKey.End); input.PushKey(ConsoleKey.Enter); });
        Assert.Equal(Exit.Ok, await HomeCommand.QueryAsync(root.Path, query, ct, _ => throw new InvalidOperationException("the intake ran")));
        HasBoth(query, "tiny-tale — A Tiny Tale");
        // Enter alone is the first book (the cursor starts on it): the query console opens on it.
        var opened = Terminal(input => input.PushKey(ConsoleKey.Enter));
        Assert.Equal(Exit.Ok, await HomeCommand.QueryAsync(root.Path, opened, ct, _ => throw new InvalidOperationException("the intake ran"), new ScriptedLines([".quit"])));
        Assert.Contains("tiny-tale: ", opened.Output);

        // Esc is Back too (the cancel result).
        var escaped = Terminal(input => input.PushKey(ConsoleKey.Escape));
        Assert.Equal(Exit.Ok, await HomeCommand.QueryAsync(root.Path, escaped, ct, _ => throw new InvalidOperationException("the intake ran")));
        Assert.Contains("Which book? (Esc: back)", escaped.Output);

        // `index run` at a terminal: Up, Enter is "+ Add a book…", which runs the intake and goes on with the book it added.
        var added = new List<string>();
        var index = Terminal(input =>
        {
            input.PushKey(ConsoleKey.UpArrow); input.PushKey(ConsoleKey.Enter);   // + Add a book…
            input.PushKey(ConsoleKey.Enter);                                       // "Start indexing?": no (the cost is not known)
        });
        var runner = new IndexRunner(root.Path, index, true, ConfigOf(IndexingP), (_, _, _) => throw new InvalidOperationException("a stage ran"),
            addBook: _ => { added.Add("asked"); return Task.FromResult<string?>("tiny-tale"); });
        Assert.Equal(Exit.Ok, await runner.RunAsync(new IndexRunOptions(null), ct));
        HasBoth(index, "tiny-tale — A Tiny Tale");
        Assert.Equal(["asked"], added);
        Assert.Contains("Nothing was run.", index.Output);
        // Back from the books leaves, by its row or by Esc.
        foreach (var keys in new Action<TestConsoleInput>[] { i => { i.PushKey(ConsoleKey.End); i.PushKey(ConsoleKey.Enter); }, i => i.PushKey(ConsoleKey.Escape) })
        {
            var backOut = Terminal(keys);
            Assert.Equal(Exit.Ok, await new IndexRunner(root.Path, backOut, true, ConfigOf(IndexingP)).RunAsync(new IndexRunOptions(null), ct));
            Assert.DoesNotContain("Estimate", backOut.Output);
        }

        // The chat console: only indexed books are listed (none here), with both entries; Back leaves without a model call.
        using var hound = new TempRoot().WithHound();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Llm:Type"] = "OpenAI", ["Llm:Model"] = "m", ["Llm:Endpoint"] = "http://localhost:1/openai/v1/", ["Llm:ApiKey"] = "fake-key-for-tests-0123456789",
        }).Build();
        var chat = Terminal(input => { input.PushKey(ConsoleKey.End); input.PushKey(ConsoleKey.Enter); });
        Assert.Equal(Exit.Ok, await ChatConsole.OpenAsync(hound.Path, config, null, null, null, null, null, ct, chat, _ => throw new InvalidOperationException("the intake ran")));
        HasBoth(chat, "");
        // "+ Add a book…" goes on with the book added: who to talk to (with "← Back", which returns to the books), then Back again.
        var runsBefore = Runs(hound.Path);
        var chatAdd = Terminal(input =>
        {
            input.PushKey(ConsoleKey.Enter);                                    // + Add a book… (the only entry before Back)
            input.PushKey(ConsoleKey.End); input.PushKey(ConsoleKey.Enter);     // "Who do you want to talk to?": ← Back
            input.PushKey(ConsoleKey.End); input.PushKey(ConsoleKey.Enter);     // the books: ← Back
        });
        Assert.Equal(Exit.Ok, await ChatConsole.OpenAsync(hound.Path, config, null, null, null, null, null, ct, chatAdd, _ => Task.FromResult<string?>(Hound)));
        Assert.Contains("Who do you want to talk to?", chatAdd.Output);
        Assert.Contains("Someone else…", chatAdd.Output);
        Assert.Equal(runsBefore, Runs(hound.Path));   // the session that was left before a question left no run folder
        CliContext.Reset();
    }

    // ── "Index a book" at a terminal ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>The book, a pre-flight panel, exactly one confirmation, then the bars: no stage picker anywhere.</summary>
    [Fact(Timeout = TestTimeout)]
    public async Task Index_a_book_at_a_terminal_reaches_the_progress_after_exactly_one_confirmation_and_never_asks_for_stages()
    {
        using var root = await RootWithTinyTale();
        var staged = new List<int>();
        var console = Terminal(input =>
        {
            input.PushKey(ConsoleKey.Enter);    // the book
            input.PushKey(ConsoleKey.Enter);    // "Start indexing?": the cost is known, Enter is yes
            input.PushKey(ConsoleKey.Enter);    // after the run: "Promote …?" is not asked here (the fake build-db leaves no leak tests); a spare key
        });
        console.Profile.Width = 200;
        CliContext.Reset();
        var runner = new IndexRunner(root.Path, console, interactive: true, ConfigOf([.. PricedP, ("Indexing:Llm", "p")]), (stage, _, _) => { staged.Add(stage); return Task.FromResult(Exit.Ok); },
            new StringWriter(), TimeSpan.FromMilliseconds(20));

        var exit = await runner.RunAsync(new IndexRunOptions(null), TestContext.Current.CancellationToken);

        Assert.Equal(Exit.Ok, exit);
        Assert.Equal([1, 2, 3, 4, 5, 6], staged);                         // the whole pipeline, as it is: no subset was asked for
        var output = console.Output;
        Assert.DoesNotContain("Which stages", output);
        Assert.Single(Regex.Matches(output, Regex.Escape("[y/n]")));       // exactly one confirmation
        Assert.Contains("Start indexing? [y/n] (y):", output);
        // The pre-flight panel: title, chapters, chunks, model, estimate, the last run.
        Assert.Contains("Index a book", output);
        foreach (var row in new[] { "A Tiny Tale (tiny-tale)", "Chapters", "Chunks", "model-p (profile p)", "about 379,200 tokens; cost about 0.86 USD", "Last run" }) Assert.Contains(row, output);
        // Then the run's first lines and the bars: every stage and build-db.
        var (question, firstLine, bars) = (output.IndexOf("Start indexing?", StringComparison.Ordinal), output.IndexOf("· stages 0-5 · 3 chapters, 3 chunks · model model-p", StringComparison.Ordinal), output.IndexOf("5 perceive", StringComparison.Ordinal));
        Assert.True(question >= 0 && firstLine > question && bars > firstLine, output);
        foreach (var bar in new[] { "0 prepare", "1 extract", "2 resolve", "3 link", "4 time", "5 perceive", "build-db" }) Assert.Contains(bar, output);
        Assert.DoesNotContain(IndexRunner.ContinueChoice, output);         // no stopped run, no such choice
        Assert.Equal(0, CallLogs(root.Path));
        CliContext.Reset();
    }

    /// <summary>The choice to continue appears only when the book's newest run stopped; taking it continues from that stage.</summary>
    [Fact(Timeout = TestTimeout)]
    public async Task Index_a_book_offers_to_continue_only_when_the_books_newest_run_stopped()
    {
        using var root = await RootWithTinyTale();
        var ct = TestContext.Current.CancellationToken;
        var config = ConfigOf([.. PricedP, ("Indexing:Llm", "p")]);
        CliContext.Reset();
        Assert.Null(new IndexRunner(root.Path, Wide(), false, config).StoppedRun("tiny-tale"));

        // A run that stops in Stage 3.
        using var ctrlC = new CancellationTokenSource();
        var first = new IndexRunner(root.Path, Wide(), false, config, async (stage, run, token) =>
        {
            LeaveStageFiles(stage, run);
            if (stage != 3) return Exit.Ok;
            ctrlC.Cancel();
            await Task.Delay(Timeout.Infinite, token);
            return Exit.Ok;
        }, new StringWriter(), TimeSpan.FromMilliseconds(20));
        Assert.Equal(Exit.Cancelled, await first.RunAsync(new IndexRunOptions("tiny-tale"), ctrlC.Token));
        var stoppedId = NewestIndexRun(root.Path);
        var stopped = new IndexRunner(root.Path, Wide(), false, config).StoppedRun("tiny-tale");
        Assert.Equal((stoppedId, 3), (stopped!.Value.RunId, stopped.Value.Stage));

        // The menu now says so and asks which: Enter is "Continue the stopped run from stage 3"; it runs 3, 4, 5 and build-db.
        var staged = new List<int>();
        var console = Terminal(input => { input.PushKey(ConsoleKey.Enter); input.PushKey(ConsoleKey.Enter); });
        console.Profile.Width = 200;
        CliContext.Reset();
        var runner = new IndexRunner(root.Path, console, true, config, (stage, _, _) => { staged.Add(stage); return Task.FromResult(Exit.Ok); }, new StringWriter(), TimeSpan.FromMilliseconds(20));
        Assert.Equal(Exit.Ok, await runner.RunAsync(new IndexRunOptions(null), ct));
        Assert.Equal([3, 4, 5, 6], staged);
        var output = console.Output;
        Assert.Contains($"A run stopped at stage 3 on ", output);
        Assert.Contains($"(run {stoppedId}).", output);
        Assert.Contains("Start indexing? (Esc: back)", output);
        foreach (var choice in new[] { "Continue the stopped run from stage 3", "Start a new full run", "← Back" }) Assert.Contains(choice, output);
        Assert.DoesNotContain("[y/n]", output);                            // the choice is the one confirmation
        Assert.DoesNotContain("Which stages", output);
        Assert.False(File.Exists(Path.Combine(RepoPaths.Runs(root.Path), stoppedId, IndexRunner.CancelFile)));

        // The newest run finished: nothing to continue, the plain question again.
        Assert.Null(new IndexRunner(root.Path, Wide(), false, config).StoppedRun("tiny-tale"));
        var again = Terminal(input => { input.PushKey(ConsoleKey.Enter); input.PushTextWithEnter("n"); });
        Assert.Equal(Exit.Ok, await new IndexRunner(root.Path, again, true, config, (_, _, _) => throw new InvalidOperationException("a stage ran")).RunAsync(new IndexRunOptions(null), ct));
        Assert.DoesNotContain(IndexRunner.ContinueChoice, again.Output);
        Assert.Contains("Start indexing? [y/n] (y):", again.Output);
        Assert.Contains("Nothing was run.", again.Output);
        CliContext.Reset();
    }

    // ── The consoles ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Reads lines from a script and, where the script says so, presses Ctrl+C instead.</summary>
    private sealed class LinesWithCtrlC(IEnumerable<string> lines) : ILineReader
    {
        public const string CtrlC = "\u0003";
        private readonly Queue<string> _lines = new(lines);
        public List<string> Prompts { get; } = [];

        public string? Read(string prompt, CancellationToken ct = default)
        {
            Prompts.Add(prompt);
            if (_lines.Count == 0) return null;
            var line = _lines.Dequeue();
            if (line != CtrlC) return line;
            Interrupts.Press();
            ct.ThrowIfCancellationRequested();
            return null;
        }

        public void Remember(string line) { }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task Ctrl_c_during_a_chat_answer_cancels_that_answer_and_shows_the_prompt_again_and_at_the_input_line_it_leaves()
    {
        using var root = new TempRoot().WithHound();
        using var server = new FakeLlmServer { DelayMs = 20_000 };   // the model takes its time: only Ctrl+C ends the call
        var config = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "templates", "cli.appsettings.json"))
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Llm:Type"] = "OpenAI", ["Llm:Model"] = "my-deployment", ["Llm:Endpoint"] = server.BaseUrl + "/openai/v1/", ["Llm:ApiKey"] = "fake-key-for-tests-0123456789",
            }).Build();
        CliContext.Reset();
        CliContext.StderrOverride = new StringWriter();
        var (session, opened) = ChatSession.Open(root.Path, config, Hound, null);
        Assert.Equal(Exit.Ok, opened);
        using var _ = session;
        Assert.Null(session!.Begin("Sherlock Holmes", 3, false));
        var console = Wide();
        var lines = new LinesWithCtrlC(["Who are you?", LinesWithCtrlC.CtrlC, "never read"]);
        using var process = new CancellationTokenSource();
        using var listening = Interrupts.Listen(process);
        // Ctrl+C once the question is on its way to the model.
        var answering = System.Diagnostics.Stopwatch.StartNew();
        var press = Task.Run(async () =>
        {
            while (server.Requests.Count == 0) await Task.Delay(20, TestContext.Current.CancellationToken);
            answering.Restart();
            Interrupts.Press();
        }, TestContext.Current.CancellationToken);

        var exit = await new ChatConsole(session, console, lines).RunAsync(process.Token).WaitAsync(Soon, TestContext.Current.CancellationToken);
        var stopped = answering.Elapsed;
        await press.WaitAsync(Soon, TestContext.Current.CancellationToken);

        Assert.True(stopped < TimeSpan.FromSeconds(3), $"the answer stopped {stopped.TotalSeconds:0.0} s after Ctrl+C");   // at once, not when the model is done

        Assert.Contains("(cancelled)", console.Output);
        Assert.Equal(2, lines.Prompts.Count);                       // the prompt came back after the cancelled answer
        Assert.Empty(session.AllTurns);
        // The second Ctrl+C was at the input line: "Cancelled.", the transcript saved, exit 130.
        Assert.Equal(Exit.Cancelled, exit);
        Assert.True(process.IsCancellationRequested);
        Assert.Contains("Transcript: ", console.Output);
        Assert.True(File.Exists(session.Run.File(RunFolder.RunJson)));
        Assert.Equal("cancelled", Assert.Single(CliContext.Problems).Type);
        CliContext.StderrOverride = null;
        CliContext.Reset();
    }

    [Fact(Timeout = TestTimeout)]
    public async Task Ctrl_c_at_the_query_console_s_input_line_leaves_with_cancelled_and_130()
    {
        using var root = new TempRoot().WithHound();
        var graph = Query.BookGraph.Load(Path.Combine(RepoPaths.Books(root.Path), Hound + ".sqlite"));
        var console = Wide();
        var lines = new LinesWithCtrlC(["RETURN 1 AS one;", LinesWithCtrlC.CtrlC, "RETURN 2 AS never;"]);
        CliContext.Reset();
        CliContext.StderrOverride = new StringWriter();
        using var process = new CancellationTokenSource();
        using var listening = Interrupts.Listen(process);

        var exit = await new QueryConsole(new QuerySession(graph, Hound, root.Path, 15, false, null), console, lines).RunAsync(process.Token);

        Assert.Equal(Exit.Cancelled, exit);
        Assert.Contains("one", console.Output);
        Assert.DoesNotContain("never", console.Output);
        Assert.Equal("Cancelled.", Assert.Single(CliContext.Problems).Detail);
        CliContext.StderrOverride = null;
        CliContext.Reset();
    }

    // ── Books add ────────────────────────────────────────────────────────────────────────────────────────────────────────

    private sealed class SlowWeb(Func<Uri, HttpResponseMessage> respond, Func<Uri, bool> hangs) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        public TaskCompletionSource Hanging { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.ToString());
            if (!hangs(request.RequestUri)) return respond(request.RequestUri);
            Hanging.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException();
        }
    }

    [Fact(Timeout = TestTimeout)]
    public async Task Books_add_cancelled_during_the_download_leaves_no_text_and_the_manifest_untouched()
    {
        using var root = await RootWithTinyTale();
        var manifestPath = Path.Combine(RepoPaths.Books(root.Path), BookManifest.FileName);
        var manifestBefore = File.ReadAllText(manifestPath);
        var filesBefore = Directory.GetFiles(Path.Combine(RepoPaths.Books(root.Path), "source")).Order().ToList();
        var web = new SlowWeb(uri => Bytes("""{"id":99005,"title":"A Slow Book","authors":[{"name":"Doe, Jane"}],"formats":{"text/plain; charset=utf-8":"https://www.gutenberg.org/ebooks/99005.txt.utf-8"}}"""), uri => uri.Host != "gutendex.com");
        var intake = new BookIntake(new HttpClient(web), root.Path, (_, _) => Task.CompletedTask);
        using var ctrlC = new CancellationTokenSource();
        CliContext.Reset();

        var adding = new BooksRunner(intake, root.Path, new TestConsole(), interactive: false).AddAsync(new BookAddOptions("99005"), ctrlC.Token);
        await web.Hanging.Task.WaitAsync(Soon, TestContext.Current.CancellationToken);
        ctrlC.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adding.WaitAsync(Soon, TestContext.Current.CancellationToken));
        Assert.Equal(["https://gutendex.com/books/99005", "https://www.gutenberg.org/ebooks/99005.txt.utf-8"], web.Requests);
        Assert.Equal(manifestBefore, File.ReadAllText(manifestPath));
        Assert.Equal(filesBefore, Directory.GetFiles(Path.Combine(RepoPaths.Books(root.Path), "source")).Order().ToList());   // no slow-book.txt, no .part
        Assert.False(File.Exists(Path.Combine(RepoPaths.Books(root.Path), "slow-book.sqlite")));
        CliContext.Reset();
    }

    /// <summary><c>books add --dry-run</c> says what would be fetched and the id, and fetches and writes nothing.</summary>
    [Fact(Timeout = TestTimeout)]
    public async Task Books_add_dry_run_shows_what_would_be_fetched_and_the_id_and_writes_nothing()
    {
        using var root = await RootWithTinyTale();
        var manifestBefore = File.ReadAllText(Path.Combine(RepoPaths.Books(root.Path), BookManifest.FileName));
        var (intake, web, _) = Intake(root, uri => uri.Host == "gutendex.com"
            ? Bytes("""{"id":99001,"title":"A Tiny Tale","authors":[{"name":"Doe, Jane"}],"formats":{"text/plain; charset=us-ascii":"https://www.gutenberg.org/ebooks/99001.txt.utf-8"}}""")
            : throw new InvalidOperationException("the text was requested"));
        var console = Wide();
        var runs = Runs(root.Path);
        CliContext.Reset();

        var exit = await new BooksRunner(intake, root.Path, console, interactive: false).DryRunAsync(new BookAddOptions("99001"), TestContext.Current.CancellationToken);

        Assert.Equal(Exit.Ok, exit);
        Assert.Equal(["https://gutendex.com/books/99001"], web.Requests.Select(r => r.Url));   // the catalogue, for the title; not the text
        Assert.Contains("Would add tiny-tale: A Tiny Tale, Jane Doe", console.Output);
        Assert.Contains("from https://www.gutenberg.org/ebooks/99001.txt.utf-8", console.Output);
        Assert.Contains("tiny-tale is already in data/books/books.json", console.Output);
        Assert.Contains("Dry run: nothing was fetched or written.", console.Output);
        var result = RebuildResult();
        Assert.Equal((true, "tiny-tale", true), (result["dryRun"]!.GetValue<bool>(), result["id"]!.GetValue<string>(), result["exists"]!.GetValue<bool>()));
        Assert.Equal(manifestBefore, File.ReadAllText(Path.Combine(RepoPaths.Books(root.Path), BookManifest.FileName)));
        Assert.Equal(runs, Runs(root.Path));
        // A .txt URL: the id is the text's own Title: line, unless it is given.
        var url = await intake.PlanAsync(new BookAddOptions("https://example.org/t/x.txt"), TestContext.Current.CancellationToken);
        Assert.Equal((null, false), (url.Id, url.Exists));
        Assert.Equal("new-id", (await intake.PlanAsync(new BookAddOptions("https://example.org/t/x.txt", Id: "new-id"), TestContext.Current.CancellationToken)).Id);
        Assert.Equal(Exit.BadInput, await new BooksRunner(intake, root.Path, Wide(), false).DryRunAsync(new BookAddOptions("the hound"), TestContext.Current.CancellationToken));
        CliContext.Reset();
    }

    /// <summary><c>books add</c> logs under its run folder: each request and what came back, what was written, Stage 0's result.</summary>
    [Fact(Timeout = TestTimeout)]
    public async Task Books_add_writes_its_run_log_and_index_run_has_a_dry_run()
    {
        using var root = new TempRoot();
        var text = Book("A Tiny Tale", "Jane Doe", CapsBody);
        var (intake, _, _) = Intake(root, uri => uri.Host == "gutendex.com" ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : uri.AbsolutePath.EndsWith("/pg99001.txt") ? Bytes(text) : new HttpResponseMessage(HttpStatusCode.NotFound));
        var console = Wide();
        CliContext.Reset();

        var (exit, outcome) = await new BooksRunner(intake, root.Path, console, interactive: false).AddAsync(new BookAddOptions("99001"), TestContext.Current.CancellationToken);

        Assert.Equal(Exit.Ok, exit);
        var log = File.ReadAllText(Path.Combine(RepoPaths.Runs(root.Path), outcome!.RunId!, RunLog.FileName));
        Assert.Contains("books add 99001", log);
        Assert.Contains("GET https://gutendex.com/books/99001", log);
        Assert.Contains("HTTP 503", log);
        Assert.Contains("GET https://www.gutenberg.org/ebooks/99001.txt.utf-8", log);
        Assert.Contains("HTTP 404", log);
        Assert.Contains("wrote data/books/source/tiny-tale.txt", log);
        Assert.Contains("data/books/books.json: tiny-tale added", log);
        Assert.Contains("stage 0: 3 chapters (detected profile); checks pass", log);
        Assert.Contains($"Log: runs/{outcome.RunId}/run.log", console.Output);
        Assert.All(log.Split('\n', StringSplitOptions.RemoveEmptyEntries), l => Assert.Matches(@"^(\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}Z |    )", l));

        // index run --dry-run: the pre-flight and the estimate; no run folder, no stage; a budget still refuses.
        var runs = Runs(root.Path);
        var dry = Wide();
        var runner = new IndexRunner(root.Path, dry, false, ConfigOf(IndexingP), (_, _, _) => throw new InvalidOperationException("a stage ran"));
        Assert.Equal(Exit.Ok, await runner.RunAsync(new IndexRunOptions("tiny-tale", DryRun: true), TestContext.Current.CancellationToken));
        Assert.Contains("Dry run: nothing was run.", dry.Output);
        Assert.Equal((true, "tiny-tale", 3), (RebuildResult()["dryRun"]!.GetValue<bool>(), RebuildResult()["book"]!.GetValue<string>(), RebuildResult()["estimate"]!["chunks"]!.GetValue<int>()));
        Assert.Equal(Exit.NotReady, await runner.RunAsync(new IndexRunOptions("tiny-tale", DryRun: true, BudgetTokens: 1), TestContext.Current.CancellationToken));
        Assert.Equal(runs, Runs(root.Path));
        CliContext.Reset();
    }

    // ── The next step ──────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact(Timeout = TestTimeout)]
    public async Task The_next_step_follows_a_state_change_on_stderr_and_quiet_and_json_suppress_it()
    {
        using var root = await RootWithTinyTale();
        var ct = TestContext.Current.CancellationToken;
        Func<int, RunFolder, CancellationToken, Task<int>> fake = (_, _, _) => Task.FromResult(Exit.Ok);
        IndexRunner Runner(StringWriter stderr) => new(root.Path, Wide(), false, ConfigOf(IndexingP), fake, stderr, TimeSpan.FromMilliseconds(20));

        // index run that ended before Stage 5: continue it. Stage 0 alone: index the book.
        CliContext.Reset();
        var partial = new StringWriter();
        Assert.Equal(Exit.Ok, await Runner(partial).RunAsync(new IndexRunOptions("tiny-tale", Stages: "0-3"), ct));
        Assert.Contains($"Next: index run --from-run {NewestIndexRun(root.Path)} --stages 4-5", partial.ToString());
        var stage0 = new StringWriter();
        Assert.Equal(Exit.Ok, await Runner(stage0).RunAsync(new IndexRunOptions("tiny-tale", Stages: "0-0"), ct));
        Assert.Contains("Next: index run --book tiny-tale", stage0.ToString());
        // A whole run, not promoted: promote it, and which database a query reads.
        var whole = new StringWriter();
        Assert.Equal(Exit.Ok, await Runner(whole).RunAsync(new IndexRunOptions("tiny-tale"), ct));
        var runId = NewestIndexRun(root.Path);
        Assert.Contains($"Next: index promote --run {runId}   ·   query --db runs/{runId}/book.sqlite", whole.ToString());

        // --quiet and --json: no next step.
        foreach (var (json, quiet) in new[] { (false, true), (true, false) })
        {
            CliContext.Reset(json, quiet);
            var silent = new StringWriter();
            Assert.Equal(Exit.Ok, await Runner(silent).RunAsync(new IndexRunOptions("tiny-tale", Stages: "0-0"), ct));
            Assert.DoesNotContain("Next:", silent.ToString());
        }

        // The other state changes: a book added, a run promoted, a setup that saved.
        CliContext.Reset();
        var stderr = new StringWriter();
        CliContext.StderrOverride = stderr;
        try
        {
            CliContext.Next(BooksRunner.NextAfterAdd("tiny-tale"));
            Assert.Equal($"Next: {CliName.Wrapper} index run --book tiny-tale   (preview: --dry-run)" + Environment.NewLine, stderr.ToString());
            Assert.Equal($"{CliName.Wrapper}   (menu)   ·   books add <number>", TemporalGraph.Cli.Setup.SetupRunner.NextAfterSetup);
            stderr.GetStringBuilder().Clear();
            // setup add, without a terminal: saved, then the next step.
            var store = new TemporalGraph.Cli.Setup.MemoryStore("tg61-test", new Dictionary<string, string>());
            var setup = new TemporalGraph.Cli.Setup.SetupRunner(store, () => new ConfigurationBuilder().Build(), () => [], new TestConsole(), new StringWriter(),
                new StringReader("fake-key-for-tests-0123456789"), false, () => root.Path);
            Assert.Equal(0, await setup.AddAsync(new TemporalGraph.Cli.Setup.AddOptions("openai", "openai", "gpt-4o-mini", KeyStdin: true, Default: true), json: false, ct));
            Assert.Equal($"Next: {CliName.Wrapper}   (menu)   ·   books add <number>" + Environment.NewLine, stderr.ToString());
            stderr.GetStringBuilder().Clear();
            CliContext.Reset(quiet: true);
            CliContext.Next("anything");
            CliContext.Reset(json: true);
            CliContext.Next("anything");
            Assert.Equal("", stderr.ToString());
        }
        finally { CliContext.StderrOverride = null; }
        CliContext.Reset();
    }

    // ── An unexpected error ────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void An_unexpected_error_is_one_line_with_the_log_that_has_the_stack_trace()
    {
        using var root = new TempRoot();
        File.WriteAllText(Path.Combine(root.Path, RepoPaths.Marker), "");
        var (shown, console) = (AnsiConsole.Console, Wide());
        AnsiConsole.Console = console;
        RepoPaths.Override = root.Path;
        try
        {
            Exception thrown;
            try { throw new InvalidOperationException("the widget broke."); }
            catch (InvalidOperationException ex) { thrown = ex; }

            // No run: the CLI's own log.
            CliContext.Reset();
            RunLog.Current = null;
            Assert.Equal(Exit.Failed, CliApp.Unexpected(thrown, ["index", "run", "--book", "x"]));
            var problem = Assert.Single(CliContext.Problems);
            Assert.Equal("unexpected-error", problem.Type);
            Assert.Equal("Unexpected error: InvalidOperationException: the widget broke. Details: runs/cli.log.", problem.Detail);
            Assert.Contains("Unexpected error: InvalidOperationException: the widget broke. Details: runs/cli.log", console.Output.Replace("\n", ""));
            Assert.DoesNotContain("   at ", console.Output);                       // no stack trace on the terminal
            var log = File.ReadAllText(Path.Combine(root.Path, "runs", "cli.log"));
            Assert.Contains("unexpected error in `index run`: System.InvalidOperationException: the widget broke.", log);
            Assert.Contains("   at ", log);                                          // it is in the log

            // "Report:" is the issues page of the repository README.md (else NOTICE.md) links to; neither names it yet, so it is left out.
            Assert.DoesNotContain("Report:", problem.Detail);
            Assert.Null(CliApp.IssuesUrl(root.Path));
            File.WriteAllText(Path.Combine(root.Path, "NOTICE.md"), "See [the code](https://github.com/some-org/Some.Repo/blob/main/x.cs).");
            Assert.Equal("https://github.com/some-org/Some.Repo/issues", CliApp.IssuesUrl(root.Path));
            File.WriteAllText(Path.Combine(root.Path, "README.md"), "Clone https://github.com/other-org/the-repo.git and run it.");
            Assert.Equal("https://github.com/other-org/the-repo/issues", CliApp.IssuesUrl(root.Path));
            CliContext.Reset();
            CliApp.Unexpected(thrown, ["query"]);
            Assert.EndsWith("Details: runs/cli.log. Report: https://github.com/other-org/the-repo/issues", CliContext.Problems[0].Detail);
            Assert.Null(CliApp.IssuesUrl(RepoPaths.FindRoot() is var _ ? Path.Combine(root.Path, "no-such-folder") : ""));

            // During a run: that run's log. With --debug the stack trace is printed too.
            var run = RunFolder.Create(RepoPaths.Runs(root.Path));
            RunLog.Current = RunLog.For(run);
            CliContext.Reset(debug: true);
            CliApp.Unexpected(thrown, ["index", "run"]);
            Assert.Contains($"Details: runs/{run.RunId}/run.log.", CliContext.Problems[0].Detail);
            Assert.Contains("   at ", File.ReadAllText(run.File(RunLog.FileName)));
            Assert.Contains("   at ", console.Output);
        }
        finally
        {
            AnsiConsole.Console = shown;
            RepoPaths.Override = null;
            RunLog.Current = null;
            CliContext.Reset();
        }
    }

    [Fact]
    public void Verbose_echoes_the_log_lines_and_debug_adds_the_calls()
    {
        using var root = new TempRoot();
        var run = RunFolder.Create(RepoPaths.Runs(root.Path));
        var echo = new StringWriter();
        var log = RunLog.For(run, echo);

        CliContext.Reset();
        log.Write("stage 1 mentions: start");
        log.Debug("stage-1: call · 100 tokens in");
        Assert.Equal("", echo.ToString());
        Assert.Single(File.ReadAllLines(run.File(RunLog.FileName)));

        CliContext.Reset(verbose: true);
        log.Write("stage 1 mentions: done");
        log.Debug("stage-1: call · 100 tokens in");
        Assert.EndsWith(" stage 1 mentions: done" + Environment.NewLine, echo.ToString());
        Assert.Equal(2, File.ReadAllLines(run.File(RunLog.FileName)).Length);

        CliContext.Reset(debug: true);
        Assert.True(CliContext.Verbose);
        log.Debug("stage-1: call · 100 tokens in");
        Assert.Contains("stage-1: call · 100 tokens in", echo.ToString());
        Assert.Equal(3, File.ReadAllLines(run.File(RunLog.FileName)).Length);

        // --debug: each call's size and timing, never its content.
        var line = """{"type":"call","timestamp":"2026-10-10T18:02:00Z","stage":"stage-1","chapter":1,"chunkIndex":0,"request":[{"role":"user","text":"the secret prompt"}],"response":{"messages":[{"role":"assistant","text":"the secret reply"}]},"usage":{"input":50,"output":20},"latencyMs":812.4,"error":null}""";
        var call = RunMonitor.Parse(System.Text.Encoding.UTF8.GetBytes(line))!;
        Assert.Equal((44L, 61L), (call.RequestBytes, call.ResponseBytes));
        new CallLogLines(log).Add([call], []);
        var debug = File.ReadAllLines(run.File(RunLog.FileName))[^1];
        Assert.EndsWith(" stage-1: call ch 1 chunk 0 · request 44 bytes, reply 61 bytes · 50 tokens in, 20 out · 812 ms", debug);
        Assert.DoesNotContain("secret", File.ReadAllText(run.File(RunLog.FileName)));

        // A log that starts before its folder exists keeps its lines until the file is named.
        var early = new RunLog();
        early.Write("GET https://example.org/x.txt");
        early.AttachTo(Path.Combine(root.Path, "runs", "later", RunLog.FileName));
        early.Write("HTTP 200");
        Assert.Equal(2, File.ReadAllLines(Path.Combine(root.Path, "runs", "later", RunLog.FileName)).Length);
        CliContext.Reset();
    }

    /// <summary>Every index command leaves lines in its run's log, not only <c>index run</c>.</summary>
    [Fact(Timeout = TestTimeout)]
    public async Task Every_index_command_that_works_on_a_run_writes_its_run_log()
    {
        using var root = new TempRoot();
        File.WriteAllText(Path.Combine(root.Path, RepoPaths.Marker), "");
        var run = RunFolder.Create(RepoPaths.Runs(root.Path));
        run.RecordCommand(new { command = "test" }, new Dictionary<string, object?> { ["book"] = "a-book" });
        string Log() => File.ReadAllText(run.File(RunLog.FileName));

        // A command on the run (here one that refuses: the run has no stage files): its command line, then its exit code.
        var (exit, _) = await Run("index", "build-db", "--run", run.RunId, "--repo", root.Path, "--json");
        Assert.Equal(Exit.BadInput, exit);
        Assert.Contains($"command: index build-db --run {run.RunId}", Log());
        Assert.Contains($"command ended: exit 2 · index build-db --run {run.RunId}", Log());
        // Reading a run leaves its log alone.
        var before = Log();
        await Run("index", "status", "--run", run.RunId, "--repo", root.Path, "--json");
        await Run("index", "watch", "--run", run.RunId, "--repo", root.Path, "--json");
        Assert.Equal(before, Log());

        // A stage command on its own: its start and end, each chunk done, each retry and each warning.
        CliContext.Reset();
        RunLog.Current = null;
        StageLogs.Begin(run, "stage-3");
        foreach (var chunk in new[] { 7, 7, 8 })
            run.AppendCall(new { type = "call", timestamp = DateTimeOffset.UtcNow, stage = "stage-3", chapter = 2, chunkIndex = chunk, usage = new { input = 10, output = 2 }, latencyMs = 5.0 });
        run.AppendCall(new { type = "retry", timestamp = DateTimeOffset.UtcNow, stage = "stage-3", attempt = 1, delayMs = 1000.0, reason = "503" });
        run.AppendFailure(new { type = "quote mismatch", stage = "stage-3", chapter = 2, chunkIndex = 8, reason = "no such text" });
        StageLogs.End(run, "stage-3", 3, 30, 6);
        var lines = Log().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l[(l.IndexOf(' ') + 1)..]).ToList();
        Assert.Equal(["stage-3: start", "stage-3: chunk 7 done", "stage-3: retry 1 after 503, waiting 1 s", "warning stage-3: quote mismatch (chapter 2, chunk 8): no such text", "stage-3: chunk 8 done", "stage-3: done · 3 calls · 30/6 tokens in/out"],
            lines.SkipWhile(l => l != "stage-3: start"));
        Assert.Null(RunLog.Current);
        StageLogs.Line(run, "build-db: start");
        Assert.EndsWith("build-db: start\n", Log());
        CliContext.Reset();
    }

    // ── Help, version, colour ──────────────────────────────────────────────────────────────────────────────────────────────

    private static (int Exit, string Output) Help(params string[] args)
    {
        var console = new TestConsole();
        console.Profile.Width = 200;
        var app = new CommandApp<HomeCommand>();
        app.Configure(config =>
        {
            CliApp.Configure(config);
            config.Settings.Console = console;
        });
        return (app.Run(args), console.Output);
    }

    [Fact]
    public void Every_command_s_help_has_an_example_and_lists_its_own_options_first()
    {
        Assert.True(CliApp.Examples.Count >= 41);
        foreach (var (name, examples) in CliApp.Examples)
        {
            Assert.InRange(examples.Length, 1, 2);
            var (exit, output) = Help([.. name.Split(' '), "--help"]);
            Assert.True(exit == 0, name + output);
            Assert.Contains("EXAMPLES:", output);
            Assert.Contains($"{CliName.Wrapper} " + string.Join(" ", examples[0].Take(2)), output);
            Assert.True(output.IndexOf("EXAMPLES:", StringComparison.Ordinal) < output.IndexOf("OPTIONS:", StringComparison.Ordinal), name);
            // An example uses only options the command has.
            Assert.All(examples.SelectMany(e => e).Where(a => a.StartsWith("--")), option => Assert.Contains(option + " ", output.Replace('\n', ' ') + " "));
        }
        // Every command the CLI has is in that list (a new command without an example fails here).
        var (_, top) = Help("--help");
        Assert.Contains("EXAMPLES:", top);
        foreach (var branch in new[] { "books", "index", "gold", "runs", "query", "chat", "setup" })
        {
            var (_, help) = Help(branch, "--help");
            var commands = help[(help.IndexOf("COMMANDS:", StringComparison.Ordinal) is var at and >= 0 ? at : help.Length)..]
                .Split('\n').Skip(1).Select(l => Regex.Match(l, "^ {4}([a-z][a-z-]*)( |$)")).Where(m => m.Success).Select(m => m.Groups[1].Value).ToList();   // a wrapped description is indented further
            Assert.NotEmpty(commands);
            Assert.All(commands, c => Assert.Contains($"{branch} {c}", CliApp.Examples.Keys));
        }

        // A command's own options come before the ones every command takes.
        var (_, run) = Help("index", "run", "--help");
        Assert.True(run.IndexOf("--book <ID>", StringComparison.Ordinal) < run.IndexOf("--json", StringComparison.Ordinal));
        Assert.True(run.IndexOf("--dry-run", StringComparison.Ordinal) < run.IndexOf("--json", StringComparison.Ordinal));
        foreach (var global in new[] { "--json", "--quiet", "--no-color", "--no-input", "--verbose", "--debug", "--repo <PATH>" }) Assert.Contains(global, run);
        Assert.Equal(["debug", "json", "no-color", "no-input", "quiet", "repo", "verbose"], CommandFirstHelp.Global.Order(StringComparer.Ordinal));

        // -h is help and nothing else: no command gives it another meaning.
        var shortNames = typeof(CliApp).Assembly.GetTypes().SelectMany(t => t.GetProperties())
            .SelectMany(p => p.GetCustomAttributes(typeof(CommandOptionAttribute), false).OfType<CommandOptionAttribute>()).SelectMany(a => a.ShortNames).ToList();
        Assert.DoesNotContain("h", shortNames);
        Assert.Contains("-h, --help", run);
    }

    [Fact(Timeout = TestTimeout)]
    public async Task Help_is_also_a_command_and_bad_input_gets_an_example_not_the_whole_help()
    {
        Assert.Equal(["index", "run", "--help"], CliApp.HelpCommand(["help", "index", "run"]));
        Assert.Equal(["--help"], CliApp.HelpCommand(["help"]));
        Assert.Equal(["books", "list"], CliApp.HelpCommand(["books", "list"]));

        var help = Wide();
        var (exit, _) = await RunAtTerminal(help, ["help", "index", "watch"], TestContext.Current.CancellationToken, terminal: false);
        Assert.Equal(Exit.Ok, exit);
        Assert.Contains($"{CliName.Wrapper} index watch [OPTIONS]", help.Output);
        Assert.Contains($"{CliName.Wrapper} index watch --run <runId>", help.Output);
        var same = Wide();
        await RunAtTerminal(same, ["index", "watch", "--help"], TestContext.Current.CancellationToken, terminal: false);
        Assert.Equal(same.Output, help.Output);

        // A missing required argument: the parser's message, one example and where the help is; exit 2.
        var missing = Wide();
        (exit, _) = await RunAtTerminal(missing, ["books", "remove"], TestContext.Current.CancellationToken, terminal: false);
        Assert.Equal(Exit.BadInput, exit);
        Assert.Contains("missing required argument 'id'", missing.Output);
        Assert.Contains($"Usage: {CliName.Wrapper} books remove [OPTIONS]", missing.Output);
        Assert.Contains($"Example: {CliName.Wrapper} books remove treasure-island --yes", missing.Output);
        Assert.Contains($"See: {CliName.Wrapper} books remove --help", missing.Output);
        Assert.DoesNotContain("OPTIONS:", missing.Output);                           // not the help
        // An option without its value, and a value that is not allowed: the same three lines.
        var noValue = Wide();
        (exit, _) = await RunAtTerminal(noValue, ["index", "run", "--book"], TestContext.Current.CancellationToken, terminal: false);
        Assert.Equal(Exit.BadInput, exit);
        Assert.Contains($"See: {CliName.Wrapper} index run --help", noValue.Output);
        var badValue = Wide();
        (exit, _) = await RunAtTerminal(badValue, ["index", "run", "--book", Hound, "--stages", "9"], TestContext.Current.CancellationToken, terminal: false);
        Assert.Equal(Exit.BadInput, exit);
        Assert.Contains("--stages must be within 0-5", badValue.Output);
        Assert.Contains($"Example: {CliName.Wrapper} index run --book hound-of-the-baskervilles --budget-tokens 25000000 --promote", badValue.Output.Replace("\n", " ").Replace("  ", " "));
        Assert.DoesNotContain("OPTIONS:", badValue.Output);
        CliContext.Reset();
    }

    [Fact(Timeout = TestTimeout)]
    public async Task Version_prints_the_informational_version_baked_at_build()
    {
        var version = CliApp.Version();
        Assert.Matches(@"^1\.0\.0(\+\S+)?$", version);   // the version of Directory.Build.props; the SDK adds the commit it built from

        var console = Wide();
        var (exit, _) = await RunAtTerminal(console, ["--version"], TestContext.Current.CancellationToken, terminal: false);

        Assert.Equal(Exit.Ok, exit);
        Assert.Equal(version, console.Output.Trim());
        var withRepo = Wide();
        (exit, _) = await RunAtTerminal(withRepo, ["--version", "--repo", RepoPaths.FindRoot()], TestContext.Current.CancellationToken, terminal: false);   // as the launch script sends it
        Assert.Equal((Exit.Ok, version), (exit, withRepo.Output.Trim()));
        // It is set in Directory.Build.props; no git, at build or at run time.
        var props = File.ReadAllText(Path.Combine(RepoPaths.FindRoot(), "Directory.Build.props"));
        Assert.Contains("<Version>1.0.0</Version>", props);
        Assert.DoesNotContain("Exec", props);
        CliContext.Reset();
    }

    [Theory(Timeout = TestTimeout)]
    [InlineData("NO_COLOR")]
    [InlineData("--no-color")]
    public async Task No_color_from_the_environment_or_the_option_turns_colour_off(string how)
    {
        static TestConsole Coloured()
        {
            var console = new TestConsole().Colors(ColorSystem.TrueColor).EmitAnsiSequences();
            console.Profile.Width = 240;
            return console;
        }
        var colour = new Regex("\u001b\\[[0-9;]*(3[0-9]|9[0-7]|38;[25])[0-9;]*m");
        var was = Environment.GetEnvironmentVariable("NO_COLOR");
        try
        {
            Environment.SetEnvironmentVariable("NO_COLOR", null);
            var with = Coloured();
            var (exit, _) = await RunAtTerminal(with, ["books", "list"], TestContext.Current.CancellationToken, terminal: false);
            Assert.Equal(Exit.Ok, exit);
            Assert.Matches(colour, with.Output);   // "yes" in green for an indexed book

            if (how == "NO_COLOR") Environment.SetEnvironmentVariable("NO_COLOR", "1");
            var without = Coloured();
            (exit, _) = await RunAtTerminal(without, how == "NO_COLOR" ? ["books", "list"] : ["books", "list", "--no-color"], TestContext.Current.CancellationToken, terminal: false);
            Assert.Equal(Exit.Ok, exit);
            Assert.Contains("hound-of-the-baskervilles", without.Output);
            Assert.DoesNotMatch(colour, without.Output);
            Assert.Equal(ColorSystem.TrueColor, without.Profile.Capabilities.ColorSystem);   // for that command only
        }
        finally { Environment.SetEnvironmentVariable("NO_COLOR", was); }
        CliContext.Reset();
    }

    // ── Timeouts and idempotence ───────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Network_timeouts_are_configurable_and_have_defaults()
    {
        IConfiguration Config(params (string, string)[] values) => new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => KeyValuePair.Create(v.Item1, (string?)v.Item2))).Build();

        Assert.Null(LlmSettings.From(Config(("Llm:Type", "OpenAI"))).TimeoutSeconds);                       // each SDK's own default
        Assert.Equal((100, 600), (LlmSettings.DefaultOpenAITimeoutSeconds, LlmSettings.DefaultAnthropicTimeoutSeconds));
        Assert.Equal(45, LlmSettings.From(Config(("Llm:TimeoutSeconds", "45"))).TimeoutSeconds);
        var both = Config(("Llm:TimeoutSeconds", "45"), ("Llm:Profiles:slow:TimeoutSeconds", "900"), ("Llm:Profiles:other:Model", "m"));
        Assert.Equal(900, LlmSettings.From(both, "slow").TimeoutSeconds);                                     // the profile's own
        Assert.Equal(45, LlmSettings.From(both, "other").TimeoutSeconds);                                     // else Llm:TimeoutSeconds
        Assert.Null(LlmSettings.From(Config(("Llm:TimeoutSeconds", "soon"))).TimeoutSeconds);
        // The clients are built with it, for both SDKs.
        foreach (var type in new[] { "OpenAI", "Anthropic" })
            using (ChatClientFactory.Create(new LlmSettings(type, "m", null, "fake-key-for-tests-0123456789") { TimeoutSeconds = 30 })) { }

        using (var client = BookIntake.CreateClient()) Assert.Equal(TimeSpan.FromSeconds(60), client.Timeout);
        Assert.Equal(60, BookIntake.DefaultDownloadTimeoutSeconds);
        using (var client = BookIntake.CreateClient(Config(("Books:DownloadTimeoutSeconds", "15")))) Assert.Equal(TimeSpan.FromSeconds(15), client.Timeout);
        using (var client = BookIntake.CreateClient(Config(("Books:DownloadTimeoutSeconds", "")))) Assert.Equal(TimeSpan.FromSeconds(60), client.Timeout);
        // The template names both, so a person finds them.
        var template = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "templates", "cli.appsettings.json"));
        Assert.Contains("\"TimeoutSeconds\"", template);
        Assert.Contains("\"DownloadTimeoutSeconds\"", template);
    }

    /// <summary>"finish the in-flight model call": a call that was sent ends by itself; the next one is not sent.</summary>
    [Fact(Timeout = TestTimeout)]
    public async Task A_stop_lets_the_call_that_was_sent_finish_and_sends_no_other()
    {
        using var server = new FakeLlmServer { DelayMs = 300 };
        using var root = new TempRoot();
        var run = RunFolder.Create(RepoPaths.Runs(root.Path));
        var llm = new LlmSettings("OpenAI", "my-deployment", server.BaseUrl + "/openai/v1/", "fake-key-for-tests-0123456789");
        using var client = ChatClientFactory.Create(llm, maxRetries: 0, new LlmPipelineOptions(CallContext: new LlmCallContext { Run = run, Stage = "stage-1" }, Retries: 2, FinishInFlight: true));
        using var stop = new CancellationTokenSource();
        var message = new[] { new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "hello") };

        var call = client.GetResponseAsync(message, cancellationToken: stop.Token);
        while (server.Requests.Count == 0) await Task.Delay(10, TestContext.Current.CancellationToken);
        stop.Cancel();                                         // while the call is with the model
        var reply = await call.WaitAsync(Soon, TestContext.Current.CancellationToken);   // it finishes
        Assert.Equal("ready", reply.Text);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetResponseAsync(message, cancellationToken: stop.Token));

        Assert.Single(server.Requests);                        // the second call never left
        var logged = File.ReadAllLines(run.File(RunFolder.CallsJsonl));
        Assert.Single(logged);                                 // and left no line
        Assert.Null(JsonNode.Parse(logged[0])!["error"]);
    }
}
