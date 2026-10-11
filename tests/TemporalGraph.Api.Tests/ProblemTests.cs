using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using HotChocolate;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using TemporalGraph.Api;

namespace TemporalGraph.Api.Tests;

// RFC 9457 problem details. REST errors are problem JSON with a traceId; every GraphQL error keeps extensions.code and carries
// extensions.problem with the shared type URI; standalone classifies provider failures itself (fix-it text); gateway mode passes the
// proxy's problem types through (never its provider or text); X-Model-Fallback and unavailable models reach the client; each problem
// is counted.
public sealed class ProblemTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private const string Hound = "hound-of-the-baskervilles";
    private const string FakeKey = "fake-key-for-tests-0123456789";
    private const string SigningKey = "test-signing-key-for-tg50-0123456789abcdef";
    private const string Problems = "https://agentbullpen.ai/problems/";

    private const string ChatMutation = """
        mutation($input: ChatInput!) { chat(input: $input) { sessionId answer model fallback { requested used } } }
        """;

    private const string ModelsJson = """
        { "providers": [
          { "name": "openrouter", "protocol": "openai", "models": [
            { "id": "anthropic/claude-haiku-5.5", "kind": "chat", "available": false, "unavailableUntilUtc": "2026-10-10T15:00:00Z" },
            { "id": "openai/gpt-4o-mini", "kind": "chat", "displayName": "GPT-4o mini" } ] } ] }
        """;

    private WebApplicationFactory<Program> Standalone(FakeLlmServer? server, params (string Key, string Value)[] settings) =>
        factory.WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Testing");
            if (server is not null)
            {
                b.UseSetting("Llm:Type", "OpenAI");
                b.UseSetting("Llm:Model", "my-deployment");
                b.UseSetting("Llm:Endpoint", server.BaseUrl + "/openai/v1/");
                b.UseSetting("Llm:ApiKey", FakeKey);
            }
            foreach (var (k, v) in settings) b.UseSetting(k, v);
        });

    private WebApplicationFactory<Program> Gateway(FakeLlmServer proxy) =>
        factory.WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Testing");
            b.UseSetting("Auth:Gateway:SigningKey", SigningKey);
            b.UseSetting("Gateway:LlmBaseUrl", proxy.BaseUrl + "/llm/");
            b.UseSetting("Llm:Model", "anthropic/claude-haiku-5.5");   // preferred, but listed as unavailable
        });

    private static string Token() => new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
    {
        Issuer = "https://api.agentbullpen.ai",
        Audience = "temporal",
        Claims = new Dictionary<string, object> { ["sub"] = "member-tg50" },
        Expires = DateTime.UtcNow.AddMinutes(30),
        SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey)), SecurityAlgorithms.HmacSha256),
    });

    private static async Task<JsonNode> Post(HttpClient client, string query, object? variables = null, string? token = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/graphql") { Content = JsonContent.Create(new { query, variables }) };
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);   // errors are GraphQL errors, never an HTTP error
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
    }

    private static object Input(string message, string character = "Sherlock Holmes", int chapter = 3, string? model = null) =>
        new { input = new { bookId = Hound, chapter, character, spoilers = false, message, model } };

    private static JsonNode Problem(JsonNode r) => r["errors"]![0]!["extensions"]!["problem"]!;
    private static string Code(JsonNode r) => r["errors"]![0]!["extensions"]!["code"]!.GetValue<string>();

    // ── REST ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_REST_error_is_problem_json_with_the_shared_type_and_a_trace_id()
    {
        using var proxy = new FakeLlmServer();
        using var gateway = Gateway(proxy);
        var unauthorized = await gateway.CreateClient().PostAsJsonAsync("/graphql", new { query = "{ books { id } }" }, TestContext.Current.CancellationToken);
        using var standalone = Standalone(null);
        var missing = await standalone.CreateClient().GetAsync("/no-such-endpoint", TestContext.Current.CancellationToken);

        foreach (var (response, status, slug) in new[] { (unauthorized, 401, "unauthorized"), (missing, 404, "not-found") })
        {
            Assert.Equal(status, (int)response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
            Assert.Equal(Problems + slug, body["type"]!.GetValue<string>());
            Assert.Equal(status, body["status"]!.GetValue<int>());
            Assert.False(string.IsNullOrEmpty(body["title"]?.GetValue<string>()));
            Assert.Matches("^[0-9a-f]{32}$", body["traceId"]!.GetValue<string>());
        }
    }

    // ── GraphQL: this app's own problems ─────────────────────────────────────────────────────────────────────────────────────

    public static TheoryData<string, string, string, int> LocalProblems() => new()
    {
        { "unknown-book", "UNKNOWN_BOOK", """{ chapters(bookId: "no-such-book") { number } }""", 404 },
        { "unknown-chapter", "UNKNOWN_CHAPTER", $$"""{ chatCharacters(bookId: "{{Hound}}", chapter: 999, spoilers: false) { name } }""", 404 },
        { "invalid-query", "SYNTAX_ERROR", $$"""{ cypher(bookId: "{{Hound}}", query: "MATCH (n RETURN n", chapter: 3, spoilers: false) { columns } }""", 400 },
        { "model-not-configured", "LLM_NOT_CONFIGURED", "chat", 503 },
    };

    [Theory]
    [MemberData(nameof(LocalProblems))]
    public async Task Every_graphql_error_carries_its_problem_and_keeps_its_code(string slug, string code, string query, int status)
    {
        using var app = Standalone(null);
        var r = query == "chat" ? await Post(app.CreateClient(), ChatMutation, Input("Who are you?")) : await Post(app.CreateClient(), query);

        var problem = Problem(r);
        Assert.Equal(Problems + slug, problem["type"]!.GetValue<string>());
        Assert.Equal(status, problem["status"]!.GetValue<int>());
        Assert.False(string.IsNullOrEmpty(problem["title"]?.GetValue<string>()));
        Assert.Matches("^[0-9a-f]{32}$", problem["traceId"]!.GetValue<string>());
        Assert.Equal(code, Code(r));
        if (slug == "invalid-query")
        {
            Assert.Equal(1, problem["line"]!.GetValue<int>());
            Assert.True(problem["column"]!.GetValue<int>() > 1);
        }
    }

    [Theory]
    [InlineData("unknown-character", "UNKNOWN_CHARACTER", "Professor Moriarty", "Who are you?")]
    [InlineData("invalid-request", "EMPTY_MESSAGE", "Sherlock Holmes", " ")]
    public async Task Chat_input_problems(string slug, string code, string character, string message)
    {
        using var server = new FakeLlmServer();
        using var app = Standalone(server);

        var r = await Post(app.CreateClient(), ChatMutation, Input(message, character));

        Assert.Equal(code, Code(r));
        Assert.Equal(Problems + slug, Problem(r)["type"]!.GetValue<string>());
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task A_turn_past_the_timeout_is_the_timeout_problem()
    {
        using var server = new FakeLlmServer { DelayMs = 3000 };
        using var app = Standalone(server, ("Chat:TimeoutSeconds", "1"));

        var r = await Post(app.CreateClient(), ChatMutation, Input("Who are you?"));

        Assert.Equal(Problems + "timeout", Problem(r)["type"]!.GetValue<string>());
        Assert.Equal(504, Problem(r)["status"]!.GetValue<int>());
    }

    [Fact]
    public void An_unexpected_exception_is_internal_error_with_only_the_trace_id()
    {
        var error = new ProblemErrorFilter().OnError(ErrorBuilder.New().SetMessage("Unexpected Execution Error")
            .SetException(new InvalidOperationException("secret connection string sk-123")).Build());

        Assert.Equal("INTERNAL_ERROR", error.Code);
        Assert.Null(error.Exception);
        Assert.DoesNotContain("secret", error.Message);
        var problem = (IReadOnlyDictionary<string, object?>)error.Extensions!["problem"]!;
        Assert.Equal(Problems + "internal-error", problem["type"]);
        Assert.Equal(500, problem["status"]);
        Assert.False(problem.ContainsKey("detail"));
    }

    // ── Standalone: provider failures classified here ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Standalone_an_account_out_of_credits_is_provider_unavailable_with_the_fix_it_text_and_is_counted()
    {
        // OpenRouter's 402 (tests/fixtures/provider-failures.json, openrouter-402-credits).
        using var server = new FakeLlmServer(errorFirst: 5, errorStatus: 402)
        {
            ErrorJson = """{"error":{"code":402,"message":"Insufficient credits. Add more using https://openrouter.ai/settings/credits","metadata":{}}}""",
        };
        using var app = Standalone(server);
        var counted = new ConcurrentBag<(string Type, string Reason)>();
        using var listener = Listen(counted);

        var r = await Post(app.CreateClient(), ChatMutation, Input("Who are you?"));

        Assert.Equal("PROVIDER_UNAVAILABLE", Code(r));
        var problem = Problem(r);
        Assert.Equal(Problems + "provider-unavailable", problem["type"]!.GetValue<string>());
        Assert.Equal(503, problem["status"]!.GetValue<int>());
        Assert.Equal("credits", problem["reason"]!.GetValue<string>());
        Assert.Equal("Your model provider account is out of credits — top it up, or run `setup` to switch the chat to another model.",
            problem["detail"]!.GetValue<string>());
        Assert.Null(problem["provider"]);                  // the endpoint's host is not a provider name
        var text = r.ToJsonString();
        Assert.DoesNotContain("localhost", text);
        Assert.DoesNotContain(FakeKey, text);
        Assert.DoesNotContain("Insufficient credits", text);   // never the provider's text
        Assert.Single(server.Requests);                     // not retried
        Assert.Contains(("provider-unavailable", "credits"), counted);
    }

    [Fact]
    public async Task Standalone_a_rejected_key_is_provider_unavailable_auth()
    {
        using var server = new FakeLlmServer(errorFirst: 5, errorStatus: 401);
        using var app = Standalone(server);

        var r = await Post(app.CreateClient(), ChatMutation, Input("Who are you?"));

        Assert.Equal("auth", Problem(r)["reason"]!.GetValue<string>());
        Assert.StartsWith("Your model provider API key wasn't accepted — run `setup`", Problem(r)["detail"]!.GetValue<string>());
        Assert.Single(server.Requests);
    }

    // ── Gateway: the proxy's problems passed through ─────────────────────────────────────────────────────────────────────────

    public static TheoryData<int, string, string, string?, object?> ProxyProblems() => new()
    {
        { 403, """{"type":"https://agentbullpen.ai/problems/budget-exhausted","title":"Daily allowance used up","status":403,"dailyAllowanceUsd":1.00,"usedTodayUsd":1.000412,"resetsAtUtc":"2026-10-10T00:00:00Z"}""",
            "BUDGET_EXHAUSTED", "resetsAtUtc", "2026-10-10T00:00:00Z" },
        // The member's allowance level is off.
        { 403, """{"type":"https://agentbullpen.ai/problems/chat-off","title":"Chat is off","status":403,"detail":"member 42 has no allowance","level":"off"}""",
            "CHAT_OFF", "level", "off" },
        { 429, """{"type":"https://agentbullpen.ai/problems/rate-limited","title":"Too many requests","status":429,"retryAfterSeconds":12}""",
            "RATE_LIMITED", "retryAfterSeconds", 12L },
        { 503, """{"type":"https://agentbullpen.ai/problems/provider-unavailable","title":"Provider unavailable","status":503,"detail":"openrouter account acct_42 out of credits","provider":"openrouter","reason":"credits"}""",
            "PROVIDER_UNAVAILABLE", "reason", "credits" },
        { 502, """{"type":"https://agentbullpen.ai/problems/upstream-error","title":"Upstream error","status":502,"provider":"openrouter"}""",
            "LLM_ERROR", null, null },
        { 403, """{"type":"https://agentbullpen.ai/problems/model-not-allowed","title":"Model not allowed","status":403,"model":"openai/gpt-4o-mini"}""",
            "UNKNOWN_MODEL", "model", "openai/gpt-4o-mini" },
        // The site's later types.
        { 403, """{"type":"https://agentbullpen.ai/problems/upstream-rejected","title":"Upstream rejected the request","status":403,"provider":"openrouter","detail":"flagged by moderation"}""",
            "UPSTREAM_REJECTED", null, null },
        { 413, """{"type":"https://agentbullpen.ai/problems/request-too-large","title":"Request too large","status":413,"maxBytes":1048576}""",
            "REQUEST_TOO_LARGE", null, null },
        { 404, """{"type":"https://agentbullpen.ai/problems/provider-not-found","title":"Provider not found","status":404,"provider":"openrouter"}""",
            "PROVIDER_NOT_FOUND", null, null },
        // The site paused the demo mid-session: the LLM proxy's call refused, the under-construction page passed on.
        { 503, """{"type":"https://agentbullpen.ai/problems/app-unavailable","title":"App unavailable","status":503,"app":"temporal","message":"Back soon","underConstructionUrl":"/under-construction?app=temporal"}""",
            "APP_UNAVAILABLE", "underConstructionUrl", "/under-construction?app=temporal" },
    };

    [Theory]
    [MemberData(nameof(ProxyProblems))]
    public async Task Gateway_the_proxys_problem_type_and_typed_extensions_pass_through_without_its_provider_or_text(int status, string body, string code,
        string? extension, object? value)
    {
        using var proxy = new FakeLlmServer(errorFirst: 5, errorStatus: status) { ModelsJson = ModelsJson, ErrorJson = body };
        using var app = Gateway(proxy);

        var r = await Post(app.CreateClient(), ChatMutation, Input("Who are you?"), Token());

        Assert.Equal(code, Code(r));
        var problem = Problem(r);
        Assert.StartsWith(Problems, problem["type"]!.GetValue<string>());
        if (extension is not null) Assert.Equal(value?.ToString(), problem[extension]!.ToString());
        Assert.Null(problem["provider"]);
        Assert.DoesNotContain("acct_42", r.ToJsonString());
        Assert.Single(proxy.Requests, p => p.Path.EndsWith("/chat/completions"));   // the proxy already retried; never again here
    }

    [Fact]
    public async Task Gateway_a_budget_refusal_without_a_reset_time_resets_at_the_next_utc_midnight()
    {
        using var proxy = new FakeLlmServer(errorFirst: 1, errorStatus: 403) { ModelsJson = ModelsJson, ErrorJson = """{"title":"Daily token budget exhausted"}""" };
        using var app = Gateway(proxy);

        var r = await Post(app.CreateClient(), ChatMutation, Input("Who are you?"), Token());

        var resets = DateTimeOffset.Parse(Problem(r)["resetsAtUtc"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(TimeSpan.Zero, resets.TimeOfDay);
        Assert.InRange(resets - DateTimeOffset.UtcNow, TimeSpan.Zero, TimeSpan.FromDays(1));
    }

    [Fact]
    public async Task Gateway_a_fallback_answer_says_which_model_answered_and_unavailable_models_are_marked()
    {
        using var proxy = new FakeLlmServer { ModelsJson = ModelsJson };
        proxy.ResponseHeaders["X-Model-Fallback"] = "anthropic/claude-haiku-5.5 -> openai/gpt-4o-mini";
        using var app = Gateway(proxy);
        var client = app.CreateClient();

        var models = await Post(client, "{ chatModels { id displayName isDefault available unavailableUntilUtc } budget { resetsAtUtc } }", token: Token());
        var list = models["data"]!["chatModels"]!.AsArray();
        var haiku = list.Single(m => m!["id"]!.GetValue<string>() == "openrouter:anthropic/claude-haiku-5.5")!;
        Assert.False(haiku["available"]!.GetValue<bool>());
        Assert.Equal("2026-10-10T15:00:00Z", haiku["unavailableUntilUtc"]!.GetValue<string>());
        Assert.False(haiku["isDefault"]!.GetValue<bool>());   // preferred, but unavailable: the default is an available model
        Assert.True(list.Single(m => m!["id"]!.GetValue<string>() == "openrouter:openai/gpt-4o-mini")!["isDefault"]!.GetValue<bool>());

        var r = await Post(client, ChatMutation, Input("Who are you?", model: "openrouter:anthropic/claude-haiku-5.5"), Token());

        var fallback = r["data"]!["chat"]!["fallback"]!;
        Assert.Equal("anthropic/claude-haiku-5.5", fallback["requested"]!.GetValue<string>());
        Assert.Equal("GPT-4o mini", fallback["used"]!.GetValue<string>());   // the list's display name

        proxy.ResponseHeaders.Clear();
        var session = r["data"]!["chat"]!["sessionId"]!.GetValue<string>();
        var next = await Post(client, ChatMutation,
            new { input = new { bookId = Hound, chapter = 3, character = "Sherlock Holmes", spoilers = false, message = "And now?", sessionId = session, model = "openrouter:anthropic/claude-haiku-5.5" } },
            Token());
        Assert.Null(next["data"]!["chat"]!["fallback"]);   // cleared before each turn
    }

    private static MeterListener Listen(ConcurrentBag<(string Type, string Reason)> counted)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == TemporalGraph.Api.Problems.MeterName && instrument.Name == TemporalGraph.Api.Problems.CounterName) l.EnableMeasurementEvents(instrument);
            },
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            string type = "", reason = "";
            foreach (var t in tags)
            {
                if (t.Key == "type") type = t.Value?.ToString() ?? "";
                if (t.Key == "reason") reason = t.Value?.ToString() ?? "";
            }
            counted.Add((type, reason));
        });
        listener.Start();
        return listener;
    }

    // ── provider-unavailable with a fallback model ──────────────────────────────────────────────────────────────

    private const string BothAvailable = """
        { "providers": [
          { "name": "openrouter", "protocol": "openai", "models": [
            { "id": "openai/gpt-4o-mini", "kind": "chat", "displayName": "GPT-4o mini" },
            { "id": "anthropic/claude-haiku-5.5", "kind": "chat", "displayName": "Claude Haiku 5.5" } ] } ] }
        """;

    private static string Unavailable(string fallback) =>
        $$"""{"type":"https://agentbullpen.ai/problems/provider-unavailable","title":"Provider unavailable","status":503,"reason":"credits","provider":"openrouter","fallbackModel":"{{fallback}}"}""";

    private static List<FakeLlmServer.Request> ChatCalls(FakeLlmServer proxy) => proxy.Requests.Where(p => p.Path.EndsWith("/chat/completions")).ToList();

    [Fact]
    public async Task Gateway_provider_unavailable_with_a_fallback_model_is_answered_once_on_it_and_the_conversation_stays_there()
    {
        using var proxy = new FakeLlmServer(errorFirst: 1, errorStatus: 503) { ModelsJson = BothAvailable, ErrorJson = Unavailable("anthropic/claude-haiku-5.5") };
        using var app = Gateway(proxy);
        var client = app.CreateClient();

        var r = await Post(client, ChatMutation, Input("Who are you?", model: "openrouter:openai/gpt-4o-mini"), Token());

        Assert.Null(r["errors"]);
        var fallback = r["data"]!["chat"]!["fallback"]!;
        Assert.Equal("GPT-4o mini", fallback["requested"]!.GetValue<string>());
        Assert.Equal("Claude Haiku 5.5", fallback["used"]!.GetValue<string>());
        var calls = ChatCalls(proxy);
        Assert.Equal(2, calls.Count);
        Assert.Contains("\"model\":\"openai/gpt-4o-mini\"", calls[0].Body.Replace(" ", ""));
        Assert.Contains("\"model\":\"anthropic/claude-haiku-5.5\"", calls[1].Body.Replace(" ", ""));

        // The next message (the client still asks for the requested model) continues the fallback session.
        var session = r["data"]!["chat"]!["sessionId"]!.GetValue<string>();
        var next = await Post(client, ChatMutation,
            new { input = new { bookId = Hound, chapter = 3, character = "Sherlock Holmes", spoilers = false, message = "And now?", sessionId = session, model = "openrouter:openai/gpt-4o-mini" } },
            Token());
        Assert.Equal(session, next["data"]!["chat"]!["sessionId"]!.GetValue<string>());
        Assert.Equal("Claude Haiku 5.5", next["data"]!["chat"]!["fallback"]!["used"]!.GetValue<string>());
        Assert.Contains("\"model\":\"anthropic/claude-haiku-5.5\"", ChatCalls(proxy)[^1].Body.Replace(" ", ""));
    }

    [Fact]
    public async Task Gateway_the_fallback_is_tried_once_and_a_second_failure_is_answered_as_usual()
    {
        using var proxy = new FakeLlmServer(errorFirst: 2, errorStatus: 503) { ModelsJson = BothAvailable, ErrorJson = Unavailable("anthropic/claude-haiku-5.5") };
        using var app = Gateway(proxy);

        var r = await Post(app.CreateClient(), ChatMutation, Input("Who are you?", model: "openrouter:openai/gpt-4o-mini"), Token());

        Assert.Equal("PROVIDER_UNAVAILABLE", Code(r));
        Assert.Equal(2, ChatCalls(proxy).Count);
    }

    [Fact]
    public async Task Gateway_a_fallback_the_site_doesnt_serve_is_not_tried()
    {
        using var proxy = new FakeLlmServer(errorFirst: 1, errorStatus: 503) { ModelsJson = ModelsJson, ErrorJson = Unavailable("anthropic/claude-haiku-5.5") };   // listed unavailable
        using var app = Gateway(proxy);

        var r = await Post(app.CreateClient(), ChatMutation, Input("Who are you?", model: "openrouter:openai/gpt-4o-mini"), Token());

        Assert.Equal("PROVIDER_UNAVAILABLE", Code(r));
        Assert.Single(ChatCalls(proxy));
    }

    [Fact]
    public async Task Gateway_a_closed_demo_on_the_model_list_is_app_unavailable_with_the_under_construction_page()
    {
        // The site's model list refused for a demo that isn't open: the problem, not "no models", so the panel redirects.
        using var proxy = new FakeLlmServer
        {
            ModelsStatus = 503,
            ModelsJson = """{"type":"https://agentbullpen.ai/problems/app-unavailable","title":"App unavailable","status":503,"app":"temporal","underConstructionUrl":"/under-construction?app=temporal"}""",
        };
        using var app = Gateway(proxy);

        var r = await Post(app.CreateClient(), "{ chatModels { id } }", token: Token());

        Assert.Equal("APP_UNAVAILABLE", Code(r));
        var problem = Problem(r);
        Assert.Equal(Problems + "app-unavailable", problem["type"]!.GetValue<string>());
        Assert.Equal("/under-construction?app=temporal", problem["underConstructionUrl"]!.GetValue<string>());
        Assert.Equal(503, problem["status"]!.GetValue<int>());
    }
}
