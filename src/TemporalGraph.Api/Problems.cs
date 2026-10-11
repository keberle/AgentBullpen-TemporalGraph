using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using HotChocolate.Execution;
using TemporalGraph.Llm;

namespace TemporalGraph.Api;

/// <param name="Slug">The last part of the type URI, <c>https://agentbullpen.ai/problems/&lt;slug&gt;</c>.</param>
/// <param name="Codes">The GraphQL <c>extensions.code</c> values that are this problem (the codes are older than the problem catalogue and stay).</param>
public sealed record ProblemType(string Slug, string Title, int Status, params string[] Codes)
{
    public string Uri => Problems.TypeBase + Slug;
}

/// <summary>
/// One error model for both transports, RFC 9457 problem details. REST answers are problem JSON (ASP.NET Core's
/// <c>AddProblemDetails</c>); every GraphQL error keeps its <c>extensions.code</c> and carries <c>extensions.problem</c> =
/// <c>{ type, title, status, detail?, …typed extensions, traceId }</c>. The type URIs are the AgentBullpen.AI site's, so a
/// problem means the same thing from the site's proxy, through this API, to the web panel. See docs/design/errors.md.
/// </summary>
public static class Problems
{
    public const string TypeBase = "https://agentbullpen.ai/problems/";
    public const string MeterName = "TemporalGraph.Api";
    public const string CounterName = "temporalgraph.problems";

    // The site's catalogue …
    public static readonly ProblemType BudgetExhausted = new("budget-exhausted", "Daily allowance used up", 403, "BUDGET_EXHAUSTED");
    // The member's allowance level is off. Nothing resets it; the graph and the rest still work.
    public static readonly ProblemType ChatOff = new("chat-off", "Chat isn't turned on for this account", 403, "CHAT_OFF");
    public static readonly ProblemType RateLimited = new("rate-limited", "Too many requests", 429, "RATE_LIMITED");
    public static readonly ProblemType ModelNotAllowed = new("model-not-allowed", "Model not allowed", 403, "UNKNOWN_MODEL");
    public static readonly ProblemType ProviderUnavailable = new("provider-unavailable", "Model provider unavailable", 503, "PROVIDER_UNAVAILABLE");
    public static readonly ProblemType UpstreamError = new("upstream-error", "The model call failed", 502, "LLM_ERROR");
    public static readonly ProblemType Unauthorized = new("unauthorized", "Sign-in required", 401, "UNAUTHORIZED");
    // The site's later types. upstream-rejected keeps the provider's 4xx at the site; this API answers 400.
    public static readonly ProblemType UpstreamRejected = new("upstream-rejected", "The model provider rejected the request", 400, "UPSTREAM_REJECTED");
    public static readonly ProblemType RequestTooLarge = new("request-too-large", "Request too large", 413, "REQUEST_TOO_LARGE");
    public static readonly ProblemType ProviderNotFound = new("provider-not-found", "Model provider not found", 404, "PROVIDER_NOT_FOUND");
    // The site has the demo closed; the reader is sent to the under-construction page.
    public static readonly ProblemType AppUnavailable = new("app-unavailable", "This demo isn't open right now", 503, "APP_UNAVAILABLE");
    // … and this app's own.
    public static readonly ProblemType ModelNotConfigured = new("model-not-configured", "No chat model configured", 503, "LLM_NOT_CONFIGURED");
    public static readonly ProblemType Timeout = new("timeout", "Timed out", 504, "TIMEOUT");
    public static readonly ProblemType UnknownBook = new("unknown-book", "Unknown book", 404, "UNKNOWN_BOOK");
    public static readonly ProblemType UnknownChapter = new("unknown-chapter", "Unknown chapter", 404, "UNKNOWN_CHAPTER");
    public static readonly ProblemType UnknownCharacter = new("unknown-character", "Unknown character", 404, "UNKNOWN_CHARACTER");
    public static readonly ProblemType InvalidQuery = new("invalid-query", "Invalid query", 400, "SYNTAX_ERROR", "NOT_SUPPORTED", "QUERY_ERROR");
    public static readonly ProblemType InvalidRequest = new("invalid-request", "Invalid request", 400, "EMPTY_MESSAGE");
    public static readonly ProblemType NotFound = new("not-found", "Not found", 404);
    public static readonly ProblemType InternalError = new("internal-error", "Something went wrong", 500, "INTERNAL_ERROR");

    public static readonly IReadOnlyList<ProblemType> All =
    [
        BudgetExhausted, ChatOff, RateLimited, ModelNotAllowed, ProviderUnavailable, UpstreamError, Unauthorized, UpstreamRejected, RequestTooLarge, ProviderNotFound, AppUnavailable,
        ModelNotConfigured, Timeout, UnknownBook, UnknownChapter, UnknownCharacter, InvalidQuery, InvalidRequest, NotFound, InternalError,
    ];

    private static readonly Dictionary<string, ProblemType> ByCode =
        All.SelectMany(p => p.Codes.Select(c => (c, p))).ToDictionary(x => x.c, x => x.p, StringComparer.Ordinal);

    private static readonly Meter Meter = new(MeterName);

    /// <summary><c>temporalgraph.problems{type, reason}</c>: one per problem answered, REST or GraphQL.</summary>
    public static readonly Counter<long> Counter = Meter.CreateCounter<long>(CounterName, description: "Problems answered (RFC 9457 type slug, and reason when there is one)");

    /// <summary>The problem for a GraphQL code; HotChocolate's own codes (HC…) and unknown ones are <see cref="InvalidRequest"/>.</summary>
    public static ProblemType ForCode(string? code) =>
        code is not null && ByCode.TryGetValue(code, out var p) ? p : code is null ? InternalError : InvalidRequest;

    public static ProblemType? ForStatus(int status) => status switch
    {
        401 => Unauthorized,
        404 => NotFound,
        >= 500 => InternalError,
        _ => null,
    };

    /// <summary>The <c>extensions.problem</c> object: type, title, status, detail, the typed extensions (null values left out), traceId.</summary>
    public static Dictionary<string, object?> Problem(ProblemType type, string? detail, IReadOnlyDictionary<string, object?>? extensions = null)
    {
        var problem = new Dictionary<string, object?> { ["type"] = type.Uri, ["title"] = type.Title, ["status"] = type.Status };
        if (detail is not null) problem["detail"] = detail;
        foreach (var (k, v) in extensions ?? new Dictionary<string, object?>())
            if (v is not null && k is not ("type" or "title" or "status" or "detail" or "traceId")) problem[k] = v;
        return problem;
    }

    /// <summary>A GraphQL error with the code and its problem (the error filter adds the traceId and counts it).</summary>
    public static GraphQLException Error(string code, string message, IReadOnlyDictionary<string, object?>? extensions = null) =>
        new(ErrorBuilder.New().SetMessage(message).SetCode(code).SetExtension("problem", Problem(ForCode(code), message, extensions)).Build());

    /// <summary>Counts the problem and marks the current span: status Error, <c>problem.type</c> (and <c>problem.reason</c>).</summary>
    public static void Record(string slug, string? reason)
    {
        Counter.Add(1, new KeyValuePair<string, object?>("type", slug), new KeyValuePair<string, object?>("reason", reason ?? ""));
        if (Activity.Current is { } span)
        {
            span.SetStatus(ActivityStatusCode.Error, slug);
            span.SetTag("problem.type", slug);
            if (reason is not null) span.SetTag("problem.reason", reason);
        }
    }

    public static string Slug(string typeUri) => typeUri.StartsWith(TypeBase, StringComparison.Ordinal) ? typeUri[TypeBase.Length..] : typeUri;

    /// <summary>The start of the next UTC day: when a daily allowance resets unless the site says otherwise.</summary>
    public static string NextUtcMidnight(DateTimeOffset now) => new DateTimeOffset(now.UtcDateTime.Date.AddDays(1), TimeSpan.Zero).ToString("O");

    /// <summary>REST: <c>AddProblemDetails</c> with this catalogue's type for 401, 404 and 5xx, and the counter.</summary>
    public static IServiceCollection AddProblems(this IServiceCollection services) =>
        services.AddProblemDetails(o => o.CustomizeProblemDetails = ctx =>
        {
            var details = ctx.ProblemDetails;
            if (ForStatus(details.Status ?? ctx.HttpContext.Response.StatusCode) is { } type)
            {
                details.Type = type.Uri;
                details.Title = type.Title;
                if (type == InternalError) details.Detail = null;   // never an exception's text
            }
            // The trace id alone (32 hex), as in GraphQL problems and the panel's "ref": what the Aspire dashboard's trace search takes.
            ctx.ProblemDetails.Extensions["traceId"] = Activity.Current?.TraceId.ToString() ?? ctx.HttpContext.TraceIdentifier;
            Record(details.Type is { } t ? Slug(t) : $"http-{details.Status}", null);
        });
}

/// <summary>
/// Every GraphQL error leaves with <c>extensions.problem</c>. Errors built with <see cref="Problems.Error"/> keep theirs; errors
/// with only a code (the book catalogue's, the Cypher parser's with line and column, HotChocolate's own) get one from the code; a gateway
/// refusal thrown outside the chat becomes its problem; any other exception becomes <c>internal-error</c> with only the traceId (no
/// message, stack or provider text). Each is counted and marks the span.
/// </summary>
public sealed class ProblemErrorFilter : IErrorFilter
{
    private static readonly string[] QueryPosition = ["reason", "line", "column", "offset"];

    public IError OnError(IError error)
    {
        var traceId = Activity.Current?.TraceId.ToString();
        if (error.Code is null && error.Exception is { } ex)
        {
            var refused = Refusal(ex);
            error = (refused is not null
                    ? ErrorBuilder.FromError(error).SetMessage(refused.Message).SetCode(refused.Code)
                        .SetExtension("problem", Problems.Problem(Problems.ForCode(refused.Code), refused.Message, refused.Extensions))
                    : ErrorBuilder.FromError(error).SetMessage(traceId is null ? "Something went wrong." : $"Something went wrong (ref {traceId}).")
                        .SetCode(Problems.InternalError.Codes[0]).SetExtension("problem", Problems.Problem(Problems.InternalError, null)))
                .SetException(null).Build();
        }

        var problem = error.Extensions?.TryGetValue("problem", out var p) == true && p is IReadOnlyDictionary<string, object?> given
            ? new Dictionary<string, object?>(given)
            : Problems.Problem(Problems.ForCode(error.Code), error.Message,
                error.Extensions?.Where(e => QueryPosition.Contains(e.Key)).ToDictionary(e => e.Key, e => e.Value));
        if (traceId is not null) problem["traceId"] = traceId;
        Problems.Record(Problems.Slug((string)problem["type"]!), problem.GetValueOrDefault("reason") as string);
        return ErrorBuilder.FromError(error).SetExtension("problem", problem).Build();
    }

    private static GatewayRefusedException? Refusal(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is GatewayRefusedException r) return r;
            if (e is GatewayTokenMissingException) return new GatewayRefusedException(GatewayRefusedException.Unauthorized, 401, "Sign in to chat.");
        }
        return null;
    }
}
