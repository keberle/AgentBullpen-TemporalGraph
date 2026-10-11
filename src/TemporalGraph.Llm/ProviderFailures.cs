using System.ClientModel;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Anthropic.Exceptions;

namespace TemporalGraph.Llm;

/// <summary>
/// A provider account that can't serve right now (the same rules as the AgentBullpen.AI site's), as opposed to a passing
/// error worth retrying. <see cref="Reason"/>: <c>credits</c> (out of credits), <c>key-limit</c> (the key's own spending limit),
/// <c>quota</c> (a quota or a long rate-limit window), <c>auth</c> (the key isn't accepted).
/// </summary>
/// <param name="Status">The provider's HTTP status.</param>
/// <param name="RetryAfter">How long the provider asked to wait, when it said.</param>
public sealed record ProviderFailure(string Reason, int Status, TimeSpan? RetryAfter = null)
{
    public const string Credits = "credits";
    public const string KeyLimit = "key-limit";
    public const string Quota = "quota";
    public const string Auth = "auth";
}

/// <summary>An error response from a provider: status, body (never shown to a reader) and the retry-after it asked for.</summary>
public sealed record ProviderResponse(int Status, string? Body, TimeSpan? RetryAfter);

/// <summary>
/// Classifies provider failures, shared by the API's standalone chat, <c>setup check</c> / <c>llm-check</c> and the retry client
/// (which doesn't retry them). Rules: OpenRouter 402 → <c>key-limit</c> when <c>error.metadata.limit_source</c> is
/// <c>openrouter_key_limit</c>, else <c>credits</c>; Anthropic 400 whose message says the credit balance is too low → <c>credits</c>;
/// 429 asking to wait more than 60 s (Azure's quota window), or about a quota (OpenAI <c>insufficient_quota</c>) → <c>quota</c>;
/// 401 or 403 → <c>auth</c>. Anything else (a short 429, 5xx, a timeout) is not a provider-account failure.
/// </summary>
public static partial class ProviderFailures
{
    /// <summary>The longest retry-after that still counts as a passing rate limit.</summary>
    public static readonly TimeSpan LongWait = TimeSpan.FromSeconds(60);

    public static ProviderFailure? Classify(int status, string? body, TimeSpan? retryAfter)
    {
        var message = Message(body);
        retryAfter ??= RetryAfterIn(message);
        return status switch
        {
            402 => new ProviderFailure(LimitSource(body) == "openrouter_key_limit" ? ProviderFailure.KeyLimit : ProviderFailure.Credits, 402, retryAfter),
            400 when message?.Contains("credit balance is too low", StringComparison.OrdinalIgnoreCase) == true => new ProviderFailure(ProviderFailure.Credits, 400),
            429 when retryAfter > LongWait || (message is not null && QuotaPattern().IsMatch(message))
                || ErrorCode(body) is "insufficient_quota" => new ProviderFailure(ProviderFailure.Quota, 429, retryAfter),
            401 or 403 => new ProviderFailure(ProviderFailure.Auth, status),
            _ => null,
        };
    }

    /// <summary>The classification of a failed call, looking through wrapping exceptions; null when it isn't a provider-account failure.</summary>
    public static ProviderFailure? Classify(Exception ex) =>
        Response(ex) is { } r ? Classify(r.Status, r.Body, r.RetryAfter) : null;

    /// <summary>The provider's error response inside <paramref name="ex"/> (OpenAI SDK or Anthropic SDK), if there is one.</summary>
    public static ProviderResponse? Response(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            switch (e)
            {
                case ClientResultException { Status: > 0 } c:
                    string? body = null;
                    try { body = c.GetRawResponse()?.Content?.ToString(); }
                    catch (Exception) { }
                    return new ProviderResponse(c.Status, body, RetryOnTransientChatClient.RetryAfter(c));
                case AnthropicApiException a:
                    return new ProviderResponse((int)a.StatusCode, a.ResponseBody, null);
            }
        }
        return null;
    }

    /// <summary>
    /// What the person running a standalone copy should do: no failover here, so the message says how to fix it.
    /// <paramref name="provider"/> is the provider's display name (their own account, so naming it is fine).
    /// </summary>
    public static string FixIt(string provider, string reason) => reason switch
    {
        ProviderFailure.Credits => $"Your {provider} account is out of credits — top it up, or run `setup` to switch the chat to another model.",
        ProviderFailure.KeyLimit => $"Your {provider} key has reached its spending limit — raise the key's limit, or run `setup` to switch the chat to another model.",
        ProviderFailure.Quota => $"Your {provider} quota is used up for now — wait for it to reset or raise it, or run `setup` to switch the chat to another model.",
        ProviderFailure.Auth => $"Your {provider} API key wasn't accepted — run `setup` to enter a new key, or switch the chat to another model.",
        _ => $"Your {provider} account can't serve the chat right now — try again later, or run `setup` to switch the chat to another model.",
    };

    /// <summary>The provider's display name for a provider id (<c>openrouter</c> → OpenRouter); "model provider" for anything else (an endpoint's host).</summary>
    public static string Label(string? provider) => provider?.ToLowerInvariant() switch
    {
        "openai" => "OpenAI",
        "anthropic" => "Anthropic",
        "azure" => "Azure OpenAI",
        "openrouter" => "OpenRouter",
        _ => "model provider",
    };

    private static JsonElement? Error(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("error", out var e) ? e.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary><c>error.message</c> (OpenAI, Azure, OpenRouter and Anthropic all nest it there); else the raw body.</summary>
    private static string? Message(string? body) =>
        Error(body) is { } e && e.ValueKind == JsonValueKind.Object && e.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : body;

    private static string? ErrorCode(string? body) =>
        Error(body) is { } e && e.ValueKind == JsonValueKind.Object && (e.TryGetProperty("code", out var c) || e.TryGetProperty("type", out c)) && c.ValueKind == JsonValueKind.String
            ? c.GetString() : null;

    /// <summary>OpenRouter's <c>error.metadata.limit_source</c>.</summary>
    private static string? LimitSource(string? body) =>
        Error(body) is { } e && e.ValueKind == JsonValueKind.Object && e.TryGetProperty("metadata", out var md) && md.ValueKind == JsonValueKind.Object
        && md.TryGetProperty("limit_source", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;

    /// <summary>Azure says "Please retry after 86400 seconds." in the message (the Anthropic SDK doesn't expose headers).</summary>
    private static TimeSpan? RetryAfterIn(string? message) =>
        message is not null && RetryAfterPattern().Match(message) is { Success: true } m
        && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? TimeSpan.FromSeconds(s) : null;

    /// <summary>"quota" as a word: OpenAI's "exceeded your current quota", not Azure's https://aka.ms/oai/quotaincrease link on every 429.</summary>
    [GeneratedRegex(@"\bquota\b", RegexOptions.IgnoreCase)]
    private static partial Regex QuotaPattern();

    [GeneratedRegex(@"retry after (\d+(?:\.\d+)?) second", RegexOptions.IgnoreCase)]
    private static partial Regex RetryAfterPattern();
}
