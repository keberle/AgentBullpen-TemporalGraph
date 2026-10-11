using System.Diagnostics;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using TemporalGraph.Llm;

namespace TemporalGraph.Chat;

/// <summary>One question and answer, with the tool calls made for it and the tokens of its LLM calls.</summary>
/// <param name="LateRowsSuppressed">Rows hidden as not yet revealed in this turn: its tool calls, plus the knowledge
/// pack's when the pack was sent with this turn.</param>
/// <param name="NeverSaid">The never-say words the first answer used (then the turn was asked once more); empty if none.</param>
/// <param name="NeverSayRemaining">The never-say words still in the answer after that retry.</param>
public sealed record ChatTurn(int Turn, string Question, string Answer, IReadOnlyList<ToolCallRecord> ToolCalls, long InputTokens, long OutputTokens, string? ModelId,
    int LateRowsSuppressed = 0, IReadOnlyList<string>? NeverSaid = null, IReadOnlyList<string>? NeverSayRemaining = null)
{
    public bool NeverSayRetried => NeverSaid is { Count: > 0 };
}

/// <summary>
/// What a conversation reports as it goes. The CLI writes <c>runs/&lt;runId&gt;/chat.jsonl</c>; the API adds span
/// events. The tools report each call through their <c>onCall</c> callback, which the caller wires to <see cref="Tool"/>.
/// </summary>
public interface IChatLog
{
    /// <summary>The knowledge pack was built (once, before the first question).</summary>
    void Pack(IReadOnlyList<ChatTools.PackSection> sections, int chars, long pos, bool spoilers);

    /// <summary>One tool call.</summary>
    void Tool(ToolCallRecord call, long pos, bool spoilers);

    /// <summary>One question and answer.</summary>
    void Turn(ChatTurn turn);
}

/// <summary>A chat log that keeps nothing.</summary>
public sealed class NoChatLog : IChatLog
{
    public static readonly NoChatLog Instance = new();

    public void Pack(IReadOnlyList<ChatTools.PackSection> sections, int chars, long pos, bool spoilers) { }

    public void Tool(ToolCallRecord call, long pos, bool spoilers) { }

    public void Turn(ChatTurn turn) { }
}

/// <summary>The chat library's span source (one span per turn).</summary>
public static class ChatTelemetry
{
    public const string SourceName = "TemporalGraph.Chat";

    public static readonly ActivitySource Source = new(SourceName);

    /// <summary>The chat library's meter (the same name as the span source).</summary>
    public static readonly System.Diagnostics.Metrics.Meter Meter = new(SourceName);

    /// <summary>Answers asked again because they used a never-say word of the book.</summary>
    public static readonly System.Diagnostics.Metrics.Counter<long> NeverSayRetries =
        Meter.CreateCounter<long>("chat.never_say.retries", description: "Answers asked again because they used a word from the book's never-say list");
}

/// <summary>
/// The conversation: one Agent Framework agent (<c>AsAIAgent</c> with the system prompt and the query tools) and one
/// <see cref="AgentSession"/>, so every question is asked in the same conversation. Each turn is reported to the
/// <see cref="IChatLog"/>.
/// <list type="bullet">
/// <item><b>Knowledge pack</b>: a system-role message added before the first question only; the session keeps it in
/// the history, so every later request still carries it (once). The chapter and spoilers can't change within a conversation.</item>
/// <item><b>RequireToolFirst</b>: <c>ChatToolMode.RequireAny</c> through <see cref="ChatClientAgentRunOptions.ChatOptions"/>
/// on every run. MEAI 10.10's <c>FunctionInvokingChatClient</c> resets a required tool mode to none (auto) after the first
/// round of a request, so only each turn's first model call is forced.</item>
/// </list>
/// Not thread-safe: one question at a time (the API serializes the messages of a session).
/// </summary>
public sealed class ChatConversation
{
    public const string AgentName = "character-chat";

    private readonly IChatClient _client;
    private readonly ChatTools _tools;
    private readonly IChatLog _log;
    private readonly AIAgent _agent;
    private readonly string? _knowledgePack;
    private readonly int _packLateRowsSuppressed;
    private readonly bool _requireToolFirst;
    private readonly IReadOnlyList<string> _neverSay;
    // The turns of the conversation this one continues (the chat console's .chapter and .spoilers build a new pack and
    // new tools at the new position, and the talk goes on); sent once, after the pack and before the first question.
    private readonly IReadOnlyList<(string Question, string Answer)> _history;
    private AgentSession? _session;
    private bool _packSent;

    /// <param name="knowledgePack">The pack text, sent once before the first question; null: no pack.</param>
    /// <param name="packLateRowsSuppressed">The pack's hidden late rows, added to the first turn's count.</param>
    /// <param name="requireToolFirst">Force a tool call on the first model call of each turn.</param>
    /// <param name="neverSay">The book's never-say words (<see cref="BookVoice.NeverSay"/>). An answer that uses one is asked
    /// once more in the same conversation with <see cref="Reminder"/>; the turn keeps the second answer.</param>
    public ChatConversation(IChatClient client, ChatTools tools, string instructions, IChatLog log, string? knowledgePack = null, bool requireToolFirst = false,
        int packLateRowsSuppressed = 0, IReadOnlyList<string>? neverSay = null, IReadOnlyList<(string Question, string Answer)>? history = null)
    {
        _neverSay = neverSay ?? [];
        _history = history ?? [];
        _client = client;
        _tools = tools;
        _log = log;
        _knowledgePack = knowledgePack;
        _packLateRowsSuppressed = packLateRowsSuppressed;
        _requireToolFirst = requireToolFirst;
        _agent = client.AsAIAgent(instructions: instructions, name: AgentName, tools: tools.AsAITools());
    }

    /// <summary>The tools (their position, gate and calls so far).</summary>
    public ChatTools Tools => _tools;

    /// <summary>The number of turns asked so far.</summary>
    public int Turns { get; private set; }

    /// <summary>Asks one question in the conversation and reports the turn.</summary>
    public async Task<ChatTurn> AskAsync(int turn, string question, CancellationToken ct)
    {
        _session ??= await _agent.CreateSessionAsync(ct);
        _tools.Turn = turn;
        var before = _tools.Calls.Count;
        var log = _client.GetService<CallLogChatClient>();
        var (inBefore, outBefore) = (log?.InputTokens ?? 0, log?.OutputTokens ?? 0);

        using var span = ChatTelemetry.Source.StartActivity("chat turn");
        span?.SetTag("chat.turn", turn);
        var messages = new List<ChatMessage>();
        var packNow = _knowledgePack is not null && !_packSent;
        if (packNow)
        {
            messages.Add(new ChatMessage(ChatRole.System, _knowledgePack!));
            _packSent = true;
        }
        if (Turns == 0)
            foreach (var (q, a) in _history)
            {
                messages.Add(new ChatMessage(ChatRole.User, q));
                messages.Add(new ChatMessage(ChatRole.Assistant, a));
            }
        messages.Add(new ChatMessage(ChatRole.User, question));
        var options = _requireToolFirst ? new ChatClientAgentRunOptions(new ChatOptions { ToolMode = ChatToolMode.RequireAny }) : null;
        var response = await _agent.RunAsync(messages, _session, options, ct);
        var answer = response.Text;

        // A word no one in the book would say → one retry with a one-line reminder, in the same conversation.
        var said = BookVoice.NeverSayHits(answer, _neverSay);
        IReadOnlyList<string> remaining = [];
        if (said.Count > 0)
        {
            ChatTelemetry.NeverSayRetries.Add(1);
            span?.SetTag("chat.never_say.words", string.Join(", ", said));
            response = await _agent.RunAsync([new ChatMessage(ChatRole.User, Reminder(said))], _session, null, ct);
            answer = response.Text;
            remaining = BookVoice.NeverSayHits(answer, _neverSay);
            if (remaining.Count > 0) span?.SetTag("chat.never_say.remaining", string.Join(", ", remaining));
        }
        span?.SetTag("chat.never_say.retried", said.Count > 0);
        Turns++;

        // Tokens: every LLM call of the turn (the tool-call round trips too), from the call log; else the response's usage.
        var input = log is null ? response.Usage?.InputTokenCount ?? 0 : log.InputTokens - inBefore;
        var output = log is null ? response.Usage?.OutputTokenCount ?? 0 : log.OutputTokens - outBefore;
        var calls = _tools.Calls.Skip(before).ToList();
        var suppressed = calls.Sum(c => c.LateRowsSuppressed) + (packNow ? _packLateRowsSuppressed : 0);
        var result = new ChatTurn(turn, question, answer, calls, input, output, ModelId(response), suppressed, said, remaining);
        span?.SetTag("chat.tool_calls", calls.Count);
        span?.SetTag("chat.late_rows_suppressed", suppressed);
        _log.Turn(result);
        return result;
    }

    /// <summary>The one-line reminder for a retry.</summary>
    public static string Reminder(IReadOnlyList<string> words) =>
        $"Say that again without {string.Join(" or ", words.Select(w => $"\"{w}\""))}: no one in your book speaks that way. Keep the same meaning and your own manner.";

    /// <summary>AgentResponse has no model id; a ChatClientAgent's raw representation is the ChatResponse, which does.</summary>
    public static string? ModelId(AgentResponse response) => (response.RawRepresentation as ChatResponse)?.ModelId;
}
