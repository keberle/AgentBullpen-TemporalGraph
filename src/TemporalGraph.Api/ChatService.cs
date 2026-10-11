using System.ClientModel;
using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.AI;
using TemporalGraph.Chat;
using TemporalGraph.Llm;

namespace TemporalGraph.Api;

/// <param name="SessionId">A session from an earlier result; null or unknown (expired, server restarted, another member's) starts a new one.</param>
/// <param name="Model">A <c>chatModels</c> id; null or empty: the default model. Another model than the session's starts a new session.</param>
public sealed record ChatInput(string BookId, int Chapter, string Character, bool Spoilers, string? SessionId, string Message, string? Model = null);

/// <param name="Args">The arguments as the CLI prints them: <c>character: "Holmes"</c>; empty for tools without one.</param>
/// <param name="Rows">The rows returned to the model (late rows dropped, before the cap).</param>
/// <param name="ResolvedName">The alias an unknown name was matched as (retry or containment), else null.</param>
/// <param name="NodeIds">The node ids of the rows the model saw for this call (the graph highlight).</param>
/// <param name="EdgeIds">The edge ids of those rows, where the query returns them.</param>
public sealed record ChatToolCall(string Tool, string Args, int Rows, string? ResolvedName, IReadOnlyList<int> NodeIds, IReadOnlyList<int> EdgeIds);

/// <summary>The tokens of every model call of the turn (tool-call round trips and a never-say retry included).</summary>
/// <param name="CostUsd">What those calls cost in US dollars: gateway, the sum of the site's <c>X-Call-Cost-Usd</c>; standalone, the
/// sum of the provider's own <c>usage.cost</c> (OpenRouter). Null when a call came without a cost: it is never estimated.</param>
public sealed record ChatUsage(int InputTokens, int OutputTokens, double? CostUsd = null);

/// <param name="SessionId">The session this answer belongs to; send it with the next message to continue the conversation.</param>
/// <param name="LateRowsSuppressed">Rows hidden as not yet revealed in this turn (tool calls, and the knowledge pack on a session's first turn).</param>
/// <param name="Budget">The member's allowance after this turn (gateway), unlimited (standalone), or null without the gateway's headers.</param>
/// <param name="Fallback">Gateway mode: the site answered with its fallback model (<c>X-Model-Fallback</c>); null otherwise.</param>
public sealed record ChatResult(string SessionId, string Answer, IReadOnlyList<ChatToolCall> ToolCalls, int LateRowsSuppressed, ChatUsage Usage, string? Model,
    Budget? Budget = null, ModelFallback? Fallback = null);

/// <summary>The model asked for and the one that answered, by display name where the model list has one.</summary>
public sealed record ModelFallback(string Requested, string Used)
{
    /// <summary><c>&lt;requested&gt; -&gt; &lt;used&gt;</c> (the site's header), with model ids turned into labels; null if it doesn't parse.</summary>
    public static ModelFallback? Parse(string? header, IReadOnlyList<ChatModel> models)
    {
        var parts = header?.Split("->", 2, StringSplitOptions.TrimEntries);
        if (parts is not [{ Length: > 0 } requested, { Length: > 0 } used]) return null;
        string Label(string id) => models.FirstOrDefault(m => m.Model == id || m.Id == id)?.DisplayName ?? id;
        return new ModelFallback(Label(requested), Label(used));
    }
}

/// <summary>A character the chat panel can offer at a chapter: canonical name, display name at the chapter, avatar initials, and
/// its graph node id, so an avatar click selects the node in the graph tabs.</summary>
[GraphQLName("ChatCharacter")]
/// <param name="Avatar"><c>&lt;avatars folder&gt;/&lt;slug of the roster name&gt;.webp</c> (relative to the web app's public folder) when the
/// manifest has an avatars folder; null otherwise (the panel shows the initials).</param>
public sealed record ChatCharacterView(string Name, string DisplayName, string Initials, int? NodeId = null, string? Avatar = null);

/// <summary>
/// One conversation: the book, chapter, character and spoilers it was built for (the pack depends on them), its
/// <see cref="ChatConversation"/> (which holds the Agent Framework session) and its own chat client, so the call log's token
/// counts belong to this conversation. Messages of a session are asked one at a time (<see cref="Gate"/>). The model it was
/// started with (a <c>chatModels</c> id) and its owner (the gateway member; null standalone).
/// </summary>
public sealed class ChatSession(string id, string bookId, int chapter, string character, bool spoilers, ChatConversation conversation, IChatClient client, DateTimeOffset now,
    string model = "", string? owner = null)
    : IDisposable
{
    public string Id { get; } = id;
    public string BookId { get; } = bookId;
    public int Chapter { get; } = chapter;
    public string Character { get; } = character;
    public bool Spoilers { get; } = spoilers;
    public ChatConversation Conversation { get; } = conversation;
    public string Model { get; } = model;
    public string? Owner { get; } = owner;
    public SemaphoreSlim Gate { get; } = new(1, 1);
    public DateTimeOffset LastUsed { get; set; } = now;

    /// <summary>The session's call context (gateway mode: where the proxy's <c>X-Model-Fallback</c> lands).</summary>
    public LlmCallContext? CallContext { get; init; }

    /// <summary>
    /// The model this session stands in for. The site's proxy said that model's provider was unavailable and named a
    /// fallback (<c>extensions.fallbackModel</c>), so this session runs on the fallback and later messages for the requested model continue it.
    /// </summary>
    public string? FallbackFor { get; init; }

    public bool Matches(string bookId, int chapter, string character, bool spoilers, string model = "") =>
        BookId == bookId && Chapter == chapter && Character == character && Spoilers == spoilers && (Model == model || FallbackFor == model);

    public void Dispose()
    {
        client.Dispose();
        Gate.Dispose();
    }
}

/// <summary>
/// The chat sessions, in memory (singleton): keyed by id, dropped after <see cref="IdleExpiry"/> without a message, at most
/// <see cref="Cap"/> (adding one more drops the least recently used). A server restart loses them all (open: persistence).
/// </summary>
public sealed class ChatSessions(TimeProvider time) : IDisposable
{
    public static readonly TimeSpan IdleExpiry = TimeSpan.FromMinutes(30);
    public const int Cap = 100;

    private readonly ConcurrentDictionary<string, ChatSession> _sessions = new();
    private readonly Lock _lock = new();

    public int Count => _sessions.Count;

    public DateTimeOffset Now => time.GetUtcNow();

    /// <summary>The session, if it exists, hasn't expired and belongs to <paramref name="owner"/> (its idle clock restarts). Another
    /// member's session id counts as unknown.</summary>
    public ChatSession? Get(string id, string? owner = null)
    {
        Prune();
        if (!_sessions.TryGetValue(id, out var session) || session.Owner != owner) return null;
        session.LastUsed = Now;
        return session;
    }

    public ChatSession Add(ChatSession session)
    {
        lock (_lock)
        {
            Prune();
            while (_sessions.Count >= Cap && _sessions.Values.MinBy(s => s.LastUsed) is { } oldest) Remove(oldest.Id);
            _sessions[session.Id] = session;
        }
        return session;
    }

    private void Prune()
    {
        var cutoff = Now - IdleExpiry;
        foreach (var s in _sessions.Values.Where(s => s.LastUsed < cutoff).ToList()) Remove(s.Id);
    }

    private void Remove(string id)
    {
        // A session in use keeps its client until the turn ends; it is only no longer found.
        if (_sessions.TryRemove(id, out var s) && s.Gate.CurrentCount == 1) s.Dispose();
    }

    public void Dispose()
    {
        foreach (var id in _sessions.Keys.ToList()) Remove(id);
    }
}

/// <summary>
/// The <c>chat</c> mutation's work: validate, find or start the session, ask, map failures to GraphQL errors with
/// <c>extensions.code</c>. Model: <c>Chat:Llm</c>, else the API's <c>Llm</c> section, with its temperature and seed (as the CLI's chat);
/// Or another model of <see cref="IModelCatalog"/>. Standalone or gateway lives in the four adapters, not here.
/// </summary>
public sealed class ChatService(BookCatalog catalog, ChatSessions sessions, IConfiguration config, IModelCatalog models, IChatClientFactory clients,
    ICurrentUser user, IBudget budget, GatewayOptions gateway)
{
    /// <summary><c>Chat:TimeoutSeconds</c> when not configured.</summary>
    public const int DefaultTimeoutSeconds = 60;

    /// <summary>True when a model is configured (standalone: the default profile is usable; gateway: the site lists a chat model).</summary>
    public async Task<bool> AvailableAsync(CancellationToken ct) => (await Catalog(ct)).Problems.Count == 0;

    /// <summary>The models a reader may pick (one is the default); empty when chat isn't available.</summary>
    public async Task<IReadOnlyList<ChatModel>> ModelsAsync(CancellationToken ct) => (await Catalog(ct)).Models;

    private async Task<ModelCatalog> Catalog(CancellationToken ct)
    {
        try { return await models.GetAsync(ct); }
        catch (Exception ex) when (Gateway(ex) is { } refused) { throw Error(refused.Code, refused.Message, Extensions(refused)); }
    }

    public async Task<ChatResult> ChatAsync(ChatInput input, CancellationToken ct)
    {
        using var span = BookCatalog.Source.StartActivity("chat");
        span?.SetTag("book.id", input.BookId);
        span?.SetTag("book.chapter", input.Chapter);
        span?.SetTag("chat.character", input.Character);
        span?.SetTag("chat.spoilers", input.Spoilers);
        if (user.Id is { } member) span?.SetTag("user.id", member);   // The gateway JWT's sub (what the site's OTLP ingest reads)
        try
        {
            var result = await Ask(input, span, ct);
            span?.SetTag("chat.tool_calls", result.ToolCalls.Count);
            span?.SetTag("chat.late_rows_suppressed", result.LateRowsSuppressed);
            span?.SetTag("gen_ai.usage.input_tokens", result.Usage.InputTokens);
            span?.SetTag("gen_ai.usage.output_tokens", result.Usage.OutputTokens);
            return result;
        }
        catch (GraphQLException ex)
        {
            span?.SetStatus(ActivityStatusCode.Error, ex.Errors.FirstOrDefault()?.Code);
            if (ex.Errors.FirstOrDefault()?.Extensions?.GetValueOrDefault("problem") is IReadOnlyDictionary<string, object?> problem)
            {
                span?.SetTag("problem.type", Problems.Slug((string)problem["type"]!));
                if (problem.GetValueOrDefault("reason") is string reason) span?.SetTag("problem.reason", reason);
            }
            throw;
        }
    }

    private async Task<ChatResult> Ask(ChatInput input, Activity? span, CancellationToken ct)
    {
        var available = await Catalog(ct); // Chat:Llm, else the Llm section; or the site's models
        if (available.Problems.Count > 0)
            throw Error("LLM_NOT_CONFIGURED", "No model is configured for chat (see README › Dev LLM provider): " + string.Join(" ", available.Problems));

        var book = catalog.Get(input.BookId); // UNKNOWN_BOOK
        if (!book.Graph.Chapters.ContainsKey(input.Chapter))
            throw Error("UNKNOWN_CHAPTER", $"Book '{input.BookId}' has no chapter {input.Chapter}.");
        var character = book.ChatCharacters.Count == 0 ? null : ChatCharacters.Resolve(book.ChatCharacters, input.Character);
        if (character is null)
            throw Error("UNKNOWN_CHARACTER", book.ChatCharacters.Count == 0
                ? $"Book '{input.BookId}' has no characters yet (no graph has been promoted for it)."
                : $"Book '{input.BookId}' has no character '{input.Character}'.");
        if (string.IsNullOrWhiteSpace(input.Message)) throw Error("EMPTY_MESSAGE", "The message is empty.");
        var queries = SampleQueries.ChatTools(input.BookId) ?? throw Error("UNKNOWN_BOOK", $"Book '{input.BookId}' has no chat queries.");
        var model = available.Find(input.Model) ?? throw Error("UNKNOWN_MODEL", $"There is no chat model '{input.Model}'.");
        span?.SetTag("chat.model", model.Id);

        var session = input.SessionId is { Length: > 0 } id ? sessions.Get(id, user.Id) : null;
        if (session is null || !session.Matches(input.BookId, input.Chapter, character, input.Spoilers, model.Id))
            session = sessions.Add(NewSession(book, character, input.Chapter, input.Spoilers, queries, model));
        span?.SetTag("chat.session_turn", session.Conversation.Turns + 1);

        ChatTurn turn;
        try
        {
            turn = await Turn(session, model, input.Message.Trim(), available.Models, allowFallback: true, ct);
        }
        catch (FallbackRequested f)
        {
            // The proxy's provider-unavailable named a fallback model: the same question once more on it, in a session that
            // stands in for the requested model (later messages continue it). A second failure is answered as usual.
            span?.SetTag("chat.model_fallback_retry", f.Model.Id);
            session = sessions.Add(NewSession(book, character, input.Chapter, input.Spoilers, queries, f.Model, fallbackFor: model.Id));
            turn = await Turn(session, f.Model, input.Message.Trim(), available.Models, allowFallback: false, ct);
        }

        var cost = session.CallContext?.CostUsd;
        if (cost is { } usd) span?.SetTag("chat.cost_usd", (double)usd);
        var fallback = session.FallbackFor is { } requested
            ? new ModelFallback(available.Models.FirstOrDefault(m => m.Id == requested)?.DisplayName ?? requested,
                available.Models.FirstOrDefault(m => m.Id == session.Model)?.DisplayName ?? session.Model)
            : ModelFallback.Parse(session.CallContext?.ModelFallback, available.Models);
        if (fallback is not null) span?.SetTag("chat.model_fallback", $"{fallback.Requested} -> {fallback.Used}");
        return new ChatResult(
            session.Id,
            turn.Answer,
            turn.ToolCalls.Select(c => new ChatToolCall(c.Tool, string.Join(", ", c.Args.Select(a => $"{a.Key}: \"{a.Value}\"")), c.Rows, c.ResolvedName,
                c.NodeIdList.Select(id => (int)id).ToList(), c.EdgeIdList.Select(id => (int)id).ToList())).ToList(),
            turn.LateRowsSuppressed,
            new ChatUsage((int)turn.InputTokens, (int)turn.OutputTokens, (double?)cost),
            turn.ModelId,
            budget.Current?.After(cost),
            fallback);
    }

    /// <summary>One turn on <paramref name="session"/>, its failures turned into problems.</summary>
    private async Task<ChatTurn> Turn(ChatSession session, ChatModel model, string message, IReadOnlyList<ChatModel> models, bool allowFallback, CancellationToken ct)
    {
        var timeout = TimeSpan.FromSeconds(config.GetValue("Chat:TimeoutSeconds", DefaultTimeoutSeconds));
        using var deadline = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        await session.Gate.WaitAsync(linked.Token);
        try
        {
            if (session.CallContext is { } before)
            {
                before.ModelFallback = null;
                before.ResetCost();   // This turn's calls only
            }
            return await session.Conversation.AskAsync(session.Conversation.Turns + 1, message, linked.Token);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw Error("TIMEOUT", $"The model did not answer within {timeout.TotalSeconds:0} s.");
        }
        catch (Exception ex) when (Gateway(ex) is { } refused)
        {
            // The site's LLM proxy said no (allowance, chat off, rate limit, sign-in, model): its code, never its text.
            // And its problem's typed extensions (reset time, retry-after, reason), never its provider.
            // provider-unavailable with a fallback model the site serves → once more on that model (the caller does it).
            if (allowFallback && refused.Code == GatewayRefusedException.ProviderUnavailable && refused.Extensions.GetValueOrDefault("fallbackModel") is string named
                && FindModel(models, named) is { } other && other.Id != model.Id)
                throw new FallbackRequested(other);
            throw Error(refused.Code, refused.Message, Extensions(refused));
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not GraphQLException && ProviderFailures.Classify(ex) is { } failure)
        {
            // The provider account can't serve (credits, key limit, quota, key). Standalone: the person running this copy is the one
            // who can fix it, so the message says how. Gateway (a proxy that forwarded the provider's answer): no provider, no fix-it.
            var extensions = new Dictionary<string, object?> { ["reason"] = failure.Reason };
            if (failure.RetryAfter is { } wait) extensions["retryAfterSeconds"] = (long)Math.Ceiling(wait.TotalSeconds);
            if (gateway.Enabled) throw Error(GatewayRefusedException.ProviderUnavailable, "The model service is unavailable right now.", extensions);
            if (ProviderFailures.Label(model.Provider) != ProviderFailures.Label(null)) extensions["provider"] = model.Provider;   // a known one, never a host
            throw Error(GatewayRefusedException.ProviderUnavailable, ProviderFailures.FixIt(ProviderFailures.Label(model.Provider), failure.Reason), extensions);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not GraphQLException && RetryOnTransientChatClient.TransientStatus(ex) == 429)
        {
            // Still rate-limited after the pipeline's retries (a provider's short window).
            var wait = RetryOnTransientChatClient.RetryAfter(ex) ?? TimeSpan.FromSeconds(20);
            throw Error(GatewayRefusedException.RateLimited, "The model provider is busy; wait a moment and ask again.",
                new Dictionary<string, object?> { ["retryAfterSeconds"] = (long)Math.Ceiling(Math.Min(wait.TotalSeconds, 60)) });
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not GraphQLException)
        {
            // Never the provider's message: it can carry the endpoint or deployment names.
            throw Error("LLM_ERROR", "The model call failed (" + Describe(ex) + ").");
        }
        finally
        {
            session.LastUsed = sessions.Now;
            session.Gate.Release();
        }

    }

    /// <summary>A chat model by the proxy's name for it (the model id, or the <c>chatModels</c> id), if the site serves it now.</summary>
    public static ChatModel? FindModel(IReadOnlyList<ChatModel> models, string name) =>
        models.FirstOrDefault(m => (m.Model == name || m.Id == name) && m.Available);

    /// <summary>Thrown by <see cref="Turn"/> when the proxy names a fallback model to try.</summary>
    private sealed class FallbackRequested(ChatModel model) : Exception("The model provider is unavailable; the site named a fallback model.")
    {
        public ChatModel Model { get; } = model;
    }

    /// <summary>The refusal's problem extensions; a budget refusal without the site's reset time gets the next UTC midnight.</summary>
    private Dictionary<string, object?> Extensions(GatewayRefusedException refused)
    {
        var extensions = new Dictionary<string, object?>(refused.Extensions);
        if (refused.Code == GatewayRefusedException.BudgetExhausted && extensions.GetValueOrDefault("resetsAtUtc") is null)
            extensions["resetsAtUtc"] = budget.Current?.ResetsAtUtc ?? Problems.NextUtcMidnight(sessions.Now);
        return extensions;
    }

    /// <summary>A gateway refusal, also when it arrives wrapped.</summary>
    private static GatewayRefusedException? Gateway(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is GatewayRefusedException r) return r;
            if (e is GatewayTokenMissingException) return new GatewayRefusedException(GatewayRefusedException.Unauthorized, 401, "Sign in to chat.");
        }
        return null;
    }

    private ChatSession NewSession(LoadedBook book, string character, int chapter, bool spoilers, IReadOnlyDictionary<string, string> queries, ChatModel model,
        string? fallbackFor = null)
    {
        var log = new SpanChatLog();
        long pos = book.Graph.Chapters[chapter].CharEnd;
        var setup = ChatSetup.Create(book.Entry, book.Graph, book.ChatCharacters, character, queries, book.Locations, chapter, spoilers, config,
            onCall: call => log.Tool(call, pos, spoilers), voice: book.Voice);
        if (setup.PackText is not null) log.Pack(setup.PackSections, setup.PackText.Length, pos, spoilers);

        // One client per session: its call log counts this conversation's tokens. No run folder (NoCallLog): spans only.
        var id = Guid.NewGuid().ToString("N");
        var context = new LlmCallContext { Run = NoCallLog.Instance, Stage = "chat", BookId = book.Entry.Id, Chapter = chapter };
        var client = clients.Create(model, id, context);
        return new ChatSession(id, book.Entry.Id, chapter, character, spoilers, setup.Conversation(client, log), client, sessions.Now, model.Id, user.Id)
        {
            CallContext = context,
            FallbackFor = fallbackFor,
        };
    }

    /// <summary>"HTTP 429", "HTTP 401", or the exception type for errors without a status.</summary>
    private static string Describe(Exception ex) => ex switch
    {
        ClientResultException { Status: > 0 } c => $"HTTP {c.Status}",
        Anthropic.Exceptions.AnthropicApiException a => $"HTTP {(int)a.StatusCode}",
        _ => ex.GetType().Name,
    };

    /// <summary>The code (as before) and its problem details (<see cref="Problems"/>).</summary>
    private static GraphQLException Error(string code, string message, IReadOnlyDictionary<string, object?>? extensions = null) =>
        Problems.Error(code, message, extensions);

    /// <summary>
    /// The characters the panel offers at a chapter: the book's chat roster (<c>books.json</c> <c>chat</c>, in roster order),
    /// only those mentioned by the chapter's end whatever the spoiler setting (the roster shrinks when the timeline moves
    /// back); with spoilers off also no late characters, by their display name; with
    /// spoilers on by their canonical name (both: the short name once the reader has met it, see ChatTools.DisplayName). Initials: the
    /// characters table's while the display name is the short name they were made from, else derived from the display name (
    /// also when the stored initials are null, empty or blank, as in a book promoted before build-db filled them).
    /// </summary>
    public static IReadOnlyList<ChatCharacterView> Characters(LoadedBook book, int chapter, bool spoilers)
    {
        if (!book.Graph.Chapters.TryGetValue(chapter, out var range))
            throw Error("UNKNOWN_CHAPTER", $"Book '{book.Entry.Id}' has no chapter {chapter}.");
        long pos = range.CharEnd;
        return book.Roster
            .Select((c, i) => (c, d: c.Node is { } n ? ChatTools.DisplayName(n, pos, spoilers, book.Ambiguous) : (Name: (string?)c.Name, Late: false),
                avatar: book.Entry.Avatars is { Length: > 0 } folder ? $"{folder}/{ChatCharacters.Slug(book.RosterNames[i])}.webp" : null))
            .Where(x => x.c.Node is null || ((spoilers || !x.d.Late) && (x.c.Node.Properties.GetValueOrDefault("evidenceStart") is not long s || s < pos)))
            .Select(x => new ChatCharacterView(x.c.Name, x.d.Name ?? x.c.Name,
                !string.IsNullOrWhiteSpace(x.c.Initials) && x.c.Initials is { } i && x.c.Node?.Properties.GetValueOrDefault("shortName") is string s && s == x.d.Name ? i : ChatCharacters.DeriveInitials(x.d.Name ?? x.c.Name),
                x.c.Node is { } node ? (int)node.Id : null, x.avatar))
            .ToList();
    }
}

/// <summary>The API's <see cref="IChatLog"/>: events on the current span (no run folder).</summary>
public sealed class SpanChatLog : IChatLog
{
    public void Pack(IReadOnlyList<ChatTools.PackSection> sections, int chars, long pos, bool spoilers) =>
        Activity.Current?.AddEvent(new ActivityEvent("chat.pack", tags: new ActivityTagsCollection
        {
            ["chat.pack.chars"] = chars,
            ["chat.pack.rows"] = sections.Sum(s => s.Rows),
            ["chat.late_rows_suppressed"] = sections.Sum(s => s.LateRowsSuppressed),
        }));

    public void Tool(ToolCallRecord call, long pos, bool spoilers) =>
        Activity.Current?.AddEvent(new ActivityEvent("chat.tool", tags: new ActivityTagsCollection
        {
            ["tool.name"] = call.Tool,
            ["tool.args"] = string.Join(", ", call.Args.Select(a => $"{a.Key}: {a.Value}")),
            ["query.rows"] = call.Rows,
            ["chat.resolved_name"] = call.ResolvedName,
            ["chat.late_rows_suppressed"] = call.LateRowsSuppressed,
        }));

    public void Turn(ChatTurn turn) { }
}
