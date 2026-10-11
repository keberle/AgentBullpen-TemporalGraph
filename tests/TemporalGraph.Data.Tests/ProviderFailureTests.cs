using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using TemporalGraph.Cli.Runs;
using TemporalGraph.Llm;
using TemporalGraph.Testing;

namespace TemporalGraph.Data.Tests;

// The shared provider-failure classifier on recorded response shapes (tests/fixtures/provider-failures.json),
// directly and through both SDKs against FakeLlmServer; the fix-it text; and the gateway's reading of the proxy's problem details.
public sealed class ProviderFailureTests
{
    private static JsonNode Fixtures() =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(RepoPaths.FindRoot(), "tests", "fixtures", "provider-failures.json")))!;

    public static TheoryData<string> Cases()
    {
        var data = new TheoryData<string>();
        foreach (var c in Fixtures()["cases"]!.AsArray()) data.Add(c!["name"]!.GetValue<string>());
        return data;
    }

    private static JsonNode Case(string name) => Fixtures()["cases"]!.AsArray().Single(c => c!["name"]!.GetValue<string>() == name)!;

    [Theory]
    [MemberData(nameof(Cases))]
    public void Each_recorded_response_classifies_as_recorded(string name)
    {
        var c = Case(name);
        var retryAfter = c["retryAfterSeconds"]?.GetValue<int>() is { } s ? TimeSpan.FromSeconds(s) : (TimeSpan?)null;

        var failure = ProviderFailures.Classify(c["status"]!.GetValue<int>(), c["body"]!.ToJsonString(), retryAfter);

        Assert.Equal(c["reason"]?.GetValue<string>(), failure?.Reason);
    }

    [Theory]
    [InlineData("openrouter-402-credits")]
    [InlineData("openrouter-402-key-limit")]
    [InlineData("azure-429-long-wait-in-message-only")]
    [InlineData("openai-401-bad-key")]
    [InlineData("openai-500")]
    public async Task Through_the_OpenAI_SDK_the_exception_classifies_the_same(string name)
    {
        var c = Case(name);
        using var server = new FakeLlmServer(errorFirst: 1, errorStatus: c["status"]!.GetValue<int>()) { ErrorJson = c["body"]!.ToJsonString() };
        using var client = ChatClientFactory.Create(new LlmSettings("OpenAI", "m", server.BaseUrl + "/v1/", "fake-key"), maxRetries: 0,
            new LlmPipelineOptions(Retries: 0));

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => client.GetResponseAsync("hello", cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(c["reason"]?.GetValue<string>(), ProviderFailures.Classify(ex)?.Reason);
    }

    [Theory]
    [InlineData("anthropic-400-credit-balance")]
    [InlineData("anthropic-401-bad-key")]
    [InlineData("anthropic-400-other")]
    public async Task Through_the_Anthropic_SDK_the_exception_classifies_the_same(string name)
    {
        var c = Case(name);
        using var server = new FakeLlmServer(errorFirst: 1, errorStatus: c["status"]!.GetValue<int>()) { ErrorJson = c["body"]!.ToJsonString() };
        using var client = ChatClientFactory.Create(new LlmSettings("Anthropic", "m", null, "fake-key"), maxRetries: 0, anthropicBaseUrl: server.BaseUrl,
            new LlmPipelineOptions(Retries: 0));

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => client.GetResponseAsync("hello", cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(c["reason"]?.GetValue<string>(), ProviderFailures.Classify(ex)?.Reason);
    }

    [Fact]
    public async Task An_account_failure_is_not_retried_but_a_server_error_is()
    {
        var c = Case("openrouter-402-credits");
        using var credits = new FakeLlmServer(errorFirst: 5, errorStatus: 402) { ErrorJson = c["body"]!.ToJsonString() };
        using var client = ChatClientFactory.Create(new LlmSettings("OpenAI", "m", credits.BaseUrl + "/v1/", "fake-key"), maxRetries: 0,
            new LlmPipelineOptions(Retries: 3, RetryDelay: (_, _) => Task.CompletedTask));
        await Assert.ThrowsAnyAsync<Exception>(() => client.GetResponseAsync("hello", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Single(credits.Requests);

        var quota = Case("azure-429-quota-long-wait");
        using var azure = new FakeLlmServer(errorFirst: 5, errorStatus: 429) { ErrorJson = quota["body"]!.ToJsonString() };
        azure.ErrorHeaders["retry-after"] = "86400";
        using var azureClient = ChatClientFactory.Create(new LlmSettings("OpenAI", "m", azure.BaseUrl + "/v1/", "fake-key"), maxRetries: 0,
            new LlmPipelineOptions(Retries: 3, RetryDelay: (_, _) => Task.CompletedTask));
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => azureClient.GetResponseAsync("hello", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Single(azure.Requests);
        Assert.Equal(TimeSpan.FromSeconds(86400), ProviderFailures.Classify(ex)?.RetryAfter);
    }

    [Theory]
    [InlineData("openrouter", ProviderFailure.Credits, "Your OpenRouter account is out of credits — top it up, or run `setup` to switch the chat to another model.")]
    [InlineData("openrouter", ProviderFailure.KeyLimit, "Your OpenRouter key has reached its spending limit — raise the key's limit, or run `setup` to switch the chat to another model.")]
    [InlineData("azure", ProviderFailure.Quota, "Your Azure OpenAI quota is used up for now — wait for it to reset or raise it, or run `setup` to switch the chat to another model.")]
    [InlineData("anthropic", ProviderFailure.Auth, "Your Anthropic API key wasn't accepted — run `setup` to enter a new key, or switch the chat to another model.")]
    [InlineData("my-proxy.example", ProviderFailure.Credits, "Your model provider account is out of credits — top it up, or run `setup` to switch the chat to another model.")]
    public void The_fix_it_text_names_the_provider_and_what_to_do(string provider, string reason, string expected) =>
        Assert.Equal(expected, ProviderFailures.FixIt(ProviderFailures.Label(provider), reason));

    [Fact]
    public void The_gateway_reads_the_proxys_problem_type_and_passes_only_reader_safe_extensions()
    {
        var body = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["type"] = "https://agentbullpen.ai/problems/provider-unavailable",
            ["title"] = "Provider unavailable",
            ["status"] = 503,
            ["detail"] = "OpenRouter account acct_123 is out of credits",
            ["provider"] = "openrouter",
            ["reason"] = "credits",
            ["fallbackModel"] = "gpt-4o-mini",
        });

        var (type, _, extensions) = GatewayRefusalChatClient.Problem(body);

        Assert.Equal("provider-unavailable", type);
        Assert.Equal("credits", extensions["reason"]);
        Assert.Equal("gpt-4o-mini", extensions["fallbackModel"]);
        Assert.False(extensions.ContainsKey("provider"));
        Assert.False(extensions.ContainsKey("detail"));
        Assert.Null(GatewayRefusalChatClient.Problem("""{"type":"about:blank","title":"x"}""").Type);   // not one of ours
    }
}
