using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using TemporalGraph.Llm;
using TemporalGraph.Cli.Runs;
using TemporalGraph.Cli.Telemetry;

namespace TemporalGraph.Data.Tests;

// Deterministic sampling settings on every call, retries on transient server errors, system_fingerprint.
// Against FakeLlmServer on localhost; no real network. Its span test listens to every span, so it runs apart from the tests that make them
// (it failed once beside the never-say chat test).
[Collection(nameof(SpanListenerCollection))]
public class LlmSamplingAndRetryTests
{
    private static LlmSettings Settings(FakeLlmServer server, bool anthropic = false) =>
        LlmSettings.From(Config(new()
        {
            ["Llm:Type"] = anthropic ? "Anthropic" : "OpenAI",
            ["Llm:Model"] = "my-deployment",
            ["Llm:Endpoint"] = anthropic ? null : server.BaseUrl + "/openai/v1/",
            ["Llm:ApiKey"] = "fake-key-for-tests-0123456789",
        }));

    private static IConfiguration Config(Dictionary<string, string?> values) => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static IChatClient Client(FakeLlmServer server, LlmPipelineOptions pipeline, bool anthropic = false) => anthropic
        ? ChatClientFactory.Create(Settings(server, anthropic: true), maxRetries: 0, server.BaseUrl, pipeline)
        : ChatClientFactory.Create(Settings(server), maxRetries: 0, pipeline);

    private sealed class TempRun : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "TemporalGraph.Data.Tests", Guid.NewGuid().ToString("N"));
        public RunFolder Run { get; }
        public TempRun() => Run = RunFolder.Create(Path.Combine(_root, "runs"));
        public List<JsonNode> Calls() => File.ReadAllLines(Run.File(RunFolder.CallsJsonl)).Select(l => JsonNode.Parse(l)!).ToList();
        public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
    }

    // ---- Sampling settings --------------------------------------------------------------------------

    [Fact]
    public void Sampling_settings_come_from_Llm_Temperature_and_Llm_Seed()
    {
        var openAi = LlmSampling.From(Config(new() { ["Llm:Type"] = "OpenAI", ["Llm:Temperature"] = "0", ["Llm:Seed"] = "20261007" }), new LlmSettings("OpenAI", "m", null, "k"));
        Assert.Equal(new LlmSampling(0, 20261007, SeedSent: true), openAi);

        // Anthropic's Messages API has no seed parameter: configured, not sent.
        var anthropic = LlmSampling.From(Config(new() { ["Llm:Temperature"] = "0", ["Llm:Seed"] = "7" }), new LlmSettings("Anthropic", "m", null, "k"));
        Assert.Equal(new LlmSampling(0, 7, SeedSent: false), anthropic);

        // Empty values: nothing is sent (provider defaults).
        Assert.Equal(new LlmSampling(null, null, false), LlmSampling.From(Config(new() { ["Llm:Temperature"] = "", ["Llm:Seed"] = "" }), new LlmSettings("OpenAI", "m", null, "k")));
    }

    [Fact]
    public void The_committed_template_has_explicit_temperature_0_and_a_seed()
    {
        var template = new ConfigurationBuilder().AddJsonFile(Path.Combine(AppContext.BaseDirectory, "templates", "cli.appsettings.json")).Build();
        var sampling = LlmSampling.From(template, new LlmSettings("OpenAI", "m", null, "k"));
        Assert.Equal(0, sampling.Temperature);
        Assert.NotNull(sampling.Seed);
    }

    [Fact]
    public async Task OpenAI_requests_carry_temperature_and_seed_and_the_span_and_call_log_record_them()
    {
        using var tmp = new TempRun();
        using var server = new FakeLlmServer();
        var spans = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == IndexingTelemetry.LlmSourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = spans.Add,
        };
        ActivitySource.AddActivityListener(listener);
        var context = new LlmCallContext { Run = tmp.Run, Stage = "test" };

        using var client = Client(server, new LlmPipelineOptions(CallContext: context, Sampling: new LlmSampling(0, 20261007, true)));
        await client.GetResponseAsync("hello", cancellationToken: TestContext.Current.CancellationToken);

        var body = JsonNode.Parse(Assert.Single(server.Requests).Body)!;
        Assert.Equal(0, body["temperature"]!.GetValue<double>());
        Assert.Equal(20261007, body["seed"]!.GetValue<long>());
        Assert.Null(body["top_p"]);

        var span = Assert.Single(spans);
        Assert.Equal(0.0, Convert.ToDouble(span.GetTagItem("gen_ai.request.temperature")));
        Assert.Equal(20261007L, Convert.ToInt64(span.GetTagItem("gen_ai.request.seed")));
        Assert.Equal(FakeLlmServer.SystemFingerprint, span.GetTagItem(CallLogChatClient.SystemFingerprintTag));

        var call = Assert.Single(tmp.Calls());
        Assert.Equal(0, call["sampling"]!["temperature"]!.GetValue<double>());
        Assert.Equal(20261007, call["sampling"]!["seed"]!.GetValue<long>());
        Assert.Equal(FakeLlmServer.SystemFingerprint, call["response"]!["systemFingerprint"]!.GetValue<string>());
    }

    [Fact]
    public async Task Without_sampling_settings_nothing_is_sent()
    {
        using var server = new FakeLlmServer();
        using var client = Client(server, new LlmPipelineOptions());
        await client.GetResponseAsync("hello", cancellationToken: TestContext.Current.CancellationToken);

        var body = JsonNode.Parse(Assert.Single(server.Requests).Body)!;
        Assert.Null(body["temperature"]);
        Assert.Null(body["seed"]);
        Assert.Null(body["top_p"]);
    }

    [Fact]
    public async Task Anthropic_requests_carry_temperature_but_no_seed()
    {
        using var server = new FakeLlmServer();
        using var client = Client(server, new LlmPipelineOptions(Sampling: new LlmSampling(0, 7, SeedSent: false)), anthropic: true);
        var response = await client.GetResponseAsync("hello", cancellationToken: TestContext.Current.CancellationToken);

        var body = JsonNode.Parse(Assert.Single(server.Requests).Body)!;
        Assert.Equal(0, body["temperature"]!.GetValue<double>());
        Assert.Null(body["seed"]);
        Assert.Null(CallLogChatClient.SystemFingerprint(response)); // Anthropic replies have none
    }

    [Fact]
    public async Task Sampling_does_not_override_options_the_caller_set()
    {
        using var server = new FakeLlmServer();
        using var client = Client(server, new LlmPipelineOptions(Sampling: new LlmSampling(0, 1, true)));
        await client.GetResponseAsync("hello", new ChatOptions { Temperature = 0.5f, Seed = 99 }, TestContext.Current.CancellationToken);

        var body = JsonNode.Parse(Assert.Single(server.Requests).Body)!;
        Assert.Equal(0.5, body["temperature"]!.GetValue<double>());
        Assert.Equal(99, body["seed"]!.GetValue<long>());
    }

    // ---- Retries on transient server errors ---------------------------------------------------------

    [Theory]
    [InlineData("OpenAI", 500)]
    [InlineData("OpenAI", 502)]
    [InlineData("OpenAI", 503)]
    [InlineData("OpenAI", 504)]
    [InlineData("Anthropic", 500)]
    [InlineData("Anthropic", 503)]
    public async Task Server_errors_are_retried_with_backoff_and_logged_like_429(string type, int status)
    {
        using var tmp = new TempRun();
        using var server = new FakeLlmServer(errorFirst: 2, errorStatus: status);
        var waits = new List<TimeSpan>();
        var context = new LlmCallContext { Run = tmp.Run, Stage = "test" };
        using var client = Client(server, new LlmPipelineOptions(CallContext: context, RetryDelay: (t, _) => { waits.Add(t); return Task.CompletedTask; }), anthropic: type == "Anthropic");

        var response = await client.GetResponseAsync("hello", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("ready", response.Text);
        Assert.Equal(3, server.Requests.Count);
        Assert.Equal([RetryOnTransientChatClient.Backoff(1), RetryOnTransientChatClient.Backoff(2)], waits);
        var lines = tmp.Calls();
        Assert.Equal(["call", "retry", "call", "retry", "call"], lines.Select(l => l["type"]!.GetValue<string>()));
        Assert.Equal(status, lines[0]["error"]!["status"]?.GetValue<int>() ?? status); // Anthropic errors carry no ClientResultException status
        Assert.All(lines.Where(l => l["type"]!.GetValue<string>() == "retry"), l => Assert.Equal(status.ToString(), l["reason"]!.GetValue<string>()));
        Assert.Null(lines[^1]["error"]);
    }

    [Theory]
    [InlineData(1500, 1500)]     // longer than the 1 s backoff: honoured
    [InlineData(200, 1000)]      // shorter: the backoff wins
    [InlineData(60000, 60000)]   // up to 60 s (a longer one is a quota, not retried; see below)
    public async Task A_retry_after_header_on_429_is_honoured_when_longer_than_the_backoff(int headerMs, int expectedMs)
    {
        using var tmp = new TempRun();
        using var server = new FakeLlmServer(rateLimitFirst: 1) { RetryAfterMs = headerMs };
        var waits = new List<TimeSpan>();
        var context = new LlmCallContext { Run = tmp.Run, Stage = "test" };
        using var client = Client(server, new LlmPipelineOptions(CallContext: context, RetryDelay: (t, _) => { waits.Add(t); return Task.CompletedTask; }));

        await client.GetResponseAsync("hello", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal([TimeSpan.FromMilliseconds(expectedMs)], waits);
        var retry = tmp.Calls().Single(l => l["type"]!.GetValue<string>() == "retry");
        Assert.Equal(headerMs, retry["retryAfterMs"]!.GetValue<double>());
    }

    [Fact]
    public async Task A_429_asking_for_more_than_a_minute_is_a_quota_and_is_not_retried()
    {
        using var server = new FakeLlmServer(rateLimitFirst: 1) { RetryAfterMs = 600000 };
        var waits = new List<TimeSpan>();
        using var client = Client(server, new LlmPipelineOptions(RetryDelay: (t, _) => { waits.Add(t); return Task.CompletedTask; }));

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => client.GetResponseAsync("hello", cancellationToken: TestContext.Current.CancellationToken));

        Assert.Empty(waits);
        Assert.Equal(ProviderFailure.Quota, ProviderFailures.Classify(ex)?.Reason);
    }

    [Fact]
    public async Task Server_errors_give_up_after_the_configured_retries()
    {
        using var server = new FakeLlmServer(errorFirst: 10, errorStatus: 503);
        using var client = Client(server, new LlmPipelineOptions(Retries: 3, RetryDelay: (_, _) => Task.CompletedTask));

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => client.GetResponseAsync("hello", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(503, RetryOnTransientChatClient.TransientStatus(ex));
        Assert.Equal(4, server.Requests.Count); // the call plus 3 retries
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(404)]
    public async Task Client_errors_are_not_retried(int status)
    {
        using var server = new FakeLlmServer(errorFirst: 1, errorStatus: status);
        using var client = Client(server, new LlmPipelineOptions(RetryDelay: (_, _) => Task.CompletedTask));

        await Assert.ThrowsAnyAsync<Exception>(() => client.GetResponseAsync("hello", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Single(server.Requests);
    }

    // A request that runs past the SDK's network timeout is cancelled with TaskCanceledException; retry it.
    private sealed class TimeoutThenOk(int timeouts) : IChatClient
    {
        public int Calls { get; private set; }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (Calls <= timeouts) throw new TaskCanceledException("The operation was cancelled because it exceeded the configured timeout.");
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [Fact]
    public async Task A_network_timeout_is_retried_but_a_cancelled_call_is_not()
    {
        var inner = new TimeoutThenOk(2);
        using var client = new RetryOnTransientChatClient(inner, null, maxRetries: 3, delay: (_, _) => Task.CompletedTask);
        var reply = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")], cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("ok", reply.Text);
        Assert.Equal(3, inner.Calls);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var cancelled = new TimeoutThenOk(1);
        using var client2 = new RetryOnTransientChatClient(cancelled, null, maxRetries: 3, delay: (_, _) => Task.CompletedTask);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client2.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")], cancellationToken: cts.Token));
        Assert.Equal(1, cancelled.Calls);
        Assert.True(RetryOnTransientChatClient.IsNetworkTimeout(new TaskCanceledException(), CancellationToken.None));
        Assert.False(RetryOnTransientChatClient.IsNetworkTimeout(new TaskCanceledException(), cts.Token));
    }
}
