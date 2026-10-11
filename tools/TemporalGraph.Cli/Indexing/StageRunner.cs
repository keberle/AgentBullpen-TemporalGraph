using TemporalGraph.Llm;

namespace TemporalGraph.Cli.Indexing;

/// <summary>
/// The one gate every model call of a stage goes through. At most <see cref="Limit"/> calls are in flight; the first
/// call is sent alone and the others wait for it to end, because a prompt-cache entry exists only once the first response has
/// begun. The number in flight and its peak are kept for the progress footer and the stage's record.
/// </summary>
public sealed class StageRunner(int parallel, bool firstAlone = true)
{
    private readonly SemaphoreSlim _gate = new(Math.Max(1, parallel));
    private readonly Lock _lock = new();
    private Task? _first;
    private int _inFlight, _peak, _calls;

    /// <summary><c>Indexing:Parallel</c> when the key is absent.</summary>
    public const int DefaultParallel = 8;

    public int Limit { get; } = Math.Max(1, parallel);
    public int InFlight => Volatile.Read(ref _inFlight);
    public int Peak => Volatile.Read(ref _peak);
    public int Calls => Volatile.Read(ref _calls);

    /// <summary>The runner of the stage that is running now, for the progress footer; null between stages.</summary>
    public static StageRunner? Current { get; private set; }

    /// <summary>Makes this runner the current one until the returned scope ends.</summary>
    public IDisposable Begin()
    {
        Current = this;
        return new End(this);
    }

    private sealed class End(StageRunner runner) : IDisposable
    {
        public void Dispose()
        {
            if (ReferenceEquals(Current, runner)) Current = null;
        }
    }

    /// <summary>Runs one model call through the gate, with its place (chapter, chunk) and its cache breakpoints.</summary>
    public async Task<T> CallAsync<T>(Func<Task<T>> call, CancellationToken ct, int? chapter = null, int? chunk = null, CacheHints? cache = null)
    {
        Task? wait = null;
        TaskCompletionSource? mine = null;
        if (firstAlone)
            lock (_lock)
            {
                if (_first is null) _first = (mine = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
                else wait = _first;
            }
        if (wait is not null) await wait.WaitAsync(ct);
        try
        {
            await _gate.WaitAsync(ct);
            try
            {
                var now = Interlocked.Increment(ref _inFlight);
                Interlocked.Increment(ref _calls);
                int peak;
                while (now > (peak = Volatile.Read(ref _peak)) && Interlocked.CompareExchange(ref _peak, now, peak) != peak) { }
                using (LlmCallContext.At(chapter, chunk))
                using (PromptCache.Use(cache))
                    return await call();
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
                _gate.Release();
            }
        }
        finally { mine?.TrySetResult(); }
    }

    /// <summary>Runs <paramref name="body"/> for every item at once (the gate bounds the model calls inside); the results come back in
    /// the items' order, whatever order they finished in.</summary>
    public static async Task<List<TResult>> ForEachAsync<TItem, TResult>(IReadOnlyList<TItem> items, Func<TItem, int, Task<TResult>> body) =>
        (await Task.WhenAll(items.Select((item, i) => body(item, i)))).ToList();
}
