using System.Diagnostics;

namespace TemporalGraph.Cli.Telemetry;

/// <summary>Spans for indexing: one per command, per stage and per chapter.</summary>
public static class IndexingTelemetry
{
    public const string SourceName = "TemporalGraph.Indexing";

    /// <summary>Source and meter name of the chat client's OpenTelemetry wrapper (UseOpenTelemetry).</summary>
    public const string LlmSourceName = TemporalGraph.Llm.LlmTelemetry.SourceName;

    public static readonly ActivitySource Source = new(SourceName);

    // Tag names.
    public const string RunId = TemporalGraph.Llm.LlmTelemetry.RunId;
    public const string Stage = TemporalGraph.Llm.LlmTelemetry.Stage;
    public const string BookId = TemporalGraph.Llm.LlmTelemetry.BookId;
    public const string Chapter = TemporalGraph.Llm.LlmTelemetry.Chapter;
    public const string ChunkIndex = TemporalGraph.Llm.LlmTelemetry.ChunkIndex;
    public const string SkillName = TemporalGraph.Llm.LlmTelemetry.SkillName;
    public const string SkillVersion = TemporalGraph.Llm.LlmTelemetry.SkillVersion;
    /// <summary>The configured sampling settings on stage spans (each GenAI call span also has gen_ai.request.temperature / .seed).</summary>
    public const string LlmTemperature = "llm.temperature";
    public const string LlmSeed = "llm.seed";
    /// <summary>The Azure content filter sent as x-policy-id (Llm:PolicyId), on stage spans and each GenAI call span.</summary>
    public const string LlmPolicyId = TemporalGraph.Llm.LlmTelemetry.PolicyId;
    public const string LlmProfile = "llm.profile";
}
