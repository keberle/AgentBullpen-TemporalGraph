using TemporalGraph.Chat;

namespace TemporalGraph.Cli.Runs;

/// <summary>
/// The CLI's <see cref="IChatLog"/>: one line per knowledge pack, tool call and turn in <c>runs/&lt;runId&gt;/chat.jsonl</c>.
/// </summary>
public sealed class ChatJsonl(RunFolder run) : IChatLog
{
    public const string FileName = "chat.jsonl";

    private static readonly Lock FileLock = new();

    public void Pack(IReadOnlyList<ChatTools.PackSection> sections, int chars, long pos, bool spoilers) =>
        Append(new
        {
            type = "pack",
            sections = sections.Select(s => new { heading = s.Heading, tool = s.Tool, rows = s.Rows, lateRowsSuppressed = s.LateRowsSuppressed }),
            chars,
            pos,
            spoilers,
            lateRowsSuppressed = sections.Sum(s => s.LateRowsSuppressed),
        });

    public void Tool(ToolCallRecord call, long pos, bool spoilers) =>
        Append(new
        {
            type = "tool",
            turn = call.Turn,
            tool = call.Tool,
            args = call.Args,
            rows = call.Rows,
            elapsedMs = call.ElapsedMs,
            pos,
            spoilers,
            note = call.UnknownName ? "unknown name" : call.Truncated ? "truncated" : null,
            resolvedName = call.ResolvedName,
            lateRowsSuppressed = call.LateRowsSuppressed,
            unknownRowsSuppressed = call.UnknownRowsSuppressed,
            nodeIds = call.NodeIdList,
            edgeIds = call.EdgeIdList,
        });

    public void Turn(ChatTurn turn) =>
        Append(new
        {
            type = "turn",
            turn = turn.Turn,
            question = turn.Question,
            answer = turn.Answer,
            toolCalls = turn.ToolCalls.Select(c => new { tool = c.Tool, args = c.Args, rows = c.Rows }),
            lateRowsSuppressed = turn.LateRowsSuppressed,
            inputTokens = turn.InputTokens,
            outputTokens = turn.OutputTokens,
            model = turn.ModelId,
            neverSaid = turn.NeverSaid,
            neverSayRemaining = turn.NeverSayRemaining,
        });

    private void Append(object entry)
    {
        var line = System.Text.Json.JsonSerializer.Serialize(entry, new System.Text.Json.JsonSerializerOptions(RunFolder.Json) { WriteIndented = false });
        lock (FileLock) File.AppendAllText(run.File(FileName), line + "\n");
    }
}
