using Microsoft.Extensions.AI;
using TemporalGraph.Llm;

namespace TemporalGraph.Data.Tests;

// Model calls in gateway mode, against FakeLlmServer standing in for the site's LLM proxy. No real network.
public class GatewayTransportTests
{
    private static IChatClient Client(FakeLlmServer server, Func<string?> token, bool anthropic = false, string? conversation = null)
    {
        var handler = new GatewayCredentialHandler(token, anthropic, conversation) { InnerHandler = new HttpClientHandler() };
        var settings = new LlmSettings(anthropic ? LlmSettings.Anthropic : LlmSettings.OpenAI, anthropic ? "claude-haiku-5-5" : "openai/gpt-4o-mini",
            anthropic ? null : server.BaseUrl + "/llm/openrouter/v1/", GatewayCredentialHandler.PlaceholderKey);
        return ChatClientFactory.Create(settings, maxRetries: 0, new LlmPipelineOptions(Retries: 3, RetryDelay: (_, _) => Task.CompletedTask),
            new LlmTransport(new HttpClient(handler), anthropic ? server.BaseUrl + "/llm/anthropic" : null));
    }

    [Fact]
    public async Task The_handler_runs_after_the_sdk_key_policy_so_each_request_carries_the_current_token()
    {
        using var server = new FakeLlmServer();
        var token = "token-one";
        using var client = Client(server, () => token, conversation: "session-1");

        await client.GetResponseAsync("Hi", cancellationToken: TestContext.Current.CancellationToken);
        token = "token-two";   // the next request of the same client (a later GraphQL request of the same chat session)
        await client.GetResponseAsync("Hi again", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(["/llm/openrouter/v1/chat/completions", "/llm/openrouter/v1/chat/completions"], server.Requests.Select(r => r.Path));
        Assert.Equal(["Bearer token-one", "Bearer token-two"], server.Requests.Select(r => r.Authorization));
        Assert.DoesNotContain(server.Requests, r => (r.Authorization ?? "").Contains(GatewayCredentialHandler.PlaceholderKey));
        Assert.All(server.Requests, r => Assert.Equal("session-1", r.ConversationId));
    }

    [Fact]
    public async Task Anthropic_gets_the_token_as_x_api_key_under_the_proxy_path()
    {
        using var server = new FakeLlmServer();
        using var client = Client(server, () => "token-a", anthropic: true);

        await client.GetResponseAsync("Hi", cancellationToken: TestContext.Current.CancellationToken);

        var request = Assert.Single(server.Requests);
        Assert.Equal("/llm/anthropic/v1/messages", request.Path);
        Assert.Equal("token-a", request.XApiKey);
        Assert.Null(request.Authorization);
    }

    [Fact]
    public async Task Without_a_token_the_call_fails_before_anything_is_sent()
    {
        using var server = new FakeLlmServer();
        using var client = Client(server, () => null);

        await Assert.ThrowsAsync<GatewayTokenMissingException>(() => client.GetResponseAsync("Hi", cancellationToken: TestContext.Current.CancellationToken));

        Assert.Empty(server.Requests);
    }

    [Theory]
    [InlineData(403, """{"title":"Daily token budget exhausted","status":403}""", "0", GatewayRefusedException.BudgetExhausted)]
    [InlineData(403, """{"title":"Model not allowed","status":403}""", "5000", GatewayRefusedException.ModelNotAllowed)]
    [InlineData(401, "", null, GatewayRefusedException.Unauthorized)]
    [InlineData(429, "", null, GatewayRefusedException.RateLimited)]
    public async Task Proxy_refusals_become_typed_errors_and_are_not_retried(int status, string body, string? remaining, string code)
    {
        using var server = new FakeLlmServer(errorFirst: 1, errorStatus: status) { ErrorJson = body };
        if (remaining is not null) server.ErrorHeaders["X-Budget-Remaining-Tokens"] = remaining;
        using var client = Client(server, () => "token");

        var ex = await Assert.ThrowsAsync<GatewayRefusedException>(() => client.GetResponseAsync("Hi", cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(code, ex.Code);
        Assert.Single(server.Requests);   // the retry client (3 retries) saw a refusal, not a 429
        Assert.DoesNotContain("localhost", ex.Message);
    }
}
