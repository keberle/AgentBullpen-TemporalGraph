using System.ClientModel;
using System.ClientModel.Primitives;
using Anthropic;
using Microsoft.Extensions.AI;
using OpenAI;

namespace TemporalGraph.Llm;

/// <summary>Telemetry and run-folder options for the chat client pipeline.</summary>
/// <param name="CaptureContent">EnableSensitiveData on the OpenTelemetry chat client: prompts and replies on spans (GenAI visualizer).</param>
/// <param name="CallContext">When set, every call is appended to the run's calls.jsonl and tagged with the run tags.</param>
/// <param name="Retries">Retries with backoff on HTTP 429 and 500/502/503/504 (span event + calls.jsonl line each).</param>
/// <param name="RetryDelay">Tests only: replaces the real wait.</param>
/// <param name="Sampling">Temperature and seed put on every call; null sends neither (provider defaults).</param>
/// <param name="FinishInFlight">A cancelled token stops the work between calls, not inside one: a call that has been sent is
/// allowed to finish (bounded by the network timeout) and is logged; the next call is not sent. The indexing stages set it.</param>
/// <param name="PromptCache">An Anthropic client sends its requests through <see cref="AnthropicCacheHandler"/>, which adds
/// the cache breakpoints a caller names (<see cref="TemporalGraph.Llm.PromptCache.Use"/>) and reads the cache counts of each reply. The indexing stages set it
/// (<c>Indexing:Cache</c>). Without it the same handler still reads the usage and a failed reply's retry-after, and adds nothing.</param>
public sealed record LlmPipelineOptions(
    bool CaptureContent = false,
    LlmCallContext? CallContext = null,
    int Retries = 3,
    Func<TimeSpan, CancellationToken, Task>? RetryDelay = null,
    LlmSampling? Sampling = null,
    bool FinishInFlight = false,
    bool PromptCache = false);

/// <summary>Builds the <see cref="IChatClient"/> for <see cref="LlmSettings"/>.</summary>
public static class ChatClientFactory
{
    /// <summary>
    /// <c>Type=OpenAI</c>: OpenAI SDK with <see cref="ApiKeyCredential"/> and, if set, <c>Llm:Endpoint</c>
    /// (OpenRouter, Azure /openai/v1/) and <c>Llm:PolicyId</c> (the <c>x-policy-id</c> header on every HTTP request, through
    /// <see cref="PolicyIdHeader"/> on the SDK's pipeline). <c>Type=Anthropic</c>: Anthropic SDK. Both via <c>AsIChatClient()</c>, then
    /// sampling (temperature, seed) → OpenTelemetry (source "TemporalGraph.Llm"; on the chat client only, not the
    /// agent, so spans aren't duplicated) → retry on 429/5xx → call log.
    /// </summary>
    /// <param name="maxRetries">SDK retries on 429/5xx; null keeps each SDK's default.</param>
    public static IChatClient Create(LlmSettings settings, int? maxRetries = null, LlmPipelineOptions? pipeline = null) =>
        Create(settings, maxRetries, anthropicBaseUrl: null, pipeline);

    /// <summary>
    /// Gateway mode: the same pipeline, but every HTTP request goes through <paramref name="transport"/>'s client (whose
    /// <see cref="GatewayCredentialHandler"/> puts the current request's token on it) and the proxy's refusals become
    /// <see cref="GatewayRefusedException"/> (innermost, so they are not retried). <c>settings.ApiKey</c> is
    /// <see cref="GatewayCredentialHandler.PlaceholderKey"/>; for Anthropic the base URL comes from the transport.
    /// </summary>
    public static IChatClient Create(LlmSettings settings, int? maxRetries, LlmPipelineOptions? pipeline, LlmTransport transport) =>
        Create(settings, maxRetries, transport.AnthropicBaseUrl, pipeline, transport.HttpClient, transport.Gateway);

    // anthropicBaseUrl: tests (local fake server) and gateway mode. Llm:Endpoint is rejected for Anthropic.
    // gateway: the HTTP client talks to the site's proxy (false: a standalone client that only reads the provider's cost).
    internal static IChatClient Create(LlmSettings settings, int? maxRetries, string? anthropicBaseUrl, LlmPipelineOptions? pipeline = null, HttpClient? httpClient = null,
        bool gateway = true)
    {
        var problems = settings.Validate();
        if (problems.Count > 0)
            throw new InvalidOperationException(string.Join(" ", problems));
        pipeline ??= new LlmPipelineOptions();

        IChatClient inner;
        if (settings.IsOpenAI)
        {
            var options = new OpenAIClientOptions();
            if (!string.IsNullOrWhiteSpace(settings.Endpoint)) options.Endpoint = new Uri(settings.Endpoint);
            if (maxRetries is { } openAiRetries) options.RetryPolicy = new ClientRetryPolicy(openAiRetries);
            if (settings.TimeoutSeconds is { } openAiTimeout) options.NetworkTimeout = TimeSpan.FromSeconds(openAiTimeout);
            if (!string.IsNullOrWhiteSpace(settings.PolicyId))
                options.AddPolicy(new PolicyIdHeader(settings.PolicyId), PipelinePosition.PerCall);
            // The transport runs after every pipeline policy, the SDK's key policy included.
            if (httpClient is not null) options.Transport = new HttpClientPipelineTransport(httpClient);

            inner = new OpenAIClient(new ApiKeyCredential(settings.ApiKey!), options)
                .GetChatClient(settings.Model!)
                .AsIChatClient();
        }
        else
        {
            var client = new AnthropicClient { ApiKey = settings.ApiKey! };
            if (maxRetries is { } anthropicRetries) client = (AnthropicClient)client.WithOptions(o => o with { MaxRetries = anthropicRetries });
            if (settings.TimeoutSeconds is { } anthropicTimeout) client = (AnthropicClient)client.WithOptions(o => o with { Timeout = TimeSpan.FromSeconds(anthropicTimeout) });
            if (anthropicBaseUrl is not null) client = (AnthropicClient)client.WithOptions(o => o with { BaseUrl = anthropicBaseUrl });
            // A client of our own when none was given, so the handler can add the cache breakpoints and read the usage.
            var own = httpClient is null && (pipeline.PromptCache || pipeline.CallContext is not null)
                ? new HttpClient(new AnthropicCacheHandler(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) }, pipeline.PromptCache)) { Timeout = Timeout.InfiniteTimeSpan }
                : null;
            if (httpClient is not null) client = (AnthropicClient)client.WithOptions(o => o with { HttpClient = httpClient });
            else if (own is not null) client = (AnthropicClient)client.WithOptions(o => o with { HttpClient = own });

            inner = client.AsIChatClient(settings.Model!);
        }
        if (httpClient is not null && gateway) inner = new GatewayRefusalChatClient(inner);

        // ChatClientBuilder: the first added is the outermost. Sampling first, so OpenTelemetry sees the options it sets.
        var builder = new ChatClientBuilder(inner);
        if (pipeline.Sampling is { } sampling)
            builder.Use(next => new SamplingChatClient(next, sampling));
        builder.UseOpenTelemetry(sourceName: LlmTelemetry.SourceName, configure: otel => otel.EnableSensitiveData = pipeline.CaptureContent);
        if (pipeline.Retries > 0)
            builder.Use(next => new RetryOnTransientChatClient(next, pipeline.CallContext, pipeline.Retries, pipeline.RetryDelay));
        // Between the retry and the call log: a retry's wait stops at once, a call that was sent is logged when it ends, and a call
        // that was never sent leaves no line.
        if (pipeline.FinishInFlight)
            builder.Use(next => new FinishInFlightChatClient(next));
        if (pipeline.CallContext is { } context)
            builder.Use(next => new CallLogChatClient(next, context, settings.Model!));
        return builder.Build();
    }
}

/// <summary>
/// "finish the in-flight model call". A call that has not started when the token is cancelled is not sent; one that has
/// been sent runs to its end without the token (its network timeout still bounds it), so its reply and tokens are not thrown away.
/// </summary>
public sealed class FinishInFlightChatClient(IChatClient inner) : DelegatingChatClient(inner)
{
    public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return base.GetResponseAsync(messages, options, CancellationToken.None);
    }
}

/// <summary>
/// Azure content filter selection per call: sets <c>x-policy-id</c> on every HTTP request of the OpenAI SDK's
/// <see cref="System.ClientModel.Primitives.ClientPipeline"/> (a per-call <see cref="PipelinePolicy"/> added with
/// <see cref="ClientPipelineOptions.AddPolicy"/>), and tags the current span (the GenAI chat span) with <c>llm.policy_id</c>.
/// </summary>
public sealed class PolicyIdHeader(string policyId) : PipelinePolicy
{
    public const string HeaderName = "x-policy-id";

    public string PolicyId { get; } = policyId;

    public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        Apply(message);
        ProcessNext(message, pipeline, currentIndex);
    }

    public override ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        Apply(message);
        return ProcessNextAsync(message, pipeline, currentIndex);
    }

    private void Apply(PipelineMessage message)
    {
        message.Request.Headers.Set(HeaderName, PolicyId);
        System.Diagnostics.Activity.Current?.SetTag(LlmTelemetry.PolicyId, PolicyId);
    }
}
