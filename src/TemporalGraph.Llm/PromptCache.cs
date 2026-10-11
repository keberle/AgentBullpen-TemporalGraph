using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TemporalGraph.Llm;

/// <summary>
/// Where a request's prompt-cache breakpoints go. The system block (a stage's skill text, the same for every call of
/// the stage) is cached with <see cref="SystemTtl"/>; <see cref="UserBlock"/> is the index of the content block of the first user
/// message that ends a second cached prefix (Stage A: the chunk text, so a glean call on the same chunk reads it from the cache).
/// </summary>
/// <param name="SystemTtl">"1h" or "5m"; null: no breakpoint on the system block.</param>
/// <param name="UserBlock">The 0-based content block of the first user message to end a 5-minute breakpoint at; null: none.</param>
/// <param name="Thinking">The request's thinking: <c>disabled</c>; an effort (<c>low</c>, <c>medium</c>, <c>high</c>: adaptive
/// thinking with <c>output_config.effort</c>, what Claude Haiku 5.5 takes); or a number (a budget in tokens, for a model that takes
/// one); null: not sent (the model's default). A thinking model bills its thinking as output tokens.</param>
public sealed record CacheHints(string? SystemTtl = "1h", int? UserBlock = null, string? Thinking = null);

/// <summary>
/// What one HTTP exchange with the provider showed, beyond what the SDK hands back: the raw token usage with the cache counts
/// (<c>cache_read_input_tokens</c>, <c>cache_creation_input_tokens</c>) and a failed response's <c>retry-after</c>.
/// </summary>
public sealed class LlmHttpObservation
{
    public long? InputTokens { get; set; }
    public long? OutputTokens { get; set; }
    public long? CacheReadTokens { get; set; }
    public long? CacheWriteTokens { get; set; }
    public TimeSpan? RetryAfter { get; set; }
    public int? Status { get; set; }

    public void Reset() => (InputTokens, OutputTokens, CacheReadTokens, CacheWriteTokens, RetryAfter, Status) = (null, null, null, null, null, null);
}

/// <summary>
/// The ambient state of one model call (it flows with the call's async context, so parallel calls don't see each other's):
/// the cache breakpoints the caller asked for and the observation the HTTP handler fills in.
/// </summary>
public static class PromptCache
{
    private static readonly AsyncLocal<CacheHints?> CurrentHints = new();
    private static readonly AsyncLocal<LlmHttpObservation?> CurrentObservation = new();

    public static CacheHints? Hints => CurrentHints.Value;

    public static LlmHttpObservation? Observation => CurrentObservation.Value;

    /// <summary>The calls made inside the returned scope (in this async flow) carry <paramref name="hints"/>.</summary>
    public static IDisposable Use(CacheHints? hints)
    {
        var before = CurrentHints.Value;
        CurrentHints.Value = hints;
        return new Restore(() => CurrentHints.Value = before);
    }

    /// <summary>The observation of the current call, created when there is none yet.</summary>
    public static LlmHttpObservation Observe() => CurrentObservation.Value ??= new LlmHttpObservation();

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }
}

/// <summary>
/// The Anthropic path. The pipeline builds its requests through Microsoft.Extensions.AI, which has no field for a cache
/// breakpoint, so this handler adds them to the request body on its way out: <c>cache_control</c> on the last system block
/// (<c>{ "type": "ephemeral", "ttl": "1h" }</c>) and on the user content block the caller named (the default 5-minute lifetime). It
/// changes nothing else, and nothing at all when the caller gave no <see cref="CacheHints"/>. On the way back it reads the response's
/// <c>usage</c> (with the cache counts) and a failed response's <c>retry-after</c> into the call's <see cref="LlmHttpObservation"/>.
/// Only <c>POST …/v1/messages</c> is touched. A provider that is not Anthropic never sees this handler.
/// </summary>
public sealed class AnthropicCacheHandler(HttpMessageHandler inner, bool cache = true) : DelegatingHandler(inner)
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var messages = request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath.EndsWith("/messages", StringComparison.Ordinal) == true;
        if (messages && PromptCache.Hints is { } hints && (cache || hints.Thinking is not null) && request.Content is not null)
        {
            var body = await request.Content.ReadAsStringAsync(cancellationToken);
            var cached = cache ? AddBreakpoints(body, hints) : null;
            if ((SetThinking(cached ?? body, hints.Thinking) ?? cached) is { } changed)
            {
                var headers = request.Content.Headers.ToList();
                request.Content = new StringContent(changed, Encoding.UTF8);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                foreach (var h in headers.Where(h => !h.Key.StartsWith("Content-", StringComparison.OrdinalIgnoreCase)))
                    request.Content.Headers.TryAddWithoutValidation(h.Key, h.Value);
            }
        }

        var response = await base.SendAsync(request, cancellationToken);
        if (!messages || PromptCache.Observation is not { } seen) return response;
        seen.Status = (int)response.StatusCode;
        if (!response.IsSuccessStatusCode)
        {
            seen.RetryAfter = RetryAfter(response);
            return response;
        }
        if (response.Content.Headers.ContentType?.MediaType != "application/json") return response;
        await response.Content.LoadIntoBufferAsync(cancellationToken);
        try
        {
            var usage = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken))?["usage"];
            seen.InputTokens = Number(usage?["input_tokens"]);
            seen.OutputTokens = Number(usage?["output_tokens"]);
            seen.CacheReadTokens = Number(usage?["cache_read_input_tokens"]);
            seen.CacheWriteTokens = Number(usage?["cache_creation_input_tokens"]);
        }
        catch (JsonException) { }
        return response;
    }

    private static long? Number(JsonNode? node) => node is JsonValue v && v.TryGetValue<long>(out var n) ? n : null;

    /// <summary><c>retry-after-ms</c> (milliseconds) or <c>retry-after</c> (seconds) of a failed response; null without either.</summary>
    public static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        static double? Value(HttpResponseMessage r, string name) =>
            r.Headers.TryGetValues(name, out var values) && double.TryParse(values.FirstOrDefault(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d >= 0 ? d : null;
        return Value(response, "retry-after-ms") is { } ms ? TimeSpan.FromMilliseconds(ms) : Value(response, "retry-after") is { } s ? TimeSpan.FromSeconds(s) : null;
    }

    /// <summary>
    /// The request body with <c>thinking</c> set: <c>{"type":"disabled"}</c>; for an effort (<c>low</c>, <c>medium</c>,
    /// <c>high</c>) <c>{"type":"adaptive"}</c> with <c>output_config.effort</c> (beside the response format, which stays); enabled
    /// with a token budget when <paramref name="thinking"/> is a number. Null when nothing is asked for or the body is not a
    /// Messages request.
    /// </summary>
    public static string? SetThinking(string body, string? thinking)
    {
        if (string.IsNullOrWhiteSpace(thinking)) return null;
        try
        {
            if (JsonNode.Parse(body) is not JsonObject root) return null;
            var value = thinking.Trim().ToLowerInvariant();
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var budget))
                root["thinking"] = new JsonObject { ["type"] = "enabled", ["budget_tokens"] = budget };
            else if (value is "low" or "medium" or "high")
            {
                root["thinking"] = new JsonObject { ["type"] = "adaptive" };
                if (root["output_config"] is not JsonObject output) root["output_config"] = output = [];
                output["effort"] = value;
            }
            else root["thinking"] = new JsonObject { ["type"] = value };
            return root.ToJsonString();
        }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// The request body with the breakpoints added; null when there is nothing to add (no system text, no such user block) or the
    /// body is not a Messages request. A system string becomes one text block; an array keeps its blocks and the last one gets the
    /// breakpoint. The named block of the first user message gets a 5-minute breakpoint (a string content becomes one text block).
    /// </summary>
    public static string? AddBreakpoints(string body, CacheHints hints)
    {
        JsonObject root;
        try
        {
            if (JsonNode.Parse(body) is not JsonObject o) return null;
            root = o;
        }
        catch (JsonException) { return null; }
        var changed = false;

        if (hints.SystemTtl is { } ttl)
        {
            var control = new JsonObject { ["type"] = "ephemeral" };
            if (ttl != "5m") control["ttl"] = ttl;
            if (root["system"] is JsonValue text && text.TryGetValue<string>(out var system) && system.Length > 0)
            {
                root["system"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = system, ["cache_control"] = control });
                changed = true;
            }
            else if (root["system"] is JsonArray { Count: > 0 } blocks && blocks[^1] is JsonObject last)
            {
                last["cache_control"] = control;
                changed = true;
            }
        }

        if (hints.UserBlock is { } index && root["messages"] is JsonArray list
            && list.OfType<JsonObject>().FirstOrDefault(m => m["role"]?.GetValue<string>() == "user") is { } user)
        {
            if (user["content"] is JsonValue only && only.TryGetValue<string>(out var content) && index == 0)
            {
                user["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = content, ["cache_control"] = new JsonObject { ["type"] = "ephemeral" } });
                changed = true;
            }
            else if (user["content"] is JsonArray parts && index >= 0 && index < parts.Count && parts[index] is JsonObject block)
            {
                block["cache_control"] = new JsonObject { ["type"] = "ephemeral" };
                changed = true;
            }
        }
        return changed ? root.ToJsonString() : null;
    }
}
