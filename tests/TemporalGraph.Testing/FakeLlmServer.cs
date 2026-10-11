using System.Net;
using System.Net.Sockets;
using System.Text;

namespace TemporalGraph.Testing;

/// <summary>
/// Local stand-in for an LLM provider (no real network): answers OpenAI <c>…/chat/completions</c> and
/// Anthropic <c>/v1/messages</c> with a fixed "ready" reply, and records each request. The first
/// <c>rateLimitFirst</c> requests get HTTP 429 instead; then the next <c>errorFirst</c> get <c>errorStatus</c>
/// (e.g. 500/503). OpenAI replies carry <see cref="SystemFingerprint"/>. <see cref="OpenAIMessages"/> scripts OpenAI
/// replies (e.g. a tool call, then an answer) before falling back to <see cref="OpenAIReply"/>. Shared by the test projects.
/// Also the site's LLM proxy: any path ending in the provider path (<c>/llm/openrouter/v1/chat/completions</c>,
/// <c>/llm/anthropic/v1/messages</c>), <c>GET …/models</c> from <see cref="ModelsJson"/>, and error responses shaped by
/// <see cref="ErrorJson"/> and <see cref="ErrorHeaders"/>.
/// </summary>
public sealed class FakeLlmServer : IDisposable
{
    public const string OpenAIModel = "fake-openai-model";
    public const string AnthropicModel = "fake-anthropic-model";
    public const string SystemFingerprint = "fp_fake123";

    /// <param name="PolicyId">The <c>x-policy-id</c> header (Azure content filter selection), if sent.</param>
    /// <param name="ConversationId">The <c>X-Conversation-Id</c> header (gateway mode), if sent.</param>
    public sealed record Request(string Path, string? Authorization, string? XApiKey, string Body, string? PolicyId = null, string? ConversationId = null);

    private readonly HttpListener _listener = new();
    private readonly Task _loop;
    private int _rateLimitLeft;
    private int _errorLeft;
    private readonly int _errorStatus;

    public string BaseUrl { get; }

    /// <summary>The assistant text of the OpenAI reply (default "ready"); e.g. a structured-output JSON string.</summary>
    public string OpenAIReply { get; set; } = "ready";

    /// <summary>The text of the Anthropic reply and its <c>usage</c> object (so a test can give the prompt-cache counts).</summary>
    public string AnthropicReply { get; set; } = "ready";
    public string AnthropicUsageJson { get; set; } = "{\"input_tokens\":12,\"output_tokens\":1}";
    public List<Request> Requests { get; } = [];

    /// <summary>OpenAI replies sent first, in order: each a raw assistant <c>message</c> JSON object (with <c>tool_calls</c>
    /// it gets finish_reason "tool_calls"). See <see cref="ToolCall"/> and <see cref="Text"/>.</summary>
    public System.Collections.Concurrent.ConcurrentQueue<string> OpenAIMessages { get; } = new();

    /// <summary>An OpenAI assistant message that calls one tool.</summary>
    public static string ToolCall(string id, string name, object arguments) =>
        $$$"""{"role":"assistant","content":null,"tool_calls":[{"id":"{{{id}}}","type":"function","function":{"name":"{{{name}}}","arguments":{{{System.Text.Json.JsonSerializer.Serialize(System.Text.Json.JsonSerializer.Serialize(arguments))}}}}}]}""";

    /// <summary>An OpenAI assistant message with text.</summary>
    public static string Text(string text) => $$"""{"role":"assistant","content":{{System.Text.Json.JsonSerializer.Serialize(text)}}}""";

    /// <summary>Milliseconds to wait before answering each request (a slow provider; the TIMEOUT test).</summary>
    public int DelayMs { get; set; }

    /// <summary>When set, 429 responses carry this <c>retry-after-ms</c> header.</summary>
    public int? RetryAfterMs { get; set; }

    /// <summary>The body of <c>GET …/models</c> (the site's <c>/llm/models</c>); null → 404.</summary>
    public string? ModelsJson { get; set; }

    /// <summary>The status of <c>GET …/models</c> when <see cref="ModelsJson"/> is set (e.g. 503 with the site's app-unavailable problem).</summary>
    public int ModelsStatus { get; set; } = 200;

    /// <summary>The body of the <c>errorFirst</c> responses instead of the provider-shaped one (e.g. the proxy's problem details).</summary>
    public string? ErrorJson { get; set; }

    /// <summary>Headers on the <c>errorFirst</c> responses (e.g. <c>X-Budget-Remaining-Usd: 0.0000</c>).</summary>
    public Dictionary<string, string> ErrorHeaders { get; } = [];

    /// <summary>Headers on every successful completion (e.g. the site proxy's <c>X-Model-Fallback</c>).</summary>
    public Dictionary<string, string> ResponseHeaders { get; } = [];

    /// <summary>Headers for successive successful completions, one entry per completion in order (e.g. each call's
    /// <c>X-Call-Cost-Usd</c>); once used up, only <see cref="ResponseHeaders"/> apply.</summary>
    public System.Collections.Concurrent.ConcurrentQueue<Dictionary<string, string>> ResponseHeaderSequence { get; } = new();

    /// <summary>When set, OpenAI replies carry <c>usage.cost</c> with this JSON number (OpenRouter's cost in US dollars).</summary>
    public string? UsageCost { get; set; }

    public FakeLlmServer(int rateLimitFirst = 0, int errorFirst = 0, int errorStatus = 500)
    {
        _rateLimitLeft = rateLimitFirst;
        _errorLeft = errorFirst;
        _errorStatus = errorStatus;
        var port = FreePort();
        BaseUrl = $"http://localhost:{port}";
        _listener.Prefixes.Add(BaseUrl + "/");
        _listener.Start();
        _loop = Task.Run(Serve);
    }

    private async Task Serve()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch (Exception) { return; } // stopped

            var body = await new StreamReader(ctx.Request.InputStream).ReadToEndAsync();
            var path = ctx.Request.Url!.AbsolutePath;
            lock (Requests)
                Requests.Add(new Request(path, ctx.Request.Headers["Authorization"], ctx.Request.Headers["x-api-key"], body, ctx.Request.Headers["x-policy-id"],
                    ctx.Request.Headers["X-Conversation-Id"]));

            if (ctx.Request.HttpMethod == "GET" && path.EndsWith("/models"))
            {
                var models = Encoding.UTF8.GetBytes(ModelsJson ?? "");
                ctx.Response.StatusCode = ModelsJson is null ? 404 : ModelsStatus;
                ctx.Response.ContentType = "application/json";
                ctx.Response.ContentLength64 = models.Length;
                await ctx.Response.OutputStream.WriteAsync(models);
                ctx.Response.Close();
                continue;
            }

            if (DelayMs > 0) await Task.Delay(DelayMs);
            string json;
            if (Interlocked.Decrement(ref _rateLimitLeft) >= 0)
            {
                var error = path.EndsWith("/v1/messages")
                    ? """{"type":"error","error":{"type":"rate_limit_error","message":"fake rate limit"}}"""
                    : """{"error":{"message":"fake rate limit","type":"rate_limit_exceeded","code":"429"}}""";
                var errorBytes = Encoding.UTF8.GetBytes(error);
                ctx.Response.StatusCode = 429;
                if (RetryAfterMs is { } ms) ctx.Response.Headers["retry-after-ms"] = ms.ToString(System.Globalization.CultureInfo.InvariantCulture);
                ctx.Response.ContentType = "application/json";
                ctx.Response.ContentLength64 = errorBytes.Length;
                await ctx.Response.OutputStream.WriteAsync(errorBytes);
                ctx.Response.Close();
                continue;
            }
            if (Interlocked.Decrement(ref _errorLeft) >= 0)
            {
                var error = ErrorJson ?? (path.EndsWith("/v1/messages")
                    ? """{"type":"error","error":{"type":"api_error","message":"fake server error"}}"""
                    : """{"error":{"message":"fake server error","type":"server_error","code":null}}""");
                var errorBytes = Encoding.UTF8.GetBytes(error);
                ctx.Response.StatusCode = _errorStatus;
                foreach (var (name, value) in ErrorHeaders) ctx.Response.Headers[name] = value;
                ctx.Response.ContentType = "application/json";
                ctx.Response.ContentLength64 = errorBytes.Length;
                await ctx.Response.OutputStream.WriteAsync(errorBytes);
                ctx.Response.Close();
                continue;
            }
            if (path.EndsWith("/chat/completions") && OpenAIMessages.TryDequeue(out var message))
                json = $$$"""
                    {"id":"chatcmpl-1","object":"chat.completion","created":1,"model":"{{{OpenAIModel}}}","system_fingerprint":"{{{SystemFingerprint}}}",
                     "choices":[{"index":0,"message":{{{message}}},"finish_reason":"{{{(message.Contains("\"tool_calls\"") ? "tool_calls" : "stop")}}}"}],
                     "usage":{"prompt_tokens":11,"completion_tokens":1,"total_tokens":12}}
                    """;
            else if (path.EndsWith("/chat/completions"))
                json = $$$"""
                    {"id":"chatcmpl-1","object":"chat.completion","created":1,"model":"{{{OpenAIModel}}}","system_fingerprint":"{{{SystemFingerprint}}}",
                     "choices":[{"index":0,"message":{"role":"assistant","content":{{{System.Text.Json.JsonSerializer.Serialize(OpenAIReply)}}}},"finish_reason":"stop"}],
                     "usage":{"prompt_tokens":11,"completion_tokens":1,"total_tokens":12}}
                    """;
            else if (path.EndsWith("/v1/messages"))
                json = $$$"""
                    {"id":"msg_1","type":"message","role":"assistant","model":"{{{AnthropicModel}}}",
                     "content":[{"type":"text","text":{{{System.Text.Json.JsonSerializer.Serialize(AnthropicReply)}}}}],"stop_reason":"end_turn","stop_sequence":null,
                     "usage":{{{AnthropicUsageJson}}}}
                    """;
            else
            {
                ctx.Response.StatusCode = 404;
                ctx.Response.Close();
                continue;
            }

            if (UsageCost is { } cost && path.EndsWith("/chat/completions")) json = json.Replace("\"total_tokens\":12}", $"\"total_tokens\":12,\"cost\":{cost}}}");
            var bytes = Encoding.UTF8.GetBytes(json);
            foreach (var (name, value) in ResponseHeaders) ctx.Response.Headers[name] = value;
            if (ResponseHeaderSequence.TryDequeue(out var next))
                foreach (var (name, value) in next) ctx.Response.Headers[name] = value;
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public void Dispose()
    {
        _listener.Stop();
        _listener.Close();
        try { _loop.Wait(TimeSpan.FromSeconds(5)); } catch (AggregateException) { }
    }
}
