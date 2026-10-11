namespace TemporalGraph.Llm;

/// <summary>The chat client's OpenTelemetry source and the tag names the pipeline sets on its spans.</summary>
public static class LlmTelemetry
{
    /// <summary>Source and meter name of the chat client's OpenTelemetry wrapper (UseOpenTelemetry).</summary>
    public const string SourceName = "TemporalGraph.Llm";

    // Tag names (the CLI's IndexingTelemetry uses the same ones on its stage spans).
    public const string RunId = "run.id";
    public const string Stage = "pipeline.stage";
    public const string BookId = "book.id";
    public const string Chapter = "book.chapter";
    public const string ChunkIndex = "chunk.index";
    public const string SkillName = "skill.name";
    public const string SkillVersion = "skill.version";
    /// <summary>The Azure content filter sent as x-policy-id (Llm:PolicyId), on each GenAI call span.</summary>
    public const string PolicyId = "llm.policy_id";
}

/// <summary>
/// Where the call log writes one JSON line per LLM call (attempt) and per retry: the CLI's run folder (<c>calls.jsonl</c>),
/// or nothing (the API: spans only, see <see cref="NoCallLog"/>).
/// </summary>
public interface ILlmCallLog
{
    /// <summary>The run id put on spans and lines; null when there is no run (the API).</summary>
    string? RunId { get; }

    void AppendCall(object entry);
}

/// <summary>An <see cref="ILlmCallLog"/> that keeps nothing (token totals are still counted by <see cref="CallLogChatClient"/>).</summary>
public sealed class NoCallLog : ILlmCallLog
{
    public static readonly NoCallLog Instance = new();

    public string? RunId => null;

    public void AppendCall(object entry) { }
}
