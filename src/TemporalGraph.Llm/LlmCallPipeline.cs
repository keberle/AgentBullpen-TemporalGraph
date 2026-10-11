using System.ClientModel;
using System.Diagnostics;
using Anthropic.Exceptions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;

namespace TemporalGraph.Llm;

/// <summary>Who is calling: written into the call log (the CLI's calls.jsonl) and onto the call's span.</summary>
public sealed class LlmCallContext
{
    /// <summary>Where calls are logged: the CLI's run folder, or <see cref="NoCallLog"/>.</summary>
    public required ILlmCallLog Run { get; init; }
    public required string Stage { get; init; }
    public string? BookId { get; set; }
    private int? _chapter, _chunkIndex;
    private static readonly AsyncLocal<(int? Chapter, int? Chunk)?> Place = new();

    /// <summary>The chapter of the call. Stages run chunks in parallel, so a call inside <see cref="At"/> reports its own.</summary>
    public int? Chapter { get => Place.Value is { } p ? p.Chapter : _chapter; set => _chapter = value; }
    public int? ChunkIndex { get => Place.Value is { } p ? p.Chunk : _chunkIndex; set => _chunkIndex = value; }

    /// <summary>The calls made inside the returned scope (in this async flow) are logged with this chapter and chunk, whatever the
    /// shared properties say.</summary>
    public static IDisposable At(int? chapter, int? chunk)
    {
        var before = Place.Value;
        Place.Value = (chapter, chunk);
        return new Leave(() => Place.Value = before);
    }

    private sealed class Leave(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }
    public string? SkillName { get; set; }
    public string? SkillVersion { get; set; }

    /// <summary>Gateway mode: the last <c>X-Model-Fallback</c> the site's proxy sent (<c>&lt;requested&gt; -&gt; &lt;used&gt;</c>); the chat clears it before each turn.</summary>
    public string? ModelFallback { get; set; }

    private readonly Lock _costLock = new();
    private decimal _costUsd;
    private int _answered, _priced;

    /// <summary>
    /// One answered model call and what it cost in US dollars, when that is known (gateway: the site's <c>X-Call-Cost-Usd</c>;
    /// standalone: the provider's own <c>usage.cost</c>). The chat clears the sum before each turn (<see cref="ResetCost"/>).
    /// </summary>
    public void AddCallCost(decimal? usd)
    {
        lock (_costLock)
        {
            _answered++;
            if (usd is not { } cost) return;
            _priced++;
            _costUsd += cost;
        }
    }

    public void ResetCost()
    {
        lock (_costLock) (_costUsd, _answered, _priced) = (0, 0, 0);
    }

    /// <summary>
    /// The cost of the calls since <see cref="ResetCost"/>, when every one of them came with a cost; else null. Never an estimate, and
    /// never a part of the total: a turn with one unpriced call has no cost to show.
    /// </summary>
    public decimal? CostUsd
    {
        get
        {
            lock (_costLock) return _answered > 0 && _priced == _answered ? _costUsd : null;
        }
    }

    internal void Tag(Activity? span)
    {
        if (span is null) return;
        if (Run.RunId is not null) span.SetTag(LlmTelemetry.RunId, Run.RunId);
        span.SetTag(LlmTelemetry.Stage, Stage);
        if (BookId is not null) span.SetTag(LlmTelemetry.BookId, BookId);
        if (Chapter is not null) span.SetTag(LlmTelemetry.Chapter, Chapter);
        if (ChunkIndex is not null) span.SetTag(LlmTelemetry.ChunkIndex, ChunkIndex);
        if (SkillName is not null) span.SetTag(LlmTelemetry.SkillName, SkillName);
        if (SkillVersion is not null) span.SetTag(LlmTelemetry.SkillVersion, SkillVersion);
    }
}

/// <summary>
/// Retry with exponential backoff on transient errors: HTTP 429 (rate limited) and 500/502/503/504 (server errors),
/// from either SDK. A longer retry-after from the server is honoured. Each retry is a span event plus a line in
/// calls.jsonl, with the status as the reason.
/// </summary>
public sealed class RetryOnTransientChatClient(IChatClient inner, LlmCallContext? context, int maxRetries = 3, Func<TimeSpan, CancellationToken, Task>? delay = null)
    : DelegatingChatClient(inner)
{
    /// <summary>The HTTP statuses that are retried.</summary>
    public static readonly IReadOnlySet<int> RetriedStatuses = new HashSet<int> { 429, 500, 502, 503, 504, 529 };   // 529: Anthropic "overloaded"

    /// <summary>Wait before retry n (1-based): 1 s, 2 s, 4 s, …</summary>
    public static TimeSpan Backoff(int retry) => TimeSpan.FromSeconds(Math.Pow(2, retry - 1));

    /// <summary>The longest wait a server's retry-after header can ask for.</summary>
    public static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The wait the server asked for, if the error response has a <c>retry-after-ms</c> (milliseconds) or
    /// <c>retry-after</c> (seconds) header. OpenAI SDK errors only; null otherwise.
    /// </summary>
    public static TimeSpan? RetryAfter(Exception ex)
    {
        // The Anthropic SDK's errors don't carry the headers; the HTTP handler notes a failed response's retry-after on the call.
        if (ex is not ClientResultException && PromptCache.Observation?.RetryAfter is { } noted) return noted;
        if (ex is not ClientResultException cre || cre.GetRawResponse() is not { } response) return null;
        static double? Number(string? v) => double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) && d >= 0 ? d : null;
        if (response.Headers.TryGetValue("retry-after-ms", out var ms) && Number(ms) is { } m) return TimeSpan.FromMilliseconds(m);
        if (response.Headers.TryGetValue("retry-after", out var sec) && Number(sec) is { } s) return TimeSpan.FromSeconds(s);
        return null;
    }

    /// <summary>The wait before retry n: the backoff, or the server's retry-after when longer (capped at 60 s).</summary>
    public static TimeSpan Wait(int retry, Exception ex) =>
        RetryAfter(ex) is { } asked && asked > Backoff(retry) ? (asked < MaxRetryAfter ? asked : MaxRetryAfter) : Backoff(retry);

    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;

    /// <summary>The HTTP status of a transient error, or null when <paramref name="ex"/> isn't one.</summary>
    public static int? TransientStatus(Exception ex) => ex switch
    {
        ClientResultException { Status: var s } when RetriedStatuses.Contains(s) => s,
        AnthropicRateLimitException => 429,
        Anthropic5xxException a when RetriedStatuses.Contains((int)a.StatusCode) => (int)a.StatusCode,
        _ => null,
    };

    /// <summary>
    /// A network timeout: the SDK cancels a request that runs past its network timeout with a
    /// <see cref="TaskCanceledException"/>, while the caller's token is not cancelled. Retried like a transient status.
    /// </summary>
    public static bool IsNetworkTimeout(Exception ex, CancellationToken callerToken) =>
        ex is OperationCanceledException && !callerToken.IsCancellationRequested;

    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var list = messages as IList<ChatMessage> ?? messages.ToList();
        var seen = PromptCache.Observe();   // set here so it flows down to the HTTP handler and is still readable after a failed attempt
        for (var retry = 1; ; retry++)
        {
            try
            {
                seen.Reset();
                return await base.GetResponseAsync(list, options, cancellationToken);
            }
            catch (Exception ex) when ((TransientStatus(ex) is not null || IsNetworkTimeout(ex, cancellationToken)) && retry <= maxRetries
                && ProviderFailures.Classify(ex) is null)   // An account out of credits or quota won't recover in seconds
            {
                var wait = Wait(retry, ex);
                var reason = TransientStatus(ex) is { } status ? status.ToString(System.Globalization.CultureInfo.InvariantCulture) : "timeout";
                Activity.Current?.AddEvent(new ActivityEvent("retry", tags: new ActivityTagsCollection
                {
                    ["retry.attempt"] = retry,
                    ["retry.delay_ms"] = wait.TotalMilliseconds,
                    ["retry.reason"] = reason,
                }));
                context?.Run.AppendCall(new
                {
                    type = "retry",
                    timestamp = DateTimeOffset.UtcNow,
                    runId = context.Run.RunId,
                    attempt = retry,
                    delayMs = wait.TotalMilliseconds,
                    retryAfterMs = RetryAfter(ex)?.TotalMilliseconds,
                    reason,
                    error = ex.GetType().Name,
                    traceId = Activity.Current?.TraceId.ToString(),
                });
                await _delay(wait, cancellationToken);
            }
        }
    }
}

/// <summary>
/// The sampling settings of the profile's section (<c>Llm:Temperature</c>, <c>Llm:Seed</c>, <c>Llm:ReasoningEffort</c>, or the same
/// keys under <c>Llm:Profiles:&lt;name&gt;</c>). Empty → not sent. <see cref="SeedSent"/> is false when the provider has no seed
/// parameter (Anthropic): the seed is configured but not sent. <see cref="ReasoningEffort"/> is the OpenAI
/// <c>reasoning_effort</c> value as written (e.g. <c>none</c>, <c>minimal</c>, <c>low</c>); OpenAI only.
/// <see cref="MaxOutputTokens"/> (<c>MaxOutputTokens</c>): the output cap sent with every call; empty → the provider
/// default (the Anthropic SDK's chat client uses its own small default, which a thinking model can use up before writing).
/// </summary>
public sealed record LlmSampling(double? Temperature, long? Seed, bool SeedSent, string? ReasoningEffort = null, int? MaxOutputTokens = null)
{
    public static LlmSampling From(IConfiguration config, LlmSettings llm)
    {
        var s = config.GetSection(llm.SectionPath);
        var temperature = double.TryParse(s["Temperature"], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var t) ? t : (double?)null;
        var seed = long.TryParse(s["Seed"], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : (long?)null;
        var effort = s["ReasoningEffort"]?.Trim() is { Length: > 0 } e && !e.StartsWith('<') && llm.IsOpenAI ? e : null;
        var maxOutput = int.TryParse(s["MaxOutputTokens"], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var m) && m > 0 ? m : (int?)null;
        return new LlmSampling(temperature, seed, seed is not null && !llm.IsAnthropic, effort, maxOutput);
    }

    /// <summary>For run.json: what is sent with every call. top_p is never sent (provider default).</summary>
    public object Record() => new { temperature = Temperature, seed = Seed, seedSent = SeedSent, topP = (double?)null, reasoningEffort = ReasoningEffort, maxOutputTokens = MaxOutputTokens };
}

/// <summary>
/// Puts the configured temperature and seed on every call's <see cref="ChatOptions"/> (unless the caller set them).
/// Outermost in the pipeline, so the OpenTelemetry chat client records them (gen_ai.request.temperature / .seed).
/// </summary>
public sealed class SamplingChatClient(IChatClient inner, LlmSampling sampling) : DelegatingChatClient(inner)
{
    public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        base.GetResponseAsync(messages, Apply(options), cancellationToken);

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        base.GetStreamingResponseAsync(messages, Apply(options), cancellationToken);

    private ChatOptions Apply(ChatOptions? options)
    {
        var o = options?.Clone() ?? new ChatOptions();
        o.Temperature ??= (float?)sampling.Temperature;
        if (sampling.SeedSent) o.Seed ??= sampling.Seed;
        o.MaxOutputTokens ??= sampling.MaxOutputTokens;
        if (sampling.ReasoningEffort is { } effort)
        {
            // reasoning_effort: MEAI's ReasoningOptions.Effort has no "minimal", so the OpenAI SDK's own options carry the
            // exact string (ChatCompletionOptions.ReasoningEffortLevel); Microsoft.Extensions.AI.OpenAI fills in the rest from ChatOptions.
            var inner = o.RawRepresentationFactory;
            o.RawRepresentationFactory = client =>
            {
                var raw = inner?.Invoke(client) as OpenAI.Chat.ChatCompletionOptions ?? new OpenAI.Chat.ChatCompletionOptions();
#pragma warning disable OPENAI001 // ReasoningEffortLevel is marked for evaluation in OpenAI 2.14.0; it is the field that sends reasoning_effort.
                raw.ReasoningEffortLevel = new OpenAI.Chat.ChatReasoningEffortLevel(effort);
#pragma warning restore OPENAI001
                return raw;
            };
        }
        return o;
    }
}

/// <summary>
/// Appends every LLM call (attempt) to calls.jsonl — request, response, tokens, latency, trace id — and tags
/// the current span (the chat client's GenAI span) with the run tags. Keeps token totals for cost.json.
/// </summary>
public sealed class CallLogChatClient(IChatClient inner, LlmCallContext context, string requestedModel) : DelegatingChatClient(inner)
{
    private long _calls, _inputTokens, _outputTokens, _cacheReadTokens, _cacheWriteTokens;

    public long Calls => Interlocked.Read(ref _calls);

    /// <summary>Input tokens billed at the input price: with a prompt cache, the part that was neither read from it nor written to it.</summary>
    public long InputTokens => Interlocked.Read(ref _inputTokens);
    public long OutputTokens => Interlocked.Read(ref _outputTokens);

    /// <summary>Input tokens read from the prompt cache (<c>cache_read_input_tokens</c>).</summary>
    public long CacheReadTokens => Interlocked.Read(ref _cacheReadTokens);

    /// <summary>Input tokens written to the prompt cache (<c>cache_creation_input_tokens</c>).</summary>
    public long CacheWriteTokens => Interlocked.Read(ref _cacheWriteTokens);

    private long _firstInputTokens = -1;

    /// <summary>The input tokens of the first successful call (chat: the call that carries the knowledge pack); null before it.</summary>
    public long? FirstInputTokens => Interlocked.Read(ref _firstInputTokens) is var v && v >= 0 ? v : null;

    /// <summary>
    /// A network timeout: the SDK cancels a request that runs past its network timeout with a
    /// <see cref="TaskCanceledException"/>, while the caller's token is not cancelled. Retried like a transient status.
    /// </summary>
    public static bool IsNetworkTimeout(Exception ex, CancellationToken callerToken) =>
        ex is OperationCanceledException && !callerToken.IsCancellationRequested;

    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var list = messages as IList<ChatMessage> ?? messages.ToList();
        var span = Activity.Current;
        context.Tag(span);
        var started = DateTimeOffset.UtcNow;
        var clock = Stopwatch.StartNew();
        Interlocked.Increment(ref _calls);
        try
        {
            var seen = PromptCache.Observe();
            seen.Reset();
            var response = await base.GetResponseAsync(list, options, cancellationToken);
            // The provider's own counts when the HTTP handler saw them (Anthropic: input_tokens leaves the cached part out).
            var usage = new CallUsage(seen.InputTokens ?? response.Usage?.InputTokenCount ?? 0, seen.OutputTokens ?? response.Usage?.OutputTokenCount ?? 0,
                seen.CacheReadTokens ?? 0, seen.CacheWriteTokens ?? 0);
            Interlocked.Add(ref _inputTokens, usage.Input);
            Interlocked.Add(ref _outputTokens, usage.Output);
            Interlocked.Add(ref _cacheReadTokens, usage.CacheRead);
            Interlocked.Add(ref _cacheWriteTokens, usage.CacheWrite);
            Interlocked.CompareExchange(ref _firstInputTokens, usage.Input + usage.CacheRead + usage.CacheWrite, -1);
            if (SystemFingerprint(response) is { } fingerprint) span?.SetTag(SystemFingerprintTag, fingerprint);
            Log(started, clock.Elapsed, span, list, options, response, error: null, usage);
            return response;
        }
        catch (Exception ex)
        {
            Log(started, clock.Elapsed, span, list, options, response: null, error: ex);
            throw;
        }
    }

    /// <summary>OpenTelemetry GenAI semantic-conventions attribute for the OpenAI system fingerprint.</summary>
    public const string SystemFingerprintTag = "openai.response.system_fingerprint";

    /// <summary>The OpenAI response's <c>system_fingerprint</c>, when the response carries one (Anthropic doesn't).</summary>
    public static string? SystemFingerprint(ChatResponse response) =>
        response.RawRepresentation is OpenAI.Chat.ChatCompletion completion && !string.IsNullOrEmpty(completion.SystemFingerprint) ? completion.SystemFingerprint
        : response.AdditionalProperties?.TryGetValue("SystemFingerprint", out var v) == true && v is string fp && fp.Length > 0 ? fp
        : null;

    /// <summary>One call's tokens: input at the input price, output, and the two prompt-cache counts.</summary>
    public sealed record CallUsage(long Input, long Output, long CacheRead, long CacheWrite);

    private void Log(DateTimeOffset started, TimeSpan latency, Activity? span, IList<ChatMessage> request, ChatOptions? options, ChatResponse? response, Exception? error, CallUsage? counted = null) =>
        context.Run.AppendCall(new
        {
            type = "call",
            timestamp = started,
            runId = context.Run.RunId,
            stage = context.Stage,
            bookId = context.BookId,
            chapter = context.Chapter,
            chunkIndex = context.ChunkIndex,
            skillName = context.SkillName,
            skillVersion = context.SkillVersion,
            model = requestedModel,
            sampling = new { temperature = options?.Temperature, seed = options?.Seed, topP = options?.TopP },
            request = request.Select(m => new { role = m.Role.Value, text = m.Text }),
            response = response is null ? null : new
            {
                modelId = response.ModelId,
                finishReason = response.FinishReason?.Value,
                systemFingerprint = SystemFingerprint(response),
                messages = response.Messages.Select(m => new { role = m.Role.Value, text = m.Text }),
            },
            usage = counted is { } u ? new { input = u.Input, output = u.Output, total = u.Input + u.Output + u.CacheRead + u.CacheWrite, cache_read_input_tokens = u.CacheRead, cache_creation_input_tokens = u.CacheWrite } : null,
            latencyMs = Math.Round(latency.TotalMilliseconds, 1),
            traceId = span?.TraceId.ToString(),
            spanId = span?.SpanId.ToString(),
            error = error is null ? null : new
            {
                type = error.GetType().Name,
                message = error.Message,
                status = error is ClientResultException cre ? cre.Status : (int?)null,
            },
        });
}

/// <summary>The <c>Pricing</c> config section. Values are prices per million tokens; empty means unknown.</summary>
/// <param name="CacheReadPerMillionTokens">The price of input read from the prompt cache; empty → a tenth of the input price.</param>
/// <param name="CacheWritePerMillionTokens">The price of input written to the prompt cache; empty → twice the input price (the
/// one-hour lifetime the indexing stages use).</param>
public sealed record Pricing(string? Currency, decimal? InputPerMillionTokens, decimal? OutputPerMillionTokens,
    decimal? CacheReadPerMillionTokens = null, decimal? CacheWritePerMillionTokens = null)
{
    public const string Section = "Pricing";

    public static Pricing From(IConfiguration config)
    {
        var s = config.GetSection(Section);
        static decimal? Price(string? v) =>
            decimal.TryParse(v, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null;
        return new Pricing(string.IsNullOrWhiteSpace(s["Currency"]) ? null : s["Currency"], Price(s["InputPerMillionTokens"]), Price(s["OutputPerMillionTokens"]),
            Price(s["CacheReadPerMillionTokens"]), Price(s["CacheWritePerMillionTokens"]));
    }

    public bool Known => InputPerMillionTokens is not null && OutputPerMillionTokens is not null;

    /// <summary>The cost of a call or a stage; null when no price is configured.</summary>
    public decimal? Cost(long inputTokens, long outputTokens, long cacheReadTokens = 0, long cacheWriteTokens = 0) => !Known ? null
        : Math.Round((inputTokens * InputPerMillionTokens!.Value + outputTokens * OutputPerMillionTokens!.Value
            + cacheReadTokens * (CacheReadPerMillionTokens ?? InputPerMillionTokens.Value / 10) + cacheWriteTokens * (CacheWritePerMillionTokens ?? InputPerMillionTokens.Value * 2)) / 1_000_000m, 6);

    /// <summary>
    /// Cost.json content with the prompt-cache counts. <c>inputTokens</c> is the input billed at the input price;
    /// <c>cacheReadTokens</c> and <c>cacheWriteTokens</c> are billed at the cache prices (<see cref="Cost"/>).
    /// </summary>
    public object CostReport(long calls, long inputTokens, long outputTokens, long cacheReadTokens, long cacheWriteTokens) => new
    {
        calls,
        inputTokens,
        cacheReadTokens,
        cacheWriteTokens,
        outputTokens,
        totalTokens = inputTokens + cacheReadTokens + cacheWriteTokens + outputTokens,
        cost = Known ? (object)Cost(inputTokens, outputTokens, cacheReadTokens, cacheWriteTokens)!.Value : "unknown",
        currency = Known ? Currency : null,
        pricing = Known ? "Pricing section" : "no price configured (Pricing section is empty)",
    };

    /// <summary>cost.json content: token totals, and the money figure only when both prices are configured.</summary>
    public object CostReport(long calls, long inputTokens, long outputTokens) => new
    {
        calls,
        inputTokens,
        outputTokens,
        totalTokens = inputTokens + outputTokens,
        cost = Known
            ? (object)Math.Round((inputTokens * InputPerMillionTokens!.Value + outputTokens * OutputPerMillionTokens!.Value) / 1_000_000m, 6)
            : "unknown",
        currency = Known ? Currency : null,
        pricing = Known ? "Pricing section" : "no price configured (Pricing section is empty)",
    };
}
