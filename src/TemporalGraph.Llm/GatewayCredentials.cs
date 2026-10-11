using System.ClientModel;
using System.Net.Http.Headers;
using System.Text.Json;
using Anthropic.Exceptions;
using Microsoft.Extensions.AI;

namespace TemporalGraph.Llm;

/// <summary>
/// How a model call reaches a provider when the app runs behind the AgentBullpen.AI gateway. The provider SDKs need an
/// <see cref="HttpClient"/> whose handler puts the credential on every request (<see cref="GatewayCredentialHandler"/>), and for
/// Anthropic the base URL (<c>Llm:Endpoint</c> is rejected for Anthropic in config; this is the only way to set it).
/// </summary>
/// <param name="Gateway">True (the default): the client talks to the site's LLM proxy, whose refusals become
/// <see cref="GatewayRefusedException"/>. False (standalone): only the HTTP client is replaced, to read the provider's cost.</param>
public sealed record LlmTransport(HttpClient HttpClient, string? AnthropicBaseUrl = null, bool Gateway = true);

/// <summary>A gateway-mode model call with no incoming bearer token to send: it fails before any network call.</summary>
public sealed class GatewayTokenMissingException() : InvalidOperationException("No gateway token for this request; the model call was not sent.");

/// <summary>
/// The site's LLM proxy refused a call: <see cref="Code"/> is the GraphQL error code the API answers with.
/// The message never carries the proxy's or the provider's text. <see cref="Extensions"/> are the typed extensions of the
/// proxy's problem details that a reader's panel may use (<c>resetsAtUtc</c>, <c>retryAfterSeconds</c>, <c>reason</c>,
/// <c>fallbackModel</c>, …); never <c>provider</c> (the site's account) and never the proxy's <c>detail</c>.
/// </summary>
public sealed class GatewayRefusedException(string code, int status, string message, IReadOnlyDictionary<string, object?>? extensions = null) : Exception(message)
{
    public const string BudgetExhausted = "BUDGET_EXHAUSTED";
    public const string RateLimited = "RATE_LIMITED";
    public const string Unauthorized = "UNAUTHORIZED";
    public const string ModelNotAllowed = "UNKNOWN_MODEL";
    public const string ProviderUnavailable = "PROVIDER_UNAVAILABLE";
    public const string UpstreamError = "LLM_ERROR";
    // The site's later problem types.
    public const string UpstreamRejected = "UPSTREAM_REJECTED";
    public const string RequestTooLarge = "REQUEST_TOO_LARGE";
    public const string ProviderNotFound = "PROVIDER_NOT_FOUND";
    // The site has this demo closed (soon, paused, hidden) and the caller isn't an admin.
    public const string AppUnavailable = "APP_UNAVAILABLE";
    // The member's allowance level is off: chat isn't turned on for the account.
    public const string ChatOff = "CHAT_OFF";

    public string Code { get; } = code;
    public int Status { get; } = status;
    public IReadOnlyDictionary<string, object?> Extensions { get; } = extensions ?? new Dictionary<string, object?>();
}

/// <summary>
/// The last handler before the network in gateway mode. On every HTTP request it replaces the SDK's credential with the
/// current request's gateway token: <c>Authorization: Bearer</c> for OpenAI-compatible providers, <c>x-api-key</c> for Anthropic.
/// It runs below the SDKs' own pipelines (the OpenAI SDK's key policy and the Anthropic SDK's header both run first), so the token
/// always wins over the placeholder key. No token: <see cref="GatewayTokenMissingException"/>, and nothing is sent. Optionally adds
/// <c>X-Conversation-Id</c> so the site's ledger groups a conversation. The token is never logged.
/// </summary>
/// <param name="token">Read on every request: the incoming request's bearer token, or null.</param>
/// <param name="onFallback">Called with the proxy's <c>X-Model-Fallback</c> header (<c>&lt;requested&gt; -&gt; &lt;used&gt;</c>) when a
/// response carries one: the site answered with its fallback model because the requested one's provider is unavailable.</param>
/// <param name="onCost">Called once per answered call (HTTP 2xx) with the proxy's <c>X-Call-Cost-Usd</c>, or null when the
/// response carries none.</param>
public sealed class GatewayCredentialHandler(Func<string?> token, bool anthropic, string? conversationId = null, Action<string>? onFallback = null,
    Action<decimal?>? onCost = null) : DelegatingHandler
{
    /// <summary>The key the SDKs are built with in gateway mode. Never sent: the handler replaces it or refuses the call.</summary>
    public const string PlaceholderKey = "gateway-token-not-set";

    public const string ConversationHeader = "X-Conversation-Id";
    public const string FallbackHeader = "X-Model-Fallback";

    /// <summary>What this call cost, in US dollars with 6 decimals (LLM-PROXY.md "Budget headers").</summary>
    public const string CallCostHeader = "X-Call-Cost-Usd";

    /// <summary>The header's value as dollars; null when absent, not a number, or negative.</summary>
    public static decimal? CallCost(HttpResponseMessage response) =>
        response.Headers.TryGetValues(CallCostHeader, out var values)
        && decimal.TryParse(values.FirstOrDefault(), System.Globalization.NumberStyles.AllowDecimalPoint, System.Globalization.CultureInfo.InvariantCulture, out var usd)
        && usd >= 0 ? usd : null;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var current = token();
        if (string.IsNullOrWhiteSpace(current)) throw new GatewayTokenMissingException();
        request.Headers.Authorization = null;
        request.Headers.Remove("x-api-key");
        if (anthropic) request.Headers.TryAddWithoutValidation("x-api-key", current);
        else request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", current);
        if (conversationId is { Length: > 0 })
        {
            request.Headers.Remove(ConversationHeader);
            request.Headers.TryAddWithoutValidation(ConversationHeader, conversationId);
        }
        var response = await base.SendAsync(request, cancellationToken);
        if (onFallback is not null && response.Headers.TryGetValues(FallbackHeader, out var values) && values.FirstOrDefault() is { Length: > 0 } fallback)
            onFallback(fallback);
        if (onCost is not null && response.IsSuccessStatusCode) onCost(CallCost(response));
        return response;
    }
}

/// <summary>
/// Standalone: reads what a call cost from the provider's own answer, when the provider says (OpenRouter's <c>usage.cost</c>, in
/// US dollars, on every completion). A provider that sends none (OpenAI, Azure) reports null: the cost is never estimated. The body is
/// buffered so the SDK reads it as usual; only JSON answers are looked at (the chat's calls aren't streamed).
/// </summary>
/// <param name="onCost">Called once per answered call (HTTP 2xx) with its cost, or null.</param>
public sealed class ProviderCostHandler(Action<decimal?> onCost) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) return response;
        decimal? cost = null;
        if (response.Content.Headers.ContentType?.MediaType is { } type && type.EndsWith("json", StringComparison.OrdinalIgnoreCase))
        {
            await response.Content.LoadIntoBufferAsync(cancellationToken);
            cost = Cost(await response.Content.ReadAsStringAsync(cancellationToken));
        }
        onCost(cost);
        return response;
    }

    /// <summary><c>usage.cost</c> of a completion body, as dollars; null when there is none.</summary>
    public static decimal? Cost(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object
                && usage.TryGetProperty("cost", out var cost) && cost.ValueKind == JsonValueKind.Number && cost.TryGetDecimal(out var usd) && usd >= 0
                ? usd : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// Innermost in a gateway-mode chat client: turns the site LLM proxy's refusals into <see cref="GatewayRefusedException"/>,
/// so the retry client above it doesn't retry them and the API can answer with a code. LLM-PROXY.md "Responses a demo should
/// handle": by the problem's type (<c>budget-exhausted</c>, <c>chat-off</c>, …); without one, 403 <c>Model not allowed</c> →
/// UNKNOWN_MODEL; any other 403 → BUDGET_EXHAUSTED; 401 → UNAUTHORIZED; 429 → RATE_LIMITED. Other errors pass unchanged.
/// </summary>
public sealed class GatewayRefusalChatClient(IChatClient inner) : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        try
        {
            return await base.GetResponseAsync(messages, options, cancellationToken);
        }
        catch (Exception ex) when (Refusal(ex) is { } refusal)
        {
            throw refusal;
        }
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var e = base.GetStreamingResponseAsync(messages, options, cancellationToken).GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            try
            {
                if (!await e.MoveNextAsync()) yield break;
            }
            catch (Exception ex) when (Refusal(ex) is { } refusal)
            {
                throw refusal;
            }
            yield return e.Current;
        }
    }

    /// <summary>The problem <c>type</c> URIs the proxy uses: <c>https://agentbullpen.ai/problems/&lt;slug&gt;</c>.</summary>
    public const string ProblemBase = "https://agentbullpen.ai/problems/";

    /// <summary>The typed extensions of the proxy's problems a reader's panel may use. Not <c>provider</c>, not <c>detail</c>.</summary>
    public static readonly string[] PassedExtensions =
        ["retryAfterSeconds", "resetsAtUtc", "dailyAllowanceUsd", "usedTodayUsd", "level", "reason", "fallbackModel", "model", "unavailableUntilUtc",
         "app", "message", "until", "underConstructionUrl"];

    /// <summary>The refusal for a proxy error response, or null when <paramref name="ex"/> isn't one.</summary>
    public static GatewayRefusedException? Refusal(Exception ex)
    {
        var (status, body, retryAfter) = ex switch
        {
            ClientResultException c when c.Status > 0 => (c.Status, Body(c), RetryOnTransientChatClient.RetryAfter(c)),
            AnthropicApiException a => ((int)a.StatusCode, a.ResponseBody, null),
            _ => (0, null, (TimeSpan?)null),
        };
        if (status == 0) return null;
        var (type, title, extensions) = Problem(body);
        if (retryAfter is { } wait && !extensions.ContainsKey("retryAfterSeconds")) extensions["retryAfterSeconds"] = (long)Math.Ceiling(wait.TotalSeconds);

        // The proxy's problem type when it sends one, else the status rules.
        switch (type)
        {
            case "budget-exhausted": return new(GatewayRefusedException.BudgetExhausted, status, "Today's allowance is used up.", extensions);
            case "chat-off": return new(GatewayRefusedException.ChatOff, status, "Chat isn't turned on for this account yet.", extensions);
            case "rate-limited": return new(GatewayRefusedException.RateLimited, status, "Too many requests; wait a minute and try again.", extensions);
            case "model-not-allowed": return new(GatewayRefusedException.ModelNotAllowed, status, "This model is not allowed on the site.", extensions);
            case "unauthorized": return new(GatewayRefusedException.Unauthorized, status, "The site did not accept this request's sign-in; sign in again.", extensions);
            case "provider-unavailable": return new(GatewayRefusedException.ProviderUnavailable, status, "The model service is unavailable right now.", extensions);
            case "upstream-error": return new(GatewayRefusedException.UpstreamError, status, "The model call failed upstream.", extensions);
            case "upstream-rejected": return new(GatewayRefusedException.UpstreamRejected, status, "The model provider rejected this request.", extensions);
            case "request-too-large": return new(GatewayRefusedException.RequestTooLarge, status, "That's too long to send.", extensions);
            case "provider-not-found": return new(GatewayRefusedException.ProviderNotFound, status, "That model isn't available.", extensions);
            case "app-unavailable": return new(GatewayRefusedException.AppUnavailable, status, "This demo isn't open right now.", extensions);
        }
        return status switch
        {
            401 => new GatewayRefusedException(GatewayRefusedException.Unauthorized, 401, "The site did not accept this request's sign-in; sign in again."),
            403 when string.Equals(title, "Model not allowed", StringComparison.OrdinalIgnoreCase) =>
                new GatewayRefusedException(GatewayRefusedException.ModelNotAllowed, 403, "This model is not allowed on the site."),
            403 => new GatewayRefusedException(GatewayRefusedException.BudgetExhausted, 403, "Today's allowance is used up.", extensions),
            429 => new GatewayRefusedException(GatewayRefusedException.RateLimited, 429, "Too many requests; wait a minute and try again.", extensions),
            _ => null,
        };
    }

    private static string? Body(ClientResultException ex)
    {
        try { return ex.GetRawResponse()?.Content?.ToString(); }
        catch (Exception) { return null; }
    }

    /// <summary>The problem-details <c>type</c> slug (ours only), <c>title</c> and the passed extensions of an error body.</summary>
    public static (string? Type, string? Title, Dictionary<string, object?> Extensions) Problem(string? body)
    {
        var extensions = new Dictionary<string, object?>();
        if (string.IsNullOrWhiteSpace(body)) return (null, null, extensions);
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return (null, null, extensions);
            string? Text(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var type = Text("type") is { } t && t.StartsWith(ProblemBase, StringComparison.OrdinalIgnoreCase) ? t[ProblemBase.Length..].Trim('/') : null;
            foreach (var name in PassedExtensions)
                if (root.TryGetProperty(name, out var v))
                    extensions[name] = v.ValueKind switch
                    {
                        JsonValueKind.Number => v.TryGetInt64(out var l) ? l : v.GetDouble(),
                        JsonValueKind.String => v.GetString(),
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        _ => null,
                    };
            return (type, Text("title"), extensions);
        }
        catch (JsonException)
        {
            return (null, null, extensions);
        }
    }
}
