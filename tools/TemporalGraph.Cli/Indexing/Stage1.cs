using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using TemporalGraph.Llm;
using TemporalGraph.Cli.Runs;
using TemporalGraph.Cli.Telemetry;

namespace TemporalGraph.Cli.Indexing;

// Stage 1 — mention extraction. One structured-output call per chunk (skills/extract-mentions.md), an optional
// second "what did you miss" pass (--glean, after GraphRAG's gleanings: https://arxiv.org/html/2404.16130),
// a code evidence check on every mention, self-consistency voting over k samples per chunk, and dedupe of
// the chunk overlaps by offset.

[JsonConverter(typeof(JsonStringEnumConverter<MentionType>))]
public enum MentionType { Character, Location }

/// <summary>Structured output of one extraction call. Strict-mode friendly: every field required, no nulls.</summary>
public sealed record MentionExtraction(List<ExtractedMention> Mentions);

/// <param name="Text">The mention exactly as it appears in the chunk.</param>
/// <param name="Type">Character (a person, or a group acting as a character) or Location (a place).</param>
/// <param name="Evidence">A verbatim quote from the chunk, one sentence or less, containing the mention.</param>
public sealed record ExtractedMention(string Text, MentionType Type, string Evidence);

/// <summary>An accepted mention: offsets are into the book text (UTF-16 code units, [start, end)).</summary>
/// <param name="Votes">How many of the chunk's samples found it (see <see cref="Stage1.Vote"/>); 1 without voting.</param>
public sealed record Mention(
    int Id, string Text, MentionType Type, int Chunk, int Chapter, string Pass,
    int CharStart, int CharEnd, string Evidence, EvidenceMatch EvidenceMatch, int Votes = 1);

/// <summary>
/// Self-consistency voting: each chunk is extracted <paramref name="Samples"/> times (k); a mention is kept when at
/// least <paramref name="MinVotes"/> samples (m) found it. k = 1, m = 1 is a single extraction.
/// </summary>
public sealed record VotingSettings(int Samples = 1, int MinVotes = 1)
{
    public IReadOnlyList<string> Validate() =>
        Samples < 1 ? ["Indexing:Stage1:Samples must be at least 1."]
        : MinVotes < 1 || MinVotes > Samples ? [$"Indexing:Stage1:MinVotes must be between 1 and Samples ({Samples})."]
        : [];
}

/// <summary>A mention rejected by the evidence check (also written to failures.jsonl as "quote mismatch").</summary>
public sealed record RejectedMention(string Text, MentionType Type, int Chunk, int Chapter, string Pass, string Evidence, string Reason, EvidenceMatch? BestMatch, int Sample = 0);

/// <param name="Returned">Mentions returned by all samples together (first pass).</param>
/// <param name="Accepted">Voted mentions with at least MinVotes votes (before the overlap dedupe).</param>
/// <param name="Rejected">Evidence-check rejects over all samples.</param>
public sealed record ChunkSummary(int Index, int Chapter, int CharStart, int CharEnd, int Returned, int Gleaned, int Accepted, int Rejected);

/// <param name="Mentions">The mentions with at least <paramref name="MinVotes"/> votes, deduped across chunk overlaps.</param>
/// <param name="Pool">Every voted mention of every chunk, whatever its votes, before the dedupe: any m can be
/// re-derived from it with <see cref="Stage1.WithMinVotes"/> (no new calls). Null in files written before vote pools were kept.</param>
public sealed record Stage1Result(
    string BookId, int FirstChapter, int LastChapter, bool Glean, double EvidenceThreshold,
    IReadOnlyList<ChunkSummary> Chunks, IReadOnlyList<Mention> Mentions, IReadOnlyList<RejectedMention> Rejected,
    int DuplicatesDropped, int Samples = 1, int MinVotes = 1, IReadOnlyList<Mention>? Pool = null);

public static class Stage1
{
    public const string StageName = "stage-1";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
    };

    /// <summary>
    /// Structured output: JSON schema response format, with "strict" in AdditionalProperties so the OpenAI
    /// adapter sends strict: true and applies its strict-schema transform (all required, additionalProperties false).
    /// </summary>
    public static ChatOptions StrictOptions() => new() { AdditionalProperties = new() { ["strict"] = true } };

    public static string GleanPrompt =>
        "Some mentions in this chunk may have been missed. Return only the mentions in the same chunk that are " +
        "not already in your previous answer, in the same format. Return an empty list if there are none.";

    public static string ChunkPrompt(ChunkInfo chunk, string chunkText) =>
        $"Chunk {chunk.Index} (chapter {chunk.Chapter}). Return the mentions in this chunk.\n<<<\n{chunkText}\n>>>";

    public static async Task<Stage1Result> RunAsync(
        IChatClient client, LlmCallContext? context, SkillFile skill, Stage0Result stage0, string bookText,
        int firstChapter, int lastChapter, bool glean, double threshold, RunFolder? run, CancellationToken ct,
        VotingSettings? voting = null)
    {
        voting ??= new VotingSettings();
        if (voting.Validate() is [var problem, ..]) throw new ArgumentException(problem, nameof(voting));
        var chunks = stage0.Chunks.Where(c => c.Chapter >= firstChapter && c.Chapter <= lastChapter).ToList();
        var summaries = new List<ChunkSummary>();
        var pool = new List<Mention>();
        var rejected = new List<RejectedMention>();

        foreach (var chunk in chunks)
        {
            using var span = IndexingTelemetry.Source.StartActivity($"chunk {chunk.Index}");
            span?.SetTag(IndexingTelemetry.RunId, run?.RunId);
            span?.SetTag(IndexingTelemetry.Stage, StageName);
            span?.SetTag(IndexingTelemetry.BookId, stage0.BookId);
            span?.SetTag(IndexingTelemetry.Chapter, chunk.Chapter);
            span?.SetTag(IndexingTelemetry.ChunkIndex, chunk.Index);
            span?.SetTag(IndexingTelemetry.SkillName, skill.Name);
            span?.SetTag(IndexingTelemetry.SkillVersion, skill.Version);
            if (context is not null)
            {
                context.BookId = stage0.BookId;
                context.Chapter = chunk.Chapter;
                context.ChunkIndex = chunk.Index;
                context.SkillName = skill.Name;
                context.SkillVersion = skill.Version.ToString();
            }

            var chunkText = bookText[chunk.CharStart..chunk.CharEnd];

            // k independent samples of the same request (same messages, same sampling settings), in parallel.
            async Task<(List<ExtractedMention> First, List<ExtractedMention> Gleaned)> Sample()
            {
                List<ChatMessage> messages =
                [
                    new(ChatRole.System, skill.Text),
                    new(ChatRole.User, ChunkPrompt(chunk, chunkText)),
                ];
                var first = await client.GetResponseAsync<MentionExtraction>(messages, Json, StrictOptions(), useJsonSchemaResponseFormat: true, ct);
                if (!glean) return (first.Result.Mentions, []);
                messages.AddRange(first.Messages);
                messages.Add(new ChatMessage(ChatRole.User, GleanPrompt));
                var second = await client.GetResponseAsync<MentionExtraction>(messages, Json, StrictOptions(), useJsonSchemaResponseFormat: true, ct);
                return (first.Result.Mentions, second.Result.Mentions);
            }
            var samples = await Task.WhenAll(Enumerable.Range(0, voting.Samples).Select(_ => Sample()));

            var rejectedBefore = rejected.Count;
            var checkedSamples = new List<IReadOnlyList<Mention>>();
            for (var sample = 0; sample < samples.Length; sample++)
            {
                var sampleAccepted = new List<Mention>();
                foreach (var (m, pass) in samples[sample].First.Select(m => (m, "first")).Concat(samples[sample].Gleaned.Select(m => (m, "glean"))))
                {
                    var check = Check(m, chunk, chunkText, bookText, threshold);
                    if (check.Mention is { } ok)
                    {
                        sampleAccepted.Add(ok with { Pass = pass });
                        continue;
                    }
                    var r = check.Rejected! with { Pass = pass, Sample = sample };
                    rejected.Add(r);
                    run?.AppendFailure(new
                    {
                        type = Scoring.FailureTags.QuoteMismatch,
                        stage = StageName,
                        runId = run.RunId,
                        bookId = stage0.BookId,
                        chapter = chunk.Chapter,
                        chunkIndex = chunk.Index,
                        pass,
                        sample,
                        mention = new { text = m.Text, type = m.Type.ToString(), evidence = m.Evidence },
                        reason = r.Reason,
                        bestMatch = r.BestMatch,
                        threshold,
                        traceId = span?.TraceId.ToString(),
                    });
                }
                checkedSamples.Add(sampleAccepted);
            }
            var voted = Vote(checkedSamples);
            pool.AddRange(voted);

            var summary = new ChunkSummary(chunk.Index, chunk.Chapter, chunk.CharStart, chunk.CharEnd,
                samples.Sum(x => x.First.Count), samples.Sum(x => x.Gleaned.Count), voted.Count(v => v.Votes >= voting.MinVotes), rejected.Count - rejectedBefore);
            summaries.Add(summary);
            span?.SetTag("mentions.returned", summary.Returned);
            span?.SetTag("mentions.gleaned", summary.Gleaned);
            span?.SetTag("mentions.accepted", summary.Accepted);
            span?.SetTag("mentions.rejected", summary.Rejected);
            span?.SetTag("stage1.samples", voting.Samples);
        }

        pool = pool.Select((m, i) => m with { Id = i + 1 }).ToList();
        var (deduped, dropped) = Dedupe(pool.Where(m => m.Votes >= voting.MinVotes));
        return new Stage1Result(stage0.BookId, firstChapter, lastChapter, glean, threshold, summaries, deduped, rejected, dropped,
            voting.Samples, voting.MinVotes, pool);
    }

    /// <summary>
    /// Self-consistency voting over one chunk's samples (each already evidence-checked). Two mentions from different
    /// samples are the same mention when they have the same type, the same normalized surface text
    /// (<see cref="Stage2.NormalizeName"/>), and overlapping evidence spans (the matched quotes in the book text).
    /// <list type="bullet">
    /// <item>Within one sample, mentions with the same type and offsets count once.</item>
    /// <item>Samples are taken in order; a mention joins the first cluster that matches it and has no mention from its
    /// sample yet, preferring a cluster with a member at the same [start, end). Otherwise it starts a new cluster.</item>
    /// <item>Votes = the number of samples in the cluster. The cluster's mention is its most common [start, end)
    /// (ties: the earliest sample), with that member's evidence.</item>
    /// </list>
    /// Returns one mention per cluster, in book order, with <see cref="Mention.Votes"/> set; nothing is dropped here.
    /// </summary>
    public static List<Mention> Vote(IReadOnlyList<IReadOnlyList<Mention>> samples)
    {
        var clusters = new List<List<(int Sample, Mention Mention)>>();
        static bool Overlaps(EvidenceMatch a, EvidenceMatch b) => a.CharStart < b.CharEnd && b.CharStart < a.CharEnd;
        for (var s = 0; s < samples.Count; s++)
        {
            var seen = new HashSet<(MentionType, int, int)>();
            foreach (var m in samples[s].OrderBy(m => m.CharStart))
            {
                if (!seen.Add((m.Type, m.CharStart, m.CharEnd))) continue;
                var key = Stage2.NormalizeName(m.Text);
                var open = clusters.Where(c => c.All(x => x.Sample != s) &&
                    c.Any(x => x.Mention.Type == m.Type && Stage2.NormalizeName(x.Mention.Text) == key && Overlaps(x.Mention.EvidenceMatch, m.EvidenceMatch))).ToList();
                var target = open.FirstOrDefault(c => c.Any(x => x.Mention.CharStart == m.CharStart && x.Mention.CharEnd == m.CharEnd)) ?? open.FirstOrDefault();
                if (target is null) clusters.Add([(s, m)]);
                else target.Add((s, m));
            }
        }
        return clusters.Select(c =>
        {
            var best = c.GroupBy(x => (x.Mention.CharStart, x.Mention.CharEnd))
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Min(x => x.Sample)).First()
                .OrderBy(x => x.Sample).First().Mention;
            return best with { Votes = c.Select(x => x.Sample).Distinct().Count() };
        }).OrderBy(m => m.CharStart).ThenBy(m => m.CharEnd).ToList();
    }

    /// <summary>
    /// The same Stage 1 output at another m: the pool's mentions with at least <paramref name="minVotes"/> votes,
    /// deduped. Requires a pool (files that keep one) and m ≤ the samples taken.
    /// </summary>
    public static Stage1Result WithMinVotes(Stage1Result r, int minVotes)
    {
        if (r.Pool is null) throw new InvalidOperationException("This stage-1.json has no vote pool (written before vote pools were kept); it can't be re-filtered.");
        if (minVotes < 1 || minVotes > r.Samples) throw new ArgumentOutOfRangeException(nameof(minVotes), $"--min-votes must be between 1 and the run's samples ({r.Samples}).");
        var (deduped, dropped) = Dedupe(r.Pool.Where(m => m.Votes >= minVotes));
        var chunks = r.Chunks.Select(c => c with { Accepted = r.Pool.Count(m => m.Chunk == c.Index && m.Votes >= minVotes) }).ToList();
        return r with { MinVotes = minVotes, Mentions = deduped, DuplicatesDropped = dropped, Chunks = chunks };
    }

    public sealed record CheckOutcome(Mention? Mention, RejectedMention? Rejected);

    /// <summary>
    /// The evidence check: the quote must match the chunk (exact, or fuzzy at or above <paramref name="threshold"/>),
    /// and the mention's surface text must occur inside the matched evidence. Anything else is rejected.
    /// </summary>
    public static CheckOutcome Check(ExtractedMention m, ChunkInfo chunk, string chunkText, string bookText, double threshold)
    {
        RejectedMention Reject(string reason, EvidenceMatch? best) =>
            new(m.Text, m.Type, chunk.Index, chunk.Chapter, "first", m.Evidence, reason, best);

        var match = EvidenceChecker.Find(m.Evidence, chunkText, chunk.CharStart);
        if (match is null) return new(null, Reject("empty evidence", null));
        if (match.Score < threshold)
            return new(null, Reject($"evidence not in chunk (best {match.Method} score {match.Score:0.###} < {threshold})", match));

        var surface = EvidenceChecker.LocateSurface(m.Text, bookText, match);
        if (surface is null) return new(null, Reject("mention text not found inside the matched evidence", match));

        var (start, end) = surface.Value;
        return new(new Mention(0, m.Text, m.Type, chunk.Index, chunk.Chapter, "first", start, end, m.Evidence, match), null);
    }

    /// <summary>
    /// Chunks overlap by ~100 tokens, so the same mention can come back from two chunks (or from both passes).
    /// Mentions with the same type and the same [start, end) are one mention; the first is kept. Ids are renumbered.
    /// </summary>
    public static (List<Mention> Mentions, int Dropped) Dedupe(IEnumerable<Mention> mentions)
    {
        var seen = new HashSet<(MentionType, int, int)>();
        var kept = new List<Mention>();
        var dropped = 0;
        foreach (var m in mentions.OrderBy(m => m.CharStart).ThenBy(m => m.Chunk).ThenBy(m => m.Pass == "first" ? 0 : 1))
        {
            if (seen.Add((m.Type, m.CharStart, m.CharEnd))) kept.Add(m with { Id = kept.Count + 1 });
            else dropped++;
        }
        return (kept, dropped);
    }

    /// <summary>Evidence score histogram for stage-1.json (accepted and rejected together).</summary>
    public static object ScoreDistribution(Stage1Result r)
    {
        var scores = r.Mentions.Select(m => m.EvidenceMatch.Score)
            .Concat(r.Rejected.Where(x => x.BestMatch is not null).Select(x => x.BestMatch!.Score)).ToList();
        string Bucket(double s) => s >= 1.0 ? "1.00" : s >= 0.95 ? "0.95-0.99" : s >= 0.9 ? "0.90-0.94" : s >= 0.8 ? "0.80-0.89" : s >= 0.5 ? "0.50-0.79" : "<0.50";
        string[] order = ["1.00", "0.95-0.99", "0.90-0.94", "0.80-0.89", "0.50-0.79", "<0.50"];
        var counts = scores.GroupBy(Bucket).ToDictionary(g => g.Key, g => g.Count());
        return new
        {
            scored = scores.Count,
            exact = r.Mentions.Count(m => m.EvidenceMatch.Method == "exact") + r.Rejected.Count(x => x.BestMatch?.Method == "exact"),
            buckets = order.ToDictionary(b => b, b => counts.GetValueOrDefault(b)),
            min = scores.Count > 0 ? scores.Min() : (double?)null,
        };
    }
}
