using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace TemporalGraph.Api.Tests;

// Gateway mode (Auth:Gateway:SigningKey set) with FakeLlmServer as the site's LLM proxy, and the standalone model picker
// (Chat:Models). Tokens are minted here the way the site's GatewaySetup.MintToken does (HS256, sub/email/name, 30 minutes).
public sealed class GatewayModeTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private const string Hound = "hound-of-the-baskervilles";
    private const string SigningKey = "test-signing-key-for-tg45-0123456789abcdef";   // test only; HS256 needs 32+ bytes
    private const string Member = "3f1c2a9e-0000-4000-8000-000000000001";
    private const string OtherMember = "3f1c2a9e-0000-4000-8000-000000000002";

    private const string ModelsJson = """
        { "providers": [
          { "name": "openrouter", "protocol": "openai", "models": [ { "id": "openai/gpt-4o-mini", "kind": "chat" }, { "id": "anthropic/claude-haiku-5.5", "kind": "chat" } ] },
          { "name": "openai", "protocol": "openai", "models": [ { "id": "gpt-image-2", "kind": "image" } ] } ] }
        """;

    private const string ChatMutation = """
        mutation($input: ChatInput!) {
          chat(input: $input) { sessionId answer model usage { inputTokens outputTokens costUsd } budget { level dailyUsd remainingUsd unlimited resetsAtUtc } }
        }
        """;

    private WebApplicationFactory<Program> Gateway(FakeLlmServer proxy, params (string Key, string Value)[] settings) =>
        factory.WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Testing");
            b.UseSetting("Auth:Gateway:SigningKey", SigningKey);
            b.UseSetting("Gateway:LlmBaseUrl", proxy.BaseUrl + "/llm/");
            b.UseSetting("Llm:Model", "openai/gpt-4o-mini");   // the default model, matched against the site's list
            foreach (var (k, v) in settings) b.UseSetting(k, v);
        });

    private static string Token(string sub = Member, string audience = "temporal", string issuer = "https://api.agentbullpen.ai", string key = SigningKey,
        TimeSpan? age = null)
    {
        var issued = DateTime.UtcNow - (age ?? TimeSpan.Zero);
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Claims = new Dictionary<string, object> { ["sub"] = sub, ["email"] = "reader@example.test", ["name"] = "Test Reader" },
            IssuedAt = issued,
            NotBefore = issued,
            Expires = issued + TimeSpan.FromMinutes(30),
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)), SecurityAlgorithms.HmacSha256),
        });
    }

    // The allowance headers, as the site's gateway puts them on each request it forwards.
    private static async Task<(HttpStatusCode Status, JsonNode? Body)> Send(HttpClient client, string query, object? variables = null, string? token = null,
        string? level = null, string? daily = null, string? remaining = null, string? resetsAt = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/graphql") { Content = JsonContent.Create(new { query, variables }) };
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (level is not null) request.Headers.Add("X-Budget-Level", level);
        if (daily is not null) request.Headers.Add("X-Budget-Daily-Usd", daily);
        if (remaining is not null) request.Headers.Add("X-Budget-Remaining-Usd", remaining);
        if (resetsAt is not null) request.Headers.Add("X-Budget-Resets-At", resetsAt);
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return (response.StatusCode, text.Length > 0 && response.StatusCode == HttpStatusCode.OK ? JsonNode.Parse(text) : null);
    }

    private static object Input(string message, string? sessionId = null, string? model = null) =>
        new { input = new { bookId = Hound, chapter = 3, character = "Sherlock Holmes", spoilers = false, sessionId, message, model } };

    private static string Code(JsonNode r) => r["errors"]![0]!["extensions"]!["code"]!.GetValue<string>();

    [Fact]
    public async Task Only_a_valid_gateway_token_gets_in()
    {
        using var proxy = new FakeLlmServer();
        using var app = Gateway(proxy);
        var client = app.CreateClient();
        const string books = "{ books { id } }";

        Assert.Equal(HttpStatusCode.OK, (await Send(client, books, token: Token())).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(client, books)).Status);                                         // none
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(client, books, token: Token(audience: "covers"))).Status);       // wrong audience
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(client, books, token: Token(age: TimeSpan.FromMinutes(31)))).Status);   // expired
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(client, books, token: Token(issuer: "https://elsewhere.example"))).Status);   // wrong issuer
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(client, books, token: Token(key: SigningKey + "-other"))).Status);   // bad signature
        Assert.Empty(proxy.Requests);
    }

    [Fact]
    public async Task Chat_goes_through_the_proxy_with_each_requests_token_and_the_conversation_id()
    {
        using var proxy = new FakeLlmServer { ModelsJson = ModelsJson };
        proxy.OpenAIMessages.Enqueue(FakeLlmServer.Text("Answer one."));
        proxy.OpenAIMessages.Enqueue(FakeLlmServer.Text("Answer two."));
        proxy.ResponseHeaders["X-Call-Cost-Usd"] = "0.000412";
        using var app = Gateway(proxy);
        var client = app.CreateClient();

        // The site's chat models (images left out), the default first by Llm:Model.
        var listing = Token();
        var (_, models) = await Send(client, "{ chatAvailable chatModels { id displayName provider isDefault } }", token: listing);
        Assert.True(models!["data"]!["chatAvailable"]!.GetValue<bool>());
        var list = models["data"]!["chatModels"]!.AsArray();
        Assert.Equal(["openrouter:openai/gpt-4o-mini", "openrouter:anthropic/claude-haiku-5.5"], list.Select(m => m!["id"]!.GetValue<string>()));
        Assert.Equal([true, false], list.Select(m => m!["isDefault"]!.GetValue<bool>()));

        var first = Token();
        var (_, one) = await Send(client, ChatMutation, Input("Who are you?"), first, level: "standard", daily: "1.00", remaining: "0.9812", resetsAt: "2026-10-11T00:00:00Z");
        Assert.Null(one!["errors"]);
        var chat = one["data"]!["chat"]!;
        var session = chat["sessionId"]!.GetValue<string>();
        Assert.Equal("Answer one.", chat["answer"]!.GetValue<string>());
        // The turn's tokens and its cost (the site's X-Call-Cost-Usd), and the allowance headers less that cost.
        Assert.Equal(11, chat["usage"]!["inputTokens"]!.GetValue<int>());
        Assert.Equal(1, chat["usage"]!["outputTokens"]!.GetValue<int>());
        Assert.Equal(0.000412, chat["usage"]!["costUsd"]!.GetValue<double>(), 9);
        Assert.Equal("standard", chat["budget"]!["level"]!.GetValue<string>());
        Assert.Equal(1.00, chat["budget"]!["dailyUsd"]!.GetValue<double>(), 9);
        Assert.Equal(0.980788, chat["budget"]!["remainingUsd"]!.GetValue<double>(), 9);
        Assert.False(chat["budget"]!["unlimited"]!.GetValue<bool>());
        Assert.Equal(DateTimeOffset.Parse("2026-10-11T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
            DateTimeOffset.Parse(chat["budget"]!["resetsAtUtc"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture));

        // A later request of the same session carries that request's token.
        var second = Token(age: TimeSpan.FromMinutes(1));
        var (_, two) = await Send(client, ChatMutation, Input("And now?", session), second);
        Assert.Equal(session, two!["data"]!["chat"]!["sessionId"]!.GetValue<string>());

        var calls = proxy.Requests.Where(r => r.Path.EndsWith("/chat/completions")).ToList();
        Assert.Equal(2, calls.Count);
        Assert.All(calls, r => Assert.Equal("/llm/openrouter/v1/chat/completions", r.Path));
        Assert.Equal(["Bearer " + first, "Bearer " + second], calls.Select(r => r.Authorization));
        Assert.All(calls, r => Assert.Equal(session, r.ConversationId));
        Assert.Contains("\"model\":\"openai/gpt-4o-mini\"", calls[0].Body.Replace(" ", ""));
        // The model list was asked with the token of the request that listed it (compared with that token itself; a token
        // made a second later differs, which failed this test whenever the clock ticked between the two).
        Assert.Equal("Bearer " + listing, proxy.Requests.First(r => r.Path == "/llm/models").Authorization);
    }

    [Fact]
    public async Task Another_members_session_id_starts_a_new_session()
    {
        using var proxy = new FakeLlmServer { ModelsJson = ModelsJson };
        using var app = Gateway(proxy);
        var client = app.CreateClient();

        var (_, mine) = await Send(client, ChatMutation, Input("Who are you?"), Token());
        var session = mine!["data"]!["chat"]!["sessionId"]!.GetValue<string>();
        var (_, theirs) = await Send(client, ChatMutation, Input("And you?", session), Token(sub: OtherMember));
        var (_, again) = await Send(client, ChatMutation, Input("Still me.", session), Token());

        Assert.NotEqual(session, theirs!["data"]!["chat"]!["sessionId"]!.GetValue<string>());
        Assert.Equal(session, again!["data"]!["chat"]!["sessionId"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_replys_cost_is_the_sum_over_every_model_call_of_the_turn()
    {
        // A tool-call round trip: two calls through the proxy, each with its own X-Call-Cost-Usd.
        using var proxy = new FakeLlmServer { ModelsJson = ModelsJson };
        proxy.OpenAIMessages.Enqueue(FakeLlmServer.ToolCall("call_1", "facts_about", new { character = "Holmes" }));
        proxy.OpenAIMessages.Enqueue(FakeLlmServer.Text("I am a consulting detective."));
        proxy.OpenAIMessages.Enqueue(FakeLlmServer.Text("As I said."));
        proxy.ResponseHeaderSequence.Enqueue(new() { ["X-Call-Cost-Usd"] = "0.000605" });
        proxy.ResponseHeaderSequence.Enqueue(new() { ["X-Call-Cost-Usd"] = "0.000031" });
        proxy.ResponseHeaderSequence.Enqueue(new() { ["X-Call-Cost-Usd"] = "0.000100" });
        using var app = Gateway(proxy);
        var client = app.CreateClient();

        var (_, one) = await Send(client, ChatMutation, Input("Who are you?"), Token(), level: "light", daily: "0.50", remaining: "0.0005");
        var chat = one!["data"]!["chat"]!;
        Assert.Equal(22, chat["usage"]!["inputTokens"]!.GetValue<int>());   // the turn's totals: two calls of 11 in, 1 out
        Assert.Equal(2, chat["usage"]!["outputTokens"]!.GetValue<int>());
        Assert.Equal(0.000636, chat["usage"]!["costUsd"]!.GetValue<double>(), 9);
        Assert.Equal(0, chat["budget"]!["remainingUsd"]!.GetValue<double>(), 9);   // never below 0

        // The next turn's cost is its own, not a running total.
        var (_, two) = await Send(client, ChatMutation, Input("Again?", chat["sessionId"]!.GetValue<string>()), Token(), level: "light", daily: "0.50", remaining: "0.4000");
        Assert.Equal(0.0001, two!["data"]!["chat"]!["usage"]!["costUsd"]!.GetValue<double>(), 9);
        Assert.Equal(0.3999, two["data"]!["chat"]!["budget"]!["remainingUsd"]!.GetValue<double>(), 9);
    }

    [Fact]
    public async Task A_turn_with_a_call_the_site_put_no_cost_on_has_no_cost_and_leaves_the_balance_as_the_header_said()
    {
        using var proxy = new FakeLlmServer { ModelsJson = ModelsJson };
        proxy.OpenAIMessages.Enqueue(FakeLlmServer.ToolCall("call_1", "facts_about", new { character = "Holmes" }));
        proxy.OpenAIMessages.Enqueue(FakeLlmServer.Text("I am a consulting detective."));
        proxy.ResponseHeaderSequence.Enqueue(new() { ["X-Call-Cost-Usd"] = "0.000605" });   // the second call: none
        using var app = Gateway(proxy);

        var (_, r) = await Send(app.CreateClient(), ChatMutation, Input("Who are you?"), Token(), level: "standard", daily: "1.00", remaining: "0.5000");

        var chat = r!["data"]!["chat"]!;
        Assert.Equal(22, chat["usage"]!["inputTokens"]!.GetValue<int>());
        Assert.Null(chat["usage"]!["costUsd"]);   // not a part of the total, and never an estimate
        Assert.Equal(0.5, chat["budget"]!["remainingUsd"]!.GetValue<double>(), 9);
    }

    [Fact]
    public void A_turns_cost_is_known_only_when_every_call_had_one()
    {
        var context = new TemporalGraph.Llm.LlmCallContext { Run = TemporalGraph.Llm.NoCallLog.Instance, Stage = "chat" };
        Assert.Null(context.CostUsd);   // no call yet
        context.AddCallCost(0.000605m);
        context.AddCallCost(0.000031m);
        Assert.Equal(0.000636m, context.CostUsd);
        context.AddCallCost(null);
        Assert.Null(context.CostUsd);
        context.ResetCost();
        context.AddCallCost(0m);
        Assert.Equal(0m, context.CostUsd);   // a free call is a known cost
    }

    [Theory]
    [InlineData("""{"usage":{"prompt_tokens":5020,"completion_tokens":42,"cost":0.000605}}""", "0.000605")]
    [InlineData("""{"usage":{"prompt_tokens":5020,"completion_tokens":42,"cost":2.7e-06}}""", "0.0000027")]
    [InlineData("""{"usage":{"prompt_tokens":5020,"completion_tokens":42}}""", null)]          // OpenAI, Azure: no cost
    [InlineData("""{"usage":{"cost":"0.01"}}""", null)]                                          // not a number
    [InlineData("""{"usage":{"cost":-1}}""", null)]
    [InlineData("data: [DONE]", null)]
    [InlineData("", null)]
    public void The_providers_own_cost_is_read_from_usage_cost_and_nothing_else(string body, string? usd) =>
        Assert.Equal(usd is null ? null : decimal.Parse(usd, System.Globalization.CultureInfo.InvariantCulture), TemporalGraph.Llm.ProviderCostHandler.Cost(body));

    [Theory]
    [InlineData(403, """{"title":"Daily token budget exhausted for member 42","status":403}""", "0.0000", "BUDGET_EXHAUSTED")]
    [InlineData(403, """{"title":"Model not allowed","status":403}""", "0.4000", "UNKNOWN_MODEL")]
    [InlineData(401, "", null, "UNAUTHORIZED")]
    [InlineData(429, "", null, "RATE_LIMITED")]
    public async Task Proxy_refusals_are_error_codes_without_the_proxys_text(int status, string body, string? remaining, string code)
    {
        using var proxy = new FakeLlmServer(errorFirst: 1, errorStatus: status) { ModelsJson = ModelsJson, ErrorJson = body };
        if (remaining is not null) proxy.ErrorHeaders["X-Budget-Remaining-Usd"] = remaining;
        using var app = Gateway(proxy);

        var (http, r) = await Send(app.CreateClient(), ChatMutation, Input("Who are you?"), Token());

        Assert.Equal(HttpStatusCode.OK, http);
        Assert.Equal(code, Code(r!));
        Assert.DoesNotContain("localhost", r!.ToJsonString());
        Assert.DoesNotContain("member 42", r.ToJsonString());
        Assert.Single(proxy.Requests, p => p.Path.EndsWith("/chat/completions"));   // never retried
    }

    [Fact]
    public async Task Budget_comes_from_the_gateway_headers_and_is_null_without_them()
    {
        using var proxy = new FakeLlmServer();
        using var app = Gateway(proxy);
        var client = app.CreateClient();
        const string q = "{ budget { level dailyUsd remainingUsd unlimited resetsAtUtc } }";

        var (_, with) = await Send(client, q, token: Token(), level: "standard", daily: "1.00", remaining: "0.9812", resetsAt: "2026-10-11T00:00:00Z");
        var (_, without) = await Send(client, q, token: Token());

        var budget = with!["data"]!["budget"]!;
        Assert.Equal("standard", budget["level"]!.GetValue<string>());
        Assert.Equal(1.00, budget["dailyUsd"]!.GetValue<double>(), 9);
        Assert.Equal(0.9812, budget["remainingUsd"]!.GetValue<double>(), 9);
        Assert.False(budget["unlimited"]!.GetValue<bool>());
        Assert.StartsWith("2026-10-11T00:00:00", budget["resetsAtUtc"]!.GetValue<string>());
        Assert.Null(without!["data"]!["budget"]);
    }

    [Fact]
    public async Task The_levels_off_and_none_and_a_missing_reset_time()
    {
        using var proxy = new FakeLlmServer();
        using var app = Gateway(proxy);
        var client = app.CreateClient();
        const string q = "{ budget { level dailyUsd remainingUsd unlimited resetsAtUtc } }";

        // off: no daily header, remaining "0". Chat is off; nothing resets it.
        var (_, off) = await Send(client, q, token: Token(), level: "off", remaining: "0", resetsAt: "2026-10-11T00:00:00Z");
        var b = off!["data"]!["budget"]!;
        Assert.Equal("off", b["level"]!.GetValue<string>());
        Assert.Null(b["dailyUsd"]);
        Assert.Equal(0, b["remainingUsd"]!.GetValue<double>());
        Assert.False(b["unlimited"]!.GetValue<bool>());
        Assert.Null(b["resetsAtUtc"]);

        // none: no daily and no remaining header. No limit.
        var (_, none) = await Send(client, q, token: Token(), level: "none", resetsAt: "2026-10-11T00:00:00Z");
        b = none!["data"]!["budget"]!;
        Assert.Equal("none", b["level"]!.GetValue<string>());
        Assert.True(b["unlimited"]!.GetValue<bool>());
        Assert.Null(b["dailyUsd"]);
        Assert.Null(b["remainingUsd"]);

        // A custom amount, and no reset header: the next UTC midnight.
        var (_, custom) = await Send(client, q, token: Token(), level: "custom", daily: "0.01", remaining: "0.0000");
        b = custom!["data"]!["budget"]!;
        Assert.Equal(0.01, b["dailyUsd"]!.GetValue<double>(), 9);
        Assert.Equal(0, b["remainingUsd"]!.GetValue<double>());
        var resets = DateTimeOffset.Parse(b["resetsAtUtc"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(TimeSpan.Zero, resets.TimeOfDay);
        Assert.InRange(resets - DateTimeOffset.UtcNow, TimeSpan.Zero, TimeSpan.FromDays(1));
    }

    [Fact]
    public async Task A_model_the_site_doesnt_list_is_unknown_model()
    {
        using var proxy = new FakeLlmServer { ModelsJson = ModelsJson };
        using var app = Gateway(proxy);

        var (_, r) = await Send(app.CreateClient(), ChatMutation, Input("Who are you?", model: "openai:gpt-6-sol"), Token());

        Assert.Equal("UNKNOWN_MODEL", Code(r!));
        Assert.DoesNotContain(proxy.Requests, p => p.Path.EndsWith("/chat/completions"));
    }

    // ── Half-filled settings: the API refuses to start ────────────────────────────────────────────────────────────────────

    private static IConfiguration Settings(params (string Key, string? Value)[] settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value))).Build();

    [Theory]
    [InlineData("<the site's gateway-signing-key>")]
    [InlineData("  <set me>  ")]
    public void A_signing_key_left_as_a_placeholder_refuses_to_start(string key)
    {
        var ex = Assert.Throws<GatewayConfigurationException>(() => GatewayOptions.From(Settings(("Auth:Gateway:SigningKey", key))));

        Assert.Contains("Auth:Gateway:SigningKey", ex.Message);
        Assert.Contains("placeholder", ex.Message);
    }

    [Theory]
    [InlineData("Auth:Gateway:Issuer", "https://api.agentbullpen.ai")]
    [InlineData("Auth:Gateway:Audience", "temporal")]
    [InlineData("Gateway:LlmBaseUrl", "https://api.agentbullpen.ai/llm")]
    public void An_empty_key_beside_another_gateway_setting_refuses_to_start(string setting, string value)
    {
        foreach (var key in new[] { null, "", "   " })
        {
            var ex = Assert.Throws<GatewayConfigurationException>(() => GatewayOptions.From(Settings(("Auth:Gateway:SigningKey", key), (setting, value))));
            Assert.Contains(setting, ex.Message);
            Assert.Contains("Auth:Gateway:SigningKey", ex.Message);
        }
    }

    [Fact]
    public void All_four_unset_is_standalone_and_the_shipped_settings_are_that()
    {
        foreach (var settings in new[]
                 {
                     Settings(),
                     Settings(("Auth:Gateway:SigningKey", ""), ("Auth:Gateway:Issuer", ""), ("Auth:Gateway:Audience", " "), ("Gateway:LlmBaseUrl", "<gateway mode only: the site's LLM proxy>")),
                     new ConfigurationBuilder().AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json")).Build(),
                 })
        {
            var options = GatewayOptions.From(settings);
            Assert.False(options.Enabled);
            Assert.Equal((GatewayOptions.DefaultIssuer, GatewayOptions.DefaultAudience, GatewayOptions.DefaultLlmBaseUrl), (options.Issuer, options.Audience, options.LlmBaseUrl));
        }
    }

    [Fact]
    public void A_key_with_the_other_three_is_gateway_mode()
    {
        var options = GatewayOptions.From(Settings(("Auth:Gateway:SigningKey", SigningKey), ("Auth:Gateway:Issuer", "https://site.test"),
            ("Auth:Gateway:Audience", "other"), ("Gateway:LlmBaseUrl", "http://localhost:5009/llm/")));

        Assert.True(options.Enabled);
        Assert.Equal(("https://site.test", "other", "http://localhost:5009/llm"), (options.Issuer, options.Audience, options.LlmBaseUrl));
    }

    // The whole API, not only the options: neither half-filled case gets as far as serving a request.
    [Theory]
    [InlineData("Auth:Gateway:SigningKey", "<the site's gateway-signing-key>")]
    [InlineData("Gateway:LlmBaseUrl", "https://api.agentbullpen.ai/llm")]
    public void The_api_does_not_start_on_half_filled_gateway_settings(string setting, string value)
    {
        using var app = factory.WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Production");
            b.UseSetting(setting, value);
        });

        Assert.ThrowsAny<Exception>(() => app.CreateClient());
    }

    // ── Standalone ──────────────────────────────────────────────────────────────────────────────────────────────────────────

    private WebApplicationFactory<Program> Standalone(FakeLlmServer server, params (string Key, string Value)[] settings) =>
        factory.WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Testing");
            b.UseSetting("Llm:Type", "OpenAI");
            b.UseSetting("Llm:Model", "my-deployment");
            b.UseSetting("Llm:Endpoint", server.BaseUrl + "/openai/v1/");
            b.UseSetting("Llm:ApiKey", "fake-key-for-tests-0123456789");
            foreach (var (k, v) in settings) b.UseSetting(k, v);
        });

    [Fact]
    public async Task Standalone_needs_no_token_and_its_budget_is_unlimited()
    {
        using var server = new FakeLlmServer();
        using var app = Standalone(server);
        var client = app.CreateClient();

        var (status, r) = await Send(client, "{ budget { level dailyUsd remainingUsd unlimited resetsAtUtc } chatModels { id displayName provider isDefault } }");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(r!["data"]!["budget"]!["unlimited"]!.GetValue<bool>());
        Assert.Null(r["data"]!["budget"]!["level"]);
        Assert.Null(r["data"]!["budget"]!["dailyUsd"]);
        Assert.Null(r["data"]!["budget"]!["remainingUsd"]);
        Assert.Null(r["data"]!["budget"]!["resetsAtUtc"]);
        var model = Assert.Single(r["data"]!["chatModels"]!.AsArray())!;   // one model: the web hides the picker
        Assert.Equal("default", model["id"]!.GetValue<string>());
        Assert.Equal("my-deployment", model["displayName"]!.GetValue<string>());
        Assert.True(model["isDefault"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Standalone_a_reply_has_its_tokens_and_the_providers_own_cost_when_the_provider_sends_one()
    {
        // OpenRouter puts usage.cost on every completion: summed over the turn's calls (a tool-call round trip: two).
        using var router = new FakeLlmServer { UsageCost = "0.000605" };
        router.OpenAIMessages.Enqueue(FakeLlmServer.ToolCall("call_1", "facts_about", new { character = "Holmes" }));
        router.OpenAIMessages.Enqueue(FakeLlmServer.Text("I am a consulting detective."));
        using var app = Standalone(router);

        var (_, r) = await Send(app.CreateClient(), ChatMutation, Input("Who are you?"));

        var chat = r!["data"]!["chat"]!;
        Assert.Equal("I am a consulting detective.", chat["answer"]!.GetValue<string>());
        Assert.Equal(22, chat["usage"]!["inputTokens"]!.GetValue<int>());
        Assert.Equal(2, chat["usage"]!["outputTokens"]!.GetValue<int>());
        Assert.Equal(0.00121, chat["usage"]!["costUsd"]!.GetValue<double>(), 9);
        Assert.True(chat["budget"]!["unlimited"]!.GetValue<bool>());
        Assert.All(router.Requests, q => Assert.Equal("Bearer fake-key-for-tests-0123456789", q.Authorization));   // the profile's own key, as before
    }

    [Fact]
    public async Task Standalone_a_provider_that_sends_no_cost_gives_tokens_only_and_a_site_header_is_not_read()
    {
        using var server = new FakeLlmServer();
        server.ResponseHeaders["X-Call-Cost-Usd"] = "0.000412";   // standalone reads only the provider's own answer
        using var app = Standalone(server);

        var (_, r) = await Send(app.CreateClient(), ChatMutation, Input("Who are you?"));

        var chat = r!["data"]!["chat"]!;
        Assert.Equal(11, chat["usage"]!["inputTokens"]!.GetValue<int>());
        Assert.Equal(1, chat["usage"]!["outputTokens"]!.GetValue<int>());
        Assert.Null(chat["usage"]!["costUsd"]);
    }

    [Fact]
    public async Task Chat_models_lists_the_allowed_profiles_and_switching_starts_a_new_session()
    {
        using var server = new FakeLlmServer();
        using var app = Standalone(server, ("Chat:Models:0", "alt"), ("Chat:Models:1", "incomplete"),
            ("Llm:Profiles:alt:Type", "OpenAI"), ("Llm:Profiles:alt:Endpoint", server.BaseUrl + "/openai/v1/"), ("Llm:Profiles:alt:Model", "alt-deployment"),
            ("Llm:Profiles:alt:DisplayName", "Alt model"), ("Llm:Profiles:incomplete:Type", "OpenAI"));
        var client = app.CreateClient();

        var (_, models) = await Send(client, "{ chatModels { id displayName isDefault } }");
        Assert.Equal(["default", "alt"], models!["data"]!["chatModels"]!.AsArray().Select(m => m!["id"]!.GetValue<string>()));   // "incomplete" left out
        Assert.Equal("Alt model", models["data"]!["chatModels"]![1]!["displayName"]!.GetValue<string>());

        var (_, one) = await Send(client, ChatMutation, Input("Who are you?"));
        var session = one!["data"]!["chat"]!["sessionId"]!.GetValue<string>();
        Assert.True(one["data"]!["chat"]!["budget"]!["unlimited"]!.GetValue<bool>());
        var (_, same) = await Send(client, ChatMutation, Input("And now?", session, model: "default"));
        var (_, alt) = await Send(client, ChatMutation, Input("And with the other?", session, model: "alt"));
        var (_, unknown) = await Send(client, ChatMutation, Input("And this?", session, model: "nosuch"));

        Assert.Equal(session, same!["data"]!["chat"]!["sessionId"]!.GetValue<string>());
        Assert.NotEqual(session, alt!["data"]!["chat"]!["sessionId"]!.GetValue<string>());
        Assert.Contains("\"model\":\"alt-deployment\"", server.Requests[^1].Body.Replace(" ", ""));
        Assert.Equal("UNKNOWN_MODEL", Code(unknown!));
        Assert.All(server.Requests, r => Assert.Null(r.ConversationId));   // standalone sends no gateway headers
    }

    [Fact]
    public async Task Health_is_mapped_and_open_in_production_behind_the_gateway()
    {
        // The Container App's probe: no token, Production, gateway auth on.
        using var proxy = new FakeLlmServer();
        using var app = factory.WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Production");
            b.UseSetting("Auth:Gateway:SigningKey", SigningKey);
            b.UseSetting("Gateway:LlmBaseUrl", proxy.BaseUrl + "/llm/");
        });
        var response = await app.CreateClient().GetAsync("/health", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(app.CreateClient(), "{ books { id } }")).Status);
    }

    [Fact]
    public void The_sites_model_list_labels_a_model_by_its_display_name_else_its_id()
    {
        using var doc = System.Text.Json.JsonDocument.Parse("""
            { "providers": [ { "name": "openrouter", "protocol": "openai", "models": [
              { "id": "openai/gpt-4o-mini", "kind": "chat", "weight": 1, "displayName": "GPT-4o mini" },
              { "id": "anthropic/claude-haiku-5.5", "kind": "chat", "displayName": "" },
              { "id": "openai/gpt-6-luna", "kind": "chat" } ] } ] }
            """);
        var models = GatewayModelCatalog.Parse(doc.RootElement);

        Assert.Equal(["GPT-4o mini", "anthropic/claude-haiku-5.5", "openai/gpt-6-luna"], models.Select(m => m.DisplayName));
        Assert.Equal("openrouter:openai/gpt-4o-mini", models[0].Id);
    }
}
