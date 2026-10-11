namespace TemporalGraph.Cli.Runs;

/// <summary>
/// Turns what landed in calls.jsonl and failures.jsonl into the lines of run.log: a line per chunk done (the calls moved on
/// to the next chunk), per retry and per warning, and with <c>--debug</c> each model call's size and timing (never its content).
/// </summary>
/// <param name="unit">The words for a chunk (or, for Stage 5, a chapter) of a stage: "chunk 6/12" when the plan is known.</param>
public sealed class CallLogLines(RunLog log, Func<string, int, string>? unit = null)
{
    private readonly Dictionary<string, int> _unit = [];

    private string Unit(string stage, int index) => unit?.Invoke(stage, index) ?? (stage == "stage-5" ? $"chapter {index}" : $"chunk {index}");

    public void Add(IReadOnlyList<RunEvent> calls, IReadOnlyList<RunEvent> warnings)
    {
        foreach (var e in calls)
        {
            var stage = e.Stage ?? "";
            if (e.Type == "retry")
            {
                log.Write($"{stage}: retry {e.Attempt} after {e.Reason ?? "an error"}, waiting {(e.DelayMs ?? 0) / 1000:0.#} s");
                continue;
            }
            if (e.Type != "call") continue;
            // A chunk is done when the calls move on to the next one (its samples are sent together).
            if ((e.ChunkIndex ?? e.Chapter) is { } now)
            {
                if (_unit.TryGetValue(stage, out var before) && before != now) log.Write($"{stage}: {Unit(stage, before)} done");
                _unit[stage] = now;
            }
            log.Debug($"{stage}: call{(e.Chapter is { } ch ? $" ch {ch}" : "")}{(e.ChunkIndex is { } ck ? $" chunk {ck}" : "")} · request {e.RequestBytes:N0} bytes, reply {e.ResponseBytes:N0} bytes"
                + $" · {e.InputTokens:N0} tokens in, {e.OutputTokens:N0} out · {e.LatencyMs ?? 0:0} ms{(e.Failed ? " · failed" : "")}");
        }
        foreach (var w in warnings)
            log.Write($"warning {w.Stage}: {w.Type}{(w.Chapter is { } ch ? $" (chapter {ch}{(w.ChunkIndex is { } ck ? $", chunk {ck}" : "")})" : "")}{(w.Reason is { Length: > 0 } why ? $": {why}" : "")}");
    }

    /// <summary>The stage ended: its last chunk is done too.</summary>
    public void StageEnded(string stage)
    {
        if (_unit.Remove(stage, out var last)) log.Write($"{stage}: {Unit(stage, last)} done");
    }
}

/// <summary>
/// Run.log for a stage command run on its own (<c>index mentions</c>, <c>resolve</c>, <c>facts</c>, <c>time</c>,
/// <c>perceive</c>, <c>voice</c>): the stage's start and end and, in between, the lines of <see cref="CallLogLines"/>, read from the
/// run folder once a second. Under <c>index run</c> the run's own watcher writes them, so this stays out of the way.
/// </summary>
public static class StageLogs
{
    private static readonly Lock Gate = new();
    private static readonly Dictionary<string, Tail> Open = [];

    /// <summary><c>index run</c> is writing this run's log itself.</summary>
    private static bool UnderIndexRun(RunFolder run) =>
        RunLog.Current?.Path is { } path && string.Equals(Path.GetFullPath(path), Path.GetFullPath(run.File(RunLog.FileName)), StringComparison.OrdinalIgnoreCase);

    /// <summary>A line in the run's log, from a command that is not a stage with model calls (<c>index build-db</c>, <c>index prepare</c>, a promotion).</summary>
    public static void Line(RunFolder run, string line)
    {
        if (UnderIndexRun(run)) RunLog.Current!.Write(line);
        else RunLog.For(run).Write(line);
    }

    public static void Begin(RunFolder run, string stage)
    {
        if (UnderIndexRun(run)) return;
        var tail = new Tail(run, stage);
        lock (Gate)
        {
            if (Open.Remove(Key(run, stage), out var old)) old.Dispose();
            Open[Key(run, stage)] = tail;
        }
    }

    public static void End(RunFolder run, string stage, long calls, long inputTokens, long outputTokens)
    {
        Tail? tail;
        lock (Gate) Open.Remove(Key(run, stage), out tail);
        if (tail is null) return;
        tail.Dispose();
        tail.Log.Write($"{stage}: done · {calls} calls · {inputTokens}/{outputTokens} tokens in/out");
        if (ReferenceEquals(RunLog.Current, tail.Log)) RunLog.Current = null;
    }

    private static string Key(RunFolder run, string stage) => run.Path + "|" + stage;

    private sealed class Tail : IDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly RunMonitor _monitor;
        private readonly CallLogLines _lines;
        private readonly string _stage;
        private readonly Task _loop;
        private readonly Lock _tick = new();

        public RunLog Log { get; }

        public Tail(RunFolder run, string stage)
        {
            _stage = stage;
            Log = RunLog.For(run);
            RunLog.Current ??= Log;   // an unexpected error names this log
            _monitor = new RunMonitor(run.Path);
            _monitor.Poll();          // only what this stage adds
            _lines = new CallLogLines(Log);
            Log.Write($"{stage}: start");
            _loop = Task.Run(async () =>
            {
                while (!_stop.IsCancellationRequested)
                {
                    Tick();
                    try { await Task.Delay(TimeSpan.FromSeconds(1), _stop.Token); }
                    catch (OperationCanceledException) { }
                }
            });
        }

        private void Tick()
        {
            lock (_tick)
            {
                try
                {
                    var (calls, warnings) = _monitor.Poll();
                    _lines.Add(calls, warnings);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            try { _loop.Wait(TimeSpan.FromSeconds(5)); }
            catch (AggregateException) { }
            Tick();
            lock (_tick) _lines.StageEnded(_stage);
            _stop.Dispose();
        }
    }
}
