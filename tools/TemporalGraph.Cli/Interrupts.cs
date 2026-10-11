using System.Runtime.InteropServices;
using Spectre.Console;

namespace TemporalGraph.Cli;

/// <summary>
/// Ctrl+C for the whole CLI. The first press cancels the token every command runs under (and says so on stderr, unless a
/// prompt is on screen: the prompt's "Cancelled." is the one line then); a second press quits at once with exit 130. A closed
/// terminal or SIGTERM counts as the first press. While a <see cref="Call"/> scope is open (the chat console's model call), the press
/// cancels that call only, so the console can show its prompt again.
/// </summary>
public static class Interrupts
{
    public const string StoppingNotice = "Stopping… (Ctrl+C again to quit at once)";

    private static readonly Lock Gate = new();
    private static readonly List<CallScope> Scopes = [];
    private static CancellationTokenSource? _process;
    private static ManualResetEventSlim? _done;
    private static int _presses;
    private static int _prompts;

    /// <summary>Tests: what a second press does in place of <see cref="Environment.Exit"/>.</summary>
    internal static Action<int> ForceQuit { get; set; } = Environment.Exit;

    /// <summary>How long a closing terminal waits for the command to stop cleanly (the OS ends the process soon after).</summary>
    private static readonly TimeSpan CloseWait = TimeSpan.FromSeconds(4);

    /// <summary>Ctrl+C (or a closed terminal) was seen since <see cref="Listen"/>.</summary>
    public static bool Pressed => Volatile.Read(ref _presses) > 0;

    /// <summary>
    /// Subscribes to Ctrl+C, SIGTERM, a closed terminal and the process exit for as long as the result is not disposed;
    /// <paramref name="process"/> is cancelled by the first of them.
    /// </summary>
    public static IDisposable Listen(CancellationTokenSource process)
    {
        var done = new ManualResetEventSlim(false);
        lock (Gate)
        {
            _process = process;
            _done = done;
            _presses = 0;
            _prompts = 0;
            Scopes.Clear();
        }

        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            e.Cancel = true;   // we own the exit
            Press();
        };
        void OnSignal(PosixSignalContext context)
        {
            context.Cancel = true;
            Terminate();
            // Windows ends the process when the handler of a closed terminal returns: give the command a moment to write its files.
            done.Wait(CloseWait);
        }
        EventHandler onExit = (_, _) =>
        {
            if (done.IsSet) return;
            Terminate();
            done.Wait(CloseWait);
        };

        Console.CancelKeyPress += onCancel;
        AppDomain.CurrentDomain.ProcessExit += onExit;
        var signals = new List<PosixSignalRegistration>();
        foreach (var signal in new[] { PosixSignal.SIGTERM, PosixSignal.SIGHUP })
        {
            try { signals.Add(PosixSignalRegistration.Create(signal, OnSignal)); }
            catch (Exception ex) when (ex is PlatformNotSupportedException or IOException or ArgumentException) { }
        }

        return new Listener(() =>
        {
            done.Set();
            Console.CancelKeyPress -= onCancel;
            AppDomain.CurrentDomain.ProcessExit -= onExit;
            foreach (var s in signals) s.Dispose();
            lock (Gate)
            {
                if (ReferenceEquals(_process, process)) (_process, _done) = (null, null);
                Scopes.Clear();
            }
        });
    }

    /// <summary>What Ctrl+C does.</summary>
    public static void Press()
    {
        CallScope? scope;
        CancellationTokenSource? process;
        int press;
        bool prompting;
        lock (Gate)
        {
            // An open call takes the first press; a second one while it is still stopping goes to the process.
            scope = Scopes.LastOrDefault(s => !s.Interrupted);
            if (scope is not null) scope.Interrupted = true;
            process = _process;
            press = scope is null ? ++_presses : 0;
            prompting = _prompts > 0;
        }
        if (scope is not null)
        {
            scope.Cancel();
            return;
        }
        if (press == 1)
        {
            if (!prompting) CliContext.Stderr.WriteLine(StoppingNotice);
            Cancel(process);
        }
        else
        {
            _done?.Set();   // the exit handler has nothing to wait for
            ForceQuit(Exit.Cancelled);
        }
    }

    /// <summary>SIGTERM, a closed terminal or the process exit: as the first Ctrl+C, and never the force quit.</summary>
    public static void Terminate()
    {
        CancellationTokenSource? process;
        lock (Gate)
        {
            if (_presses == 0) _presses = 1;
            process = _process;
        }
        Cancel(process);
    }

    /// <summary>The token is cancelled at once; what waits on it goes on from the thread pool, not on the thread that delivered the
    /// key (the console's handler thread returns straight away).</summary>
    private static void Cancel(CancellationTokenSource? source)
    {
        try { _ = source?.CancelAsync(); }
        catch (ObjectDisposedException) { }
    }

    /// <summary>While open, a prompt is on screen: the first press prints nothing itself.</summary>
    public static IDisposable Prompting()
    {
        lock (Gate) _prompts++;
        return new Listener(() => { lock (Gate) _prompts = Math.Max(0, _prompts - 1); });
    }

    /// <summary>
    /// A call that one Ctrl+C cancels on its own (the chat console's model call): the press cancels <see cref="CallScope.Token"/> and
    /// not the process, and <see cref="CallScope.Interrupted"/> says it happened.
    /// </summary>
    public static CallScope Call(CancellationToken outer)
    {
        var scope = new CallScope(CancellationTokenSource.CreateLinkedTokenSource(outer));
        lock (Gate) Scopes.Add(scope);
        return scope;
    }

    public sealed class CallScope(CancellationTokenSource source) : IDisposable
    {
        public CancellationToken Token => source.Token;

        /// <summary>Ctrl+C was pressed during this call.</summary>
        public bool Interrupted { get; internal set; }

        internal void Cancel() => Interrupts.Cancel(source);

        public void Dispose()
        {
            lock (Gate) Scopes.Remove(this);
            source.Dispose();
        }
    }

    private sealed class Listener(Action stop) : IDisposable
    {
        private int _stopped;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _stopped, 1) == 0) stop();
        }
    }
}

/// <summary>
/// The one way the CLI asks a person something. Spectre's synchronous <c>Prompt</c> / <c>Ask</c> / <c>Confirm</c> never see a
/// cancellation token; <c>ShowAsync</c> does, so Ctrl+C leaves any prompt with an <see cref="OperationCanceledException"/> (exit 130,
/// "Cancelled.") and the cursor shown. A test fails the build of this rule if a synchronous call comes back.
/// </summary>
public static class Prompts
{
    public const string Back = "← Back";

    /// <summary>What the title of every list ends with: its cancel result is its way back.</summary>
    public const string EscBack = "[grey](Esc: back)[/]";

    /// <summary>The same for the two top menus, where the way back is the way out.</summary>
    public const string EscExit = "[grey](Esc: exit)[/]";

    public static Task<T> AskAsync<T>(IPrompt<T> prompt, CancellationToken ct) => AskAsync(AnsiConsole.Console, prompt, ct);

    public static async Task<T> AskAsync<T>(this IAnsiConsole console, IPrompt<T> prompt, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var prompting = Interrupts.Prompting();
        try
        {
            var answer = await prompt.ShowAsync(console, ct);
            ct.ThrowIfCancellationRequested();
            return answer;
        }
        catch (OperationCanceledException)
        {
            console.WriteLine();   // the next line ("Cancelled.") on a line of its own
            throw;
        }
        finally
        {
            // A list hides the cursor while it is shown and puts it back when it ends by itself; this puts it back on every other way out.
            console.Cursor.Show();
        }
    }

    public static Task<bool> ConfirmAsync(this IAnsiConsole console, string question, bool defaultValue, CancellationToken ct) =>
        console.AskAsync(new ConfirmationPrompt(question) { DefaultValue = defaultValue }, ct);
}
