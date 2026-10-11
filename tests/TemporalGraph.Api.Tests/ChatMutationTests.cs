using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;
using TemporalGraph.Api;

namespace TemporalGraph.Api.Tests;

// The chat mutation, chatAvailable and chatCharacters over HTTP, against FakeLlmServer (no real network). The
// "Testing" environment keeps appsettings.Development.json and user-secrets out: the committed template is the config.
public sealed class ChatMutationTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private const string Hound = "hound-of-the-baskervilles";
    private const string FakeKey = "fake-key-for-tests-0123456789";

    private const string ChatMutation = """
        mutation($input: ChatInput!) {
          chat(input: $input) { sessionId answer toolCalls { tool args rows resolvedName nodeIds edgeIds } lateRowsSuppressed usage { inputTokens outputTokens } model }
        }
        """;

    private WebApplicationFactory<Program> App(FakeLlmServer? server, params (string Key, string Value)[] settings) =>
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

    private static async Task<JsonNode> Post(HttpClient client, string query, object? variables = null)
    {
        var response = await client.PostAsJsonAsync("/graphql", new { query, variables }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);   // errors are GraphQL errors, never a 500
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
    }

    private static object Input(string message, int chapter = 3, string character = "Sherlock Holmes", bool spoilers = false, string? sessionId = null, string book = Hound) =>
        new { input = new { bookId = book, chapter, character, spoilers, sessionId, message } };

    private static string Code(JsonNode r) => r["errors"]![0]!["extensions"]!["code"]!.GetValue<string>();

    [Fact]
    public async Task Chat_is_not_available_on_the_template_config_and_the_mutation_says_llm_not_configured()
    {
        using var app = App(null);
        var client = app.CreateClient();

        var available = await Post(client, "{ chatAvailable }");
        Assert.Null(available["errors"]);
        Assert.False(available["data"]!["chatAvailable"]!.GetValue<bool>());

        var r = await Post(client, ChatMutation, Input("Who is Dr. Mortimer?"));
        Assert.Equal("LLM_NOT_CONFIGURED", Code(r));
        var message = r["errors"]![0]!["message"]!.GetValue<string>();
        Assert.Contains("README", message);
        Assert.Contains("Llm:ApiKey is not set.", message);   // the setting's name, never a value
        Assert.Equal(["chat"], r["errors"]![0]!["path"]!.AsArray().Select(p => p!.GetValue<string>()));
        Assert.True(r["data"] is null || r["data"]!.GetValueKind() == System.Text.Json.JsonValueKind.Null);
    }

    [Fact]
    public async Task Chat_answers_with_its_tool_calls_tokens_and_model()
    {
        using var server = new FakeLlmServer();
        server.OpenAIMessages.Enqueue(FakeLlmServer.ToolCall("call_1", "facts_about", new { character = "Holmes" }));
        server.OpenAIMessages.Enqueue(FakeLlmServer.Text("I am a consulting detective."));
        using var app = App(server);
        var client = app.CreateClient();

        Assert.True((await Post(client, "{ chatAvailable }"))["data"]!["chatAvailable"]!.GetValue<bool>());
        var r = await Post(client, ChatMutation, Input("Who are you?"));

        Assert.Null(r["errors"]);
        var chat = r["data"]!["chat"]!;
        Assert.Equal("I am a consulting detective.", chat["answer"]!.GetValue<string>());
        Assert.Equal(32, chat["sessionId"]!.GetValue<string>().Length);
        var call = Assert.Single(chat["toolCalls"]!.AsArray())!;
        Assert.Equal("facts_about", call["tool"]!.GetValue<string>());
        Assert.Equal("character: \"Holmes\"", call["args"]!.GetValue<string>());
        Assert.True(call["rows"]!.GetValue<int>() > 0);
        Assert.Null(call["resolvedName"]?.GetValue<string>());
        // The ids of the rows the model saw (the Fact nodes and what they are about). A Fact is a node and names its subjects in a property, so there is no ABOUT edge to carry.
        Assert.True(call["nodeIds"]!.AsArray().Count > call["rows"]!.GetValue<int>());
        Assert.Empty(call["edgeIds"]!.AsArray());
        Assert.Equal(22, chat["usage"]!["inputTokens"]!.GetValue<int>());   // two LLM calls of 11
        Assert.Equal(2, chat["usage"]!["outputTokens"]!.GetValue<int>());
        Assert.Equal(FakeLlmServer.OpenAIModel, chat["model"]!.GetValue<string>());
        Assert.True(chat["lateRowsSuppressed"]!.GetValue<int>() >= 0);

        // The same prompt, pack and tools as the CLI: the display name, the pack once, the tool result in the second request.
        Assert.Equal(2, server.Requests.Count);
        var first = JsonNode.Parse(server.Requests[0].Body)!;
        Assert.StartsWith("You are Sherlock Holmes, a character in \"The Hound of the Baskervilles\"", first["messages"]![0]!["content"]!.GetValue<string>());
        Assert.StartsWith("What you, Sherlock Holmes, know at this point", first["messages"]![1]!["content"]!.GetValue<string>());
        Assert.Equal("required", first["tool_choice"]!.GetValue<string>());
        Assert.Equal(0, first["temperature"]!.GetValue<int>());
        Assert.Equal(20261007, first["seed"]!.GetValue<long>());
        Assert.Equal("tool", JsonNode.Parse(server.Requests[1].Body)!["messages"]!.AsArray()[^1]!["role"]!.GetValue<string>());
    }

    [Fact]
    public async Task Chat_llm_switches_the_api_chat_to_a_profile()
    {
        // Chat:Llm names an Llm:Profiles entry; the Llm section itself stays as it is.
        using var server = new FakeLlmServer();
        server.OpenAIMessages.Enqueue(FakeLlmServer.Text("Hm."));
        using var app = App(server, ("Chat:Llm", "alt"), ("Llm:Profiles:alt:Type", "OpenAI"), ("Llm:Profiles:alt:Endpoint", server.BaseUrl + "/openai/v1/"),
            ("Llm:Profiles:alt:Model", "alt-deployment"));
        var client = app.CreateClient();

        Assert.True((await Post(client, "{ chatAvailable }"))["data"]!["chatAvailable"]!.GetValue<bool>());
        Assert.Null((await Post(client, ChatMutation, Input("Who are you?")))["errors"]);
        Assert.Contains("\"model\":\"alt-deployment\"", server.Requests[0].Body.Replace(" ", ""));

        // A Chat:Llm profile that isn't configured makes chat unavailable (not a silent fallback).
        using var missing = App(server, ("Chat:Llm", "nosuch"));
        Assert.False((await Post(missing.CreateClient(), "{ chatAvailable }"))["data"]!["chatAvailable"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Search_facts_calls_carry_the_fact_ids_and_their_edges()
    {
        using var server = new FakeLlmServer();
        server.OpenAIMessages.Enqueue(FakeLlmServer.ToolCall("call_1", "search_facts", new { keywords = "murder Stapleton" }));
        server.OpenAIMessages.Enqueue(FakeLlmServer.Text("Stapleton."));
        using var app = App(server);

        var r = await Post(app.CreateClient(), ChatMutation, Input("Who is the killer?", chapter: 15));

        Assert.Null(r["errors"]);
        var call = Assert.Single(r["data"]!["chat"]!["toolCalls"]!.AsArray())!;
        Assert.Equal("search_facts", call["tool"]!.GetValue<string>());
        var nodes = call["nodeIds"]!.AsArray().Select(n => n!.GetValue<int>()).ToList();
        Assert.Contains(26, nodes);   // Stapleton (the facts are about him)
        Assert.True(nodes.Count > 20);
        Assert.NotEmpty(call["edgeIds"]!.AsArray());
    }

    [Fact]
    public async Task A_session_continues_the_conversation_and_a_changed_chapter_starts_a_new_one()
    {
        using var server = new FakeLlmServer();
        server.OpenAIMessages.Enqueue(FakeLlmServer.Text("Answer one."));
        server.OpenAIMessages.Enqueue(FakeLlmServer.Text("Answer two."));
        server.OpenAIMessages.Enqueue(FakeLlmServer.Text("Answer three."));
        using var app = App(server);
        var client = app.CreateClient();

        var one = (await Post(client, ChatMutation, Input("Question one?")))["data"]!["chat"]!;
        var id = one["sessionId"]!.GetValue<string>();
        var two = (await Post(client, ChatMutation, Input("Question two?", sessionId: id)))["data"]!["chat"]!;
        Assert.Equal(id, two["sessionId"]!.GetValue<string>());
        Assert.Equal("Answer two.", two["answer"]!.GetValue<string>());
        // Same session: the history goes with the second question, and the pack is still there once.
        Assert.Contains("Question one?", server.Requests[1].Body);
        Assert.Contains("Answer one.", server.Requests[1].Body);
        Assert.Equal(1, Count(server.Requests[1].Body, "know at this point"));

        var three = (await Post(client, ChatMutation, Input("Question three?", chapter: 5, sessionId: id)))["data"]!["chat"]!;
        Assert.NotEqual(id, three["sessionId"]!.GetValue<string>());
        Assert.DoesNotContain("Question one?", server.Requests[2].Body);
        Assert.Contains("up to the end of chapter 5.", server.Requests[2].Body);

        // An unknown (expired, restarted) session id starts a new session too.
        server.OpenAIMessages.Enqueue(FakeLlmServer.Text("Answer four."));
        var four = (await Post(client, ChatMutation, Input("Question four?", sessionId: "no-such-session")))["data"]!["chat"]!;
        Assert.NotEqual("no-such-session", four["sessionId"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(Hound, 3, "Irene Adler", "UNKNOWN_CHARACTER")]
    [InlineData(Hound, 99, "Sherlock Holmes", "UNKNOWN_CHAPTER")]
    [InlineData("no-such-book", 3, "Sherlock Holmes", "UNKNOWN_BOOK")]
    [InlineData("three-musketeers", 1, "Sherlock Holmes", "UNKNOWN_CHARACTER")]   // another book's character
    public async Task Bad_input_is_a_typed_error_without_a_model_call(string book, int chapter, string character, string code)
    {
        using var server = new FakeLlmServer();
        using var app = App(server);

        var r = await Post(app.CreateClient(), ChatMutation, Input("Who?", chapter, character, book: book));

        Assert.Equal(code, Code(r));
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task A_failed_model_call_is_llm_error_without_the_endpoint_or_key()
    {
        using var server = new FakeLlmServer(errorFirst: 5, errorStatus: 400);   // not retried, and not a provider-account failure
        using var app = App(server);

        var r = await Post(app.CreateClient(), ChatMutation, Input("Who are you?"));

        Assert.Equal("LLM_ERROR", Code(r));
        var text = r.ToJsonString();
        Assert.Contains("HTTP 400", text);
        Assert.Equal("https://agentbullpen.ai/problems/upstream-error", r["errors"]![0]!["extensions"]!["problem"]!["type"]!.GetValue<string>());
        Assert.DoesNotContain("localhost", text);
        Assert.DoesNotContain(FakeKey, text);
        Assert.DoesNotContain("my-deployment", text);
    }

    [Fact]
    public async Task A_turn_past_the_timeout_is_timeout()
    {
        using var server = new FakeLlmServer { DelayMs = 3000 };
        using var app = App(server, ("Chat:TimeoutSeconds", "1"));

        var r = await Post(app.CreateClient(), ChatMutation, Input("Who are you?"));

        Assert.Equal("TIMEOUT", Code(r));
    }

    /// <summary>Books.json's Hound roster, resolved to the promoted graph's canonical names.</summary>
    private static readonly string[] HoundRoster =
        ["Sherlock Holmes", "Dr. Watson", "Sir Henry Baskerville", "Dr. James Mortimer", "Mr. Stapleton", "Miss Stapleton", "Barrymore", "Mrs. Laura Lyons"];

    /// <summary>
    /// How they are shown with spoilers on: the roster's own spelling (books.json), which wins over the short name; Miss
    /// Stapleton and Barrymore are spelled the same both ways.
    /// </summary>
    private static readonly string[] HoundShown =
        ["Sherlock Holmes", "Dr. Watson", "Sir Henry Baskerville", "Dr. James Mortimer", "Mr. Stapleton", "Miss Stapleton", "Barrymore", "Laura Lyons"];

    [Fact]
    public async Task Chat_characters_are_the_roster_in_order_and_hide_later_characters_with_spoilers_off()
    {
        using var app = App(null);
        const string q = "query($ch: Int!, $s: Boolean!) { chatCharacters(bookId: \"hound-of-the-baskervilles\", chapter: $ch, spoilers: $s) { name displayName initials nodeId avatar } }";
        var client = app.CreateClient();

        var off = (await Post(client, q, new { ch = 3, s = false }))["data"]!["chatCharacters"]!.AsArray();
        var on = (await Post(client, q, new { ch = 15, s = true }))["data"]!["chatCharacters"]!.AsArray();
        var on3 = (await Post(client, q, new { ch = 3, s = true }))["data"]!["chatCharacters"]!.AsArray();
        var all = (await Post(client, $"{{ characters(bookId: \"{Hound}\") }}"))["data"]!["characters"]!.AsArray();

        // Spoilers on at ch. 15: the whole roster in roster order, canonical names shown by the roster's spelling; the Graph Query list
        // keeps every character.
        Assert.Equal(HoundRoster, on.Select(c => c!["name"]!.GetValue<string>()));
        Assert.Equal(HoundShown, on.Select(c => c!["displayName"]!.GetValue<string>()));
        Assert.Equal(1, on[0]!["nodeId"]!.GetValue<int>());    // Holmes's graph node (avatar click selects it)
        Assert.Equal(55, on[5]!["nodeId"]!.GetValue<int>());   // Miss Stapleton
        // The avatar is the slug of the roster name (not of the canonical name), in the manifest's folder.
        Assert.Equal(
            ["sherlock-holmes", "dr-watson", "sir-henry-baskerville", "dr-james-mortimer", "mr-stapleton", "miss-stapleton", "barrymore", "laura-lyons"],
            on.Select(c => c!["avatar"]!.GetValue<string>().Replace("books/hound-of-the-baskervilles/characters/", "").Replace(".webp", "")));
        Assert.InRange(all.Count, 52, 70);   // 61 characters in the promoted graph when this was written; a range of 15 % either way, so that a re-index does not break this
        // Spoilers off at ch. 3: only roster characters mentioned by then, still in roster order, by their spoiler-safe names.
        var offNames = off.Select(c => c!["name"]!.GetValue<string>()).ToList();
        Assert.Equal(HoundRoster.Where(offNames.Contains), offNames);
        Assert.DoesNotContain("Miss Stapleton", offNames);
        Assert.DoesNotContain("Mrs. Laura Lyons", offNames);
        Assert.True(off.Count < on.Count);
        // Spoilers on also offers only the characters mentioned by the chapter (the roster shrinks when the timeline moves
        // back), the same ones as with spoilers off at ch. 3, by the roster's spelling.
        Assert.Equal(["Sherlock Holmes", "Dr. Watson", "Sir Henry Baskerville", "Dr. James Mortimer", "Mr. Stapleton", "Barrymore"],
            on3.Select(c => c!["name"]!.GetValue<string>()));
        Assert.Equal(offNames, on3.Select(c => c!["name"]!.GetValue<string>()));
        Assert.Equal(["Sherlock Holmes", "Dr. Watson", "Sir Henry Baskerville", "Dr. James Mortimer", "Mr. Stapleton", "Barrymore"],
            on3.Select(c => c!["displayName"]!.GetValue<string>()));
        var holmes = off[0]!;
        Assert.Equal("Sherlock Holmes", holmes["name"]!.GetValue<string>());
        Assert.Equal("Sherlock Holmes", holmes["displayName"]!.GetValue<string>());   // Revealed in ch. 1 as spelled in the roster
        Assert.Equal("SH", holmes["initials"]!.GetValue<string>());   // of the shown name
        Assert.All(off, c => Assert.False(string.IsNullOrEmpty(c!["initials"]!.GetValue<string>())));
        // "Beryl Garcia" is first spelled in ch. 15; at ch. 3 she isn't offered at all with spoilers off.
        Assert.DoesNotContain(off, c => c!["displayName"]!.GetValue<string>().Contains("Beryl"));

        // Musketeers: the roster's eight by their canonical names at the end, in roster order.
        var musketeers = await Post(client, "{ chatCharacters(bookId: \"three-musketeers\", chapter: 67, spoilers: true) { name } }");
        Assert.Equal(["D’Artagnan", "Athos", "Porthos", "Aramis", "Milady de Winter", "Cardinal Richelieu", "Madame Bonacieux", "Planchet"],
            musketeers["data"]!["chatCharacters"]!.AsArray().Select(c => c!["name"]!.GetValue<string>()));
        // Shown by the roster's spelling where the graph has it by then (d'Artagnan from ch. 1; "Cardinal Richelieu" from ch. 61
        // in the promoted graph, so the Cardinal goes by his short name here), else by their short names, with initials
        // made from the shown name, at mid-book with spoilers off.
        var mid = await Post(client, "{ chatCharacters(bookId: \"three-musketeers\", chapter: 20, spoilers: false) { displayName initials } }");
        var shown = mid["data"]!["chatCharacters"]!.AsArray();
        Assert.Equal(["d'Artagnan", "Athos", "Porthos", "Aramis", "Milady", "The Red Duke", "Mme. Bonacieux", "Planchet"],
            shown.Select(c => c!["displayName"]!.GetValue<string>()));
        Assert.Equal(["D", "A", "P", "A", "M", "RD", "B", "P"], shown.Select(c => c!["initials"]!.GetValue<string>()));
    }

    /// <summary>Stored initials that are null, empty or blank (a book promoted before build-db filled them) → derived from the shown name.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Empty_stored_initials_are_derived_from_the_display_name(string? stored)
    {
        var catalog = new BookCatalog([new TemporalGraph.Data.BookEntry(Hound, "The Hound of the Baskervilles", "Arthur Conan Doyle",
            ["Sherlock Holmes", "Dr. Watson", "Sir Henry Baskerville", "Dr. James Mortimer", "Mr. Stapleton", "Miss Stapleton", "Barrymore", "Laura Lyons"])], new ListLogger());
        catalog.LoadAll();
        var book = catalog.Get(Hound);
        var bare = book with { Roster = book.Roster.Select(c => c with { Initials = stored }).ToList() };

        var shown = ChatService.Characters(bare, 15, spoilers: true);

        Assert.Equal(["SH", "W", "HB", "JM", "S", "S", "B", "LL"], shown.Select(c => c.Initials));   // of the roster spellings
        Assert.Equal(ChatService.Characters(book, 15, spoilers: true).Select(c => c.Initials), shown.Select(c => c.Initials));   // same as stored
    }

    [Fact]
    public void A_roster_name_that_matches_nothing_is_skipped_and_logged()
    {
        var log = new ListLogger();
        var catalog = new BookCatalog([new TemporalGraph.Data.BookEntry(Hound, "The Hound of the Baskervilles", "Arthur Conan Doyle", ["Irene Adler", "watson", "Holmes", "Sherlock Holmes"])], log);

        catalog.LoadAll();

        var book = catalog.Get(Hound);
        Assert.Equal(["Dr. Watson", "Sherlock Holmes"], book.Roster.Select(c => c.Name));   // case-insensitive, aliases, each once
        Assert.Equal(["Irene Adler"], book.UnknownRosterNames);
        var warning = Assert.Single(log.Messages);
        Assert.Contains("'Irene Adler' matches no character", warning);
    }

    private sealed class ListLogger : Microsoft.Extensions.Logging.ILogger<BookCatalog>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }

    private static int Count(string text, string part) => (text.Length - text.Replace(part, "").Length) / part.Length;
}

// The session store's expiry and cap, with a hand-moved clock.
public sealed class ChatSessionsTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static ChatSession Session(string id, DateTimeOffset now) =>
        new(id, "book", 1, "c", false, conversation: null!, new NullChatClient(), now);

    private sealed class NullChatClient : Microsoft.Extensions.AI.IChatClient
    {
        public Task<Microsoft.Extensions.AI.ChatResponse> GetResponseAsync(IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, Microsoft.Extensions.AI.ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<Microsoft.Extensions.AI.ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, Microsoft.Extensions.AI.ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [Fact]
    public void Sessions_expire_after_30_idle_minutes()
    {
        var clock = new Clock();
        using var sessions = new ChatSessions(clock);
        sessions.Add(Session("a", clock.Now));

        clock.Now += TimeSpan.FromMinutes(29);
        Assert.NotNull(sessions.Get("a"));   // and its idle clock restarts
        clock.Now += TimeSpan.FromMinutes(29);
        Assert.NotNull(sessions.Get("a"));
        clock.Now += TimeSpan.FromMinutes(31);
        Assert.Null(sessions.Get("a"));
        Assert.Equal(0, sessions.Count);
    }

    [Fact]
    public void At_the_cap_the_least_recently_used_session_is_dropped()
    {
        var clock = new Clock();
        using var sessions = new ChatSessions(clock);
        for (var i = 0; i < ChatSessions.Cap; i++)
        {
            sessions.Add(Session($"s{i}", clock.Now));
            clock.Now += TimeSpan.FromSeconds(1);
        }
        Assert.NotNull(sessions.Get("s0"));   // s0 used again: s1 is now the least recently used

        sessions.Add(Session("new", clock.Now));

        Assert.Equal(ChatSessions.Cap, sessions.Count);
        Assert.Null(sessions.Get("s1"));
        Assert.NotNull(sessions.Get("s0"));
        Assert.NotNull(sessions.Get("new"));
    }
}
