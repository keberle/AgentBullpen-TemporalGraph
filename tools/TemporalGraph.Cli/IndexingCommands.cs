using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using Spectre.Console.Cli;
using TemporalGraph.Cli.Crosscheck;
using TemporalGraph.Cli.Gold;
using TemporalGraph.Cli.Indexing;
using TemporalGraph.Llm;
using TemporalGraph.Cli.Runs;
using TemporalGraph.Cli.Telemetry;
using TemporalGraph.Data;

namespace TemporalGraph.Cli;

/// <summary>Shared pieces of the indexing commands (Stage 1, Stage 2, cross-check, review, gold import).</summary>
public static class IndexingShared
{
    /// <summary>The <c>Indexing</c> config section (appsettings.json).</summary>
    /// <param name="Stage3DedupeThreshold"><c>Indexing:Stage3:DedupeThreshold</c>: the token-Jaccard threshold of the run-wide node
    /// dedupe (default 0.6).</param>
    /// <param name="DefaultLlmProfile"><c>Indexing:Llm</c>: the profile the indexing stages use without <c>--llm</c> (haiku55);
    /// empty → the <c>Llm</c> section.</param>
    public sealed record IndexingSettings(double EvidenceThreshold, ResolveSettings Resolve, VotingSettings Voting,
        VotingSettings? Stage3Voting = null, VotingSettings? Stage4Voting = null, double Stage3DedupeThreshold = RunDedupe.DefaultThreshold,
        string? DefaultLlmProfile = null)
    {
        /// <summary>
        /// Defaults when <c>Indexing:Stage1</c> is absent, the same as the template: k = 5, m = 4. Measured on ch. 1–3
        /// with k = 5: m = 5 had the best F1 (0.740 ± 0.021), m = 4 was within 0.003 with a quarter of the spread (± 0.005),
        /// so m = 4.
        /// </summary>
        public const int DefaultSamples = 5;
        public const int DefaultMinVotes = 4;

        /// <summary>
        /// Stage 3 (facts and edges) voting defaults when <c>Indexing:Stage3</c> is absent; the same as the template. m was
        /// chosen by stability on ch. 1–3 with k = 5 (no facts gold yet): m = 2 had the highest share of items found by all 3 runs (0.414).
        /// </summary>
        public const int DefaultStage3Samples = 5;
        public const int DefaultStage3MinVotes = 2;

        /// <summary>
        /// Stage 4 (story date) voting defaults when <c>Indexing:Stage4</c> is absent; the same as the template. A date
        /// needs a majority of 5 samples (identical-share of dated items across 5 frozen runs 0.067 → 0.375).
        /// </summary>
        public const int DefaultStage4Samples = 5;
        public const int DefaultStage4MinVotes = 3;

        public static IndexingSettings From(IConfiguration config)
        {
            var s = config.GetSection("Indexing");
            var r = s.GetSection("Resolve");
            var v = s.GetSection("Stage1");
            var d = new ResolveSettings();
            return new IndexingSettings(
                s.GetValue("EvidenceThreshold", 0.9),
                new ResolveSettings(r["Similarity"] ?? d.Similarity, r.GetValue("JaroWinklerThreshold", d.JaroWinklerThreshold), r.GetValue("EvidencePerSide", d.EvidencePerSide)),
                new VotingSettings(v.GetValue("Samples", DefaultSamples), v.GetValue("MinVotes", DefaultMinVotes)),
                new VotingSettings(s.GetValue("Stage3:Samples", DefaultStage3Samples), s.GetValue("Stage3:MinVotes", DefaultStage3MinVotes)),
                new VotingSettings(s.GetValue("Stage4:Samples", DefaultStage4Samples), s.GetValue("Stage4:MinVotes", DefaultStage4MinVotes)),
                s.GetValue("Stage3:DedupeThreshold", RunDedupe.DefaultThreshold),
                s["Llm"]?.Trim() is { Length: > 0 } p && !p.StartsWith('<') ? p : null);
        }
    }

    public static (int First, int Last)? ParseChapters(string spec)
    {
        var parts = spec.Split('-');
        if (parts.Length == 1 && int.TryParse(parts[0], out var one)) return (one, one);
        if (parts.Length == 2 && int.TryParse(parts[0], out var a) && int.TryParse(parts[1], out var b) && a <= b) return (a, b);
        return null;
    }

    /// <summary>"ch01-03" for a range, "ch04" for one chapter (gold file names).</summary>
    public static string ChapterSlug(int first, int last) => first == last ? $"ch{first:00}" : $"ch{first:00}-{last:00}";

    public static Stage0Result Stage0For(string bookId) =>
        Stage0.Prepare(BookProfiles.For(bookId), SourceTexts.Read(bookId), SourceTexts.Sha256(bookId));

    /// <summary>
    /// Stage 0 as the run was chunked. A run records its chunking in run.json (<c>settings.chunkTokens</c>,
    /// <c>overlapTokens</c>, <c>chapterAligned</c>); one made before the one-pass pipeline has no <c>chapterAligned</c> and was cut in
    /// 1,000-token windows. A run folder without the setting yet is chunked as the configuration says.
    /// </summary>
    public static Stage0Result Stage0For(RunFolder run, string bookId) =>
        Stage0.Prepare(BookProfiles.For(bookId), SourceTexts.Read(bookId), SourceTexts.Sha256(bookId), ChunkingOf(run));

    public static ChunkSettings ChunkingOf(RunFolder run)
    {
        try
        {
            var settings = File.Exists(run.File(RunFolder.RunJson)) ? JsonNode.Parse(File.ReadAllText(run.File(RunFolder.RunJson)))?["settings"] : null;
            if (settings?["chunkTokens"]?.GetValue<int>() is { } tokens)
                return settings["chapterAligned"]?.GetValue<bool>() == true ? new ChunkSettings(tokens, 0, 4, true)
                    : new ChunkSettings(tokens, settings["overlapTokens"]?.GetValue<int>() ?? ChunkSettings.Windows.OverlapTokens, 4, false);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException) { }
        return ChunkSettings.From(LlmSettings.LoadConfiguration());
    }

    public static string BookText(string bookId) => System.Text.Encoding.UTF8.GetString(SourceTexts.Read(bookId));

    public static Func<int, int> ChapterOf(Stage0Result stage0) => offset =>
        stage0.Chapters.FirstOrDefault(c => offset >= c.CharStart && offset < c.CharEnd)?.Number ?? 0;

    /// <summary>Writes <paramref name="value"/> plus extra top-level fields.</summary>
    public static void WriteWith(RunFolder run, string file, object value, IReadOnlyDictionary<string, object?> extra)
    {
        var node = (JsonObject)JsonSerializer.SerializeToNode(value, RunFolder.Json)!;
        var ordered = new JsonObject();
        foreach (var (k, v) in extra) ordered[k] = JsonSerializer.SerializeToNode(v, RunFolder.Json);
        foreach (var (k, v) in node.ToList()) { node.Remove(k); ordered[k] = v; }
        File.WriteAllText(run.File(file), ordered.ToJsonString(RunFolder.Json) + "\n");
    }

    public static T Read<T>(RunFolder run, string file)
    {
        // A command that reads the earlier shape (facts as edges) must not misread a run of the full schema.
        if ((typeof(T) == typeof(Stage3Result) || typeof(T) == typeof(Stage4Result)) && Pipeline.IsGraphShape(run, file))
            throw new InvalidDataException($"{run.RunId}'s {file} is of the full schema; this command reads runs made before it. Use index score --graph, index build-db, gold perceptions-review.");
        return JsonSerializer.Deserialize<T>(File.ReadAllText(run.File(file)), RunFolder.Json)
            ?? throw new InvalidDataException($"{file} is empty");
    }

    public static int Fail(string message, int code = 2)
    {
        return CliContext.Fail(code, message);
    }

    /// <summary>Retries on 429/5xx for the indexing stages.</summary>
    public const int StageRetries = 6;

    /// <summary>The LLM client for a stage: sampling + call log + retries + OpenTelemetry, SDK retries off.</summary>
    /// <param name="profile">The <c>--llm</c> profile name; null: the <c>Llm</c> section.</param>
    /// Without <paramref name="profile"/> the stages use <c>Indexing:Llm</c> (the template's haiku55), not the <c>Llm</c> section
    /// that chat and llm-check use.
    public static (IChatClient Client, LlmCallContext Context, LlmSettings Llm, LlmSampling Sampling)? Llm(IConfiguration config, RunFolder run, string stage, string? profile = null)
    {
        var llm = LlmSettings.From(config, profile ?? IndexingSettings.From(config).DefaultLlmProfile);
        var problems = llm.Validate();
        if (problems.Count > 0)
        {
            LlmCheckCommand.NotConfigured(problems);
            return null;
        }
        var capture = string.Equals(Environment.GetEnvironmentVariable(LlmCheckCommand.CaptureContentVariable), "true", StringComparison.OrdinalIgnoreCase);
        var context = new LlmCallContext { Run = run, Stage = stage };
        StageLogs.Begin(run, stage);   // Run.log, until RecordCost ends the stage
        var sampling = LlmSampling.From(config, llm);
        // 6 retries (backoff 1 s … 32 s, or the server's retry-after): Stage 1 voting sends k requests per chunk at once.
        // A stop (Ctrl+C, `index cancel`) lets the calls already sent finish and sends no more.
        // The prompt cache (Indexing:Cache, default on): the stage's system text is read from the cache after its first call.
        return (ChatClientFactory.Create(llm, maxRetries: 0, new LlmPipelineOptions(capture, context, Retries: StageRetries, Sampling: sampling, FinishInFlight: true,
            PromptCache: config.GetValue("Indexing:Cache", true))), context, llm, sampling);
    }

    /// <summary>For run.json settings.llm: the profile (null = the Llm section), type, model and the content filter policy (no endpoint, never the key).</summary>
    public static object LlmRecord(LlmSettings llm) => new { profile = llm.Profile, type = llm.Type, model = llm.Model, policyId = llm.PolicyId };

    /// <summary>The profile (llm.profile; "Llm" for the default section) and the content filter policy (Llm:PolicyId) on a span.</summary>
    public static void TagLlm(Activity? span, LlmSettings llm)
    {
        span?.SetTag(IndexingTelemetry.LlmProfile, llm.Profile ?? LlmSettings.Section);
        if (llm.PolicyId is { } p) span?.SetTag(IndexingTelemetry.LlmPolicyId, p);
    }

    /// <summary>The effective sampling settings on a stage span.</summary>
    public static void TagSampling(Activity? span, LlmSampling sampling)
    {
        if (span is null) return;
        if (sampling.Temperature is { } t) span.SetTag(IndexingTelemetry.LlmTemperature, t);
        if (sampling.Seed is { } s && sampling.SeedSent) span.SetTag(IndexingTelemetry.LlmSeed, s);
    }

    public static void RecordCost(RunFolder run, IConfiguration config, string stage, IChatClient client)
    {
        var log = client.GetService<CallLogChatClient>()!;
        var pricing = Pricing.From(config);
        run.WriteStageCostWithCache(stage, pricing.CostReport(log.Calls, log.InputTokens, log.OutputTokens, log.CacheReadTokens, log.CacheWriteTokens), pricing.CostReport);
        StageLogs.End(run, stage, log.Calls, log.InputTokens + log.CacheReadTokens + log.CacheWriteTokens, log.OutputTokens);
    }

    public static object Git(string root) => GitInfo.Current(root) is var g ? new { commit = g.Sha, dirty = g.Dirty } : null!;
}

/// <summary>stage-1.json: the mentions Stage 2 reads. Written by <c>index extract</c> (<see cref="Pipeline.ExtractAsync"/>); the
/// <c>index mentions</c> command and its skill are gone.</summary>
public static class Stage1File
{
    public static object Counts(Stage1Result r) => new Dictionary<string, object>
    {
        ["chunks"] = r.Chunks.Count,
        ["returned"] = r.Chunks.Sum(c => c.Returned),
        ["gleaned"] = r.Chunks.Sum(c => c.Gleaned),
        ["accepted"] = r.Chunks.Sum(c => c.Accepted),
        ["rejected"] = r.Rejected.Count,
        ["duplicatesDropped"] = r.DuplicatesDropped,
        ["mentions"] = r.Mentions.Count,
        ["mentionsFromGlean"] = r.Mentions.Count(m => m.Pass == "glean"),
        // Share of returned mentions (all samples, both passes) that passed the evidence check.
        ["evidenceMatchRate"] = r.Chunks.Sum(c => c.Returned + c.Gleaned) is var returned and > 0 ? Math.Round((returned - r.Rejected.Count) / (double)returned, 4) : 0,
        ["samples"] = r.Samples,
        ["minVotes"] = r.MinVotes,
        ["pool"] = r.Pool?.Count ?? r.Mentions.Count,
    };

    /// <summary>The stage-1.json content (and its extra top-level fields) for a Stage 1 result.</summary>
    public static void WriteStage1(RunFolder run, Stage1Result result, object skillRecord, IReadOnlyDictionary<string, object?>? more = null)
    {
        var extra = new Dictionary<string, object?>
        {
            ["stage"] = Stage1.StageName,
            ["skill"] = skillRecord,
            ["scoreDistribution"] = Stage1.ScoreDistribution(result),
            ["counts"] = Counts(result),
        };
        foreach (var (k, v) in more ?? new Dictionary<string, object?>()) extra[k] = v;
        IndexingShared.WriteWith(run, RunFolder.Stage1Json, result, extra);
    }
}

/// <summary>
/// <c>index resolve --run &lt;runId&gt;</c>: Stage 2 on that run's stage-1.json.
/// <c>index resolve --stage1-from &lt;runId&gt; [--min-votes m]</c>: frozen input. A new run gets a copy of that run's
/// Stage 1 output (optionally re-filtered to m votes from its vote pool, no new calls), and only Stage 2 runs, so the
/// resolve skill can be tested without Stage 1 noise.
/// </summary>
public sealed class IndexResolveCommand : AsyncCommand<IndexResolveCommand.Settings>
{
    public sealed class Settings : GlobalSettings
    {
        [CommandOption("--run <RUN>")] public string Run { get; init; } = "";
        [CommandOption("--stage1-from <RUN>")][Description("Copy this run's Stage 1 output into a new run and run only Stage 2 on it.")] public string? Stage1From { get; init; }
        [CommandOption("--min-votes <M>")][Description("With --stage1-from: keep mentions with at least M votes (from the vote pool).")] public int? MinVotes { get; init; }
        [CommandOption("--booknlp-from <RUN>")][Description("Copy this run's BookNLP output (stage2/booknlp) instead of running BookNLP again.")] public string? BookNlpFrom { get; init; }
        [CommandOption("--llm <NAME>")][Description("A named LLM profile (Llm:Profiles:<name>); default: the Llm section.")] public string? Llm { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        var root = RepoPaths.FindRoot();
        RunFolder run;
        if (settings.Stage1From is { } from)
        {
            if (settings.Run.Length > 0) return IndexingShared.Fail("Give --run or --stage1-from, not both.");
            RunFolder source;
            try { source = RunFolder.Open(RepoPaths.Runs(root), from); }
            catch (DirectoryNotFoundException ex) { return IndexingShared.Fail(ex.Message); }
            try { run = FrozenStage1.Copy(source, RepoPaths.Runs(root), settings.MinVotes); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentOutOfRangeException or FileNotFoundException) { return IndexingShared.Fail(ex.Message); }
            AnsiConsole.MarkupLine($"Frozen Stage 1 from [bold]{source.RunId}[/]{(settings.MinVotes is { } m ? $" (min votes {m})" : "")} → new run [bold]{run.RunId}[/]");
        }
        else
        {
            if (settings.MinVotes is not null) return IndexingShared.Fail("--min-votes is only used with --stage1-from.");
            try { run = RunFolder.Open(RepoPaths.Runs(root), settings.Run); }
            catch (DirectoryNotFoundException ex) { return IndexingShared.Fail(ex.Message); }
        }
        // Stage 2 on a BookNLP backbone (CorefStage2), in Pipeline; the sidecar's output stays in the run folder.
        return await Pipeline.ResolveAsync(root, run, settings.Llm, settings.BookNlpFrom, ct);
    }

    private static object Decision(PairDecision d) => new
    {
        a = d.NameA,
        b = d.NameB,
        candidate = new { method = d.Pair.Method, score = d.Pair.Score },
        same = d.Decision.SameEntity,
        canonicalName = d.Decision.CanonicalName,
        reason = d.Decision.Reason,
        evidenceA = d.EvidenceA,
        evidenceB = d.EvidenceB,
    };

    public static object Counts(Stage2Result r) => new Dictionary<string, object>
    {
        ["groups"] = r.Groups.Count,
        ["candidates"] = r.Candidates.Count,
        ["merges"] = r.Decisions.Count(d => d.Decision.SameEntity),
        ["notMerged"] = r.Decisions.Count(d => !d.Decision.SameEntity),
        ["conflicts"] = r.Conflicts.Count,
        ["entities"] = r.Entities.Count,
        ["characters"] = r.Entities.Count(e => e.Type == MentionType.Character),
        ["locations"] = r.Entities.Count(e => e.Type == MentionType.Location),
    };
}

/// <summary>Reads stage-2.json's entities back (for cross-check and review).</summary>
public static class Stage2File
{
    /// <param name="Pass">"booknlp" for a noun-phrase mention taken from a BookNLP chain; null for a Stage 1 mention.</param>
    public sealed record FileMention(int CharStart, int CharEnd, int Chapter, string Text, string? Pass = null);
    public sealed record FileEntity(int Id, string CanonicalName, MentionType Type, List<string> Aliases, List<FileMention> Mentions, int[] Groups, string? Kind = null);
    public sealed record FileDecision(string A, string B, bool Same, string? CanonicalName, string Reason);
    public sealed record File2(string Book, List<FileEntity> Entities, List<FileDecision> Merges, List<FileDecision> NotMerged, List<string> Conflicts,
        List<IdentityReveal>? IdentityReveals = null);

    public static Stage2Result Load(RunFolder run)
    {
        var f = IndexingShared.Read<File2>(run, RunFolder.Stage2Json);
        var entities = f.Entities.Select(e => new Entity(e.Id, e.CanonicalName, e.Type, e.Aliases,
            e.Mentions.Select(m => new Mention(0, m.Text, e.Type, 0, m.Chapter, m.Pass ?? "", m.CharStart, m.CharEnd, "", new EvidenceMatch("", 0, 0, 0))).ToList(),
            e.Mentions.Min(m => m.CharStart), e.Groups, e.Kind)).ToList();
        var decisions = f.Merges.Concat(f.NotMerged).Select(d => new PairDecision(new CandidatePair(0, 0, "", 0), d.A, d.B,
            new MergeDecision(d.Same, d.CanonicalName, d.Reason), [], [])).ToList();
        return new Stage2Result(f.Book, [], [], decisions, entities, f.Conflicts, new ResolveSettings(), f.IdentityReveals);
    }
}

/// <summary><c>index crosscheck --run &lt;runId&gt;</c>: compares stage-2.json with LitBank (and BookNLP, when present).</summary>
public sealed class IndexCrosscheckCommand : Command<IndexCrosscheckCommand.Settings>
{
    public sealed class Settings : GlobalSettings
    {
        [CommandOption("--run <RUN>")] public string Run { get; init; } = "";
    }

    public override int Execute(CommandContext context, Settings settings, CancellationToken ct)
    {
        var root = RepoPaths.FindRoot();
        RunFolder run;
        try { run = RunFolder.Open(RepoPaths.Runs(root), settings.Run); }
        catch (DirectoryNotFoundException ex) { return IndexingShared.Fail(ex.Message); }
        if (!File.Exists(run.File(RunFolder.Stage2Json))) return IndexingShared.Fail($"{run.RunId} has no stage-2.json; run index resolve first.");

        var stage2 = Stage2File.Load(run);
        var results = Compare(root, stage2, RunChapters(run));
        // Stage 2's chains against LitBank's coreference for the slice LitBank annotates (the start of chapter 1). Read-only.
        CorefAgreement? coref = null;
        if (stage2.BookId == "hound-of-the-baskervilles" && LitBankCoref.Exists(root) && RunChapters(run).First == 1)
        {
            var stage0 = IndexingShared.Stage0For(stage2.BookId);
            var litBank = LitBankCoref.Load(root, IndexingShared.BookText(stage2.BookId), stage0.Chapters[0].CharStart);
            coref = LitBankCoref.Compare(litBank, LitBankCoref.OurChains(run, stage2), stage2.Entities.ToDictionary(e => e.Id, e => e.CanonicalName));
        }
        IndexingShared.WriteWith(run, RunFolder.CrosscheckJson, new { comparisons = results, litBankCoref = coref }, new Dictionary<string, object?> { ["book"] = stage2.BookId, ["note"] = "Review prompts, not truth." });
        run.RecordCommand(new { command = $"index crosscheck --run {run.RunId}", startedAt = DateTimeOffset.UtcNow }, new Dictionary<string, object?>());

        var table = new Table().AddColumns("Source", "Slice", "Their names", "Our entities in slice", "Not in ours", "Not in theirs", "Alias differences");
        foreach (var r in results)
            table.AddRow(r.Source, Markup.Escape(r.Available ? $"[{r.SliceStart},{r.SliceEnd})" : "not available"), r.TheirNames.ToString(), r.OurEntitiesInSlice.ToString(),
                r.Disagreements.Count(d => d.Kind == "not in ours").ToString(), r.Disagreements.Count(d => d.Kind == "not in theirs").ToString(),
                r.Disagreements.Count(d => d.Kind == "alias difference").ToString());
        AnsiConsole.Write(table);
        if (coref is not null)
        {
            var c = new Table().AddColumns("LitBank coreference", "Person mentions", "On our chains", "Pairs", "Agreement", "Link precision", "Link recall", "Link F1");
            c.AddRow(Markup.Escape($"[{coref.SliceStart},{coref.SliceEnd})"), coref.PersonMentions.ToString(), coref.OnOurChains.ToString(), coref.Pairs.ToString(),
                $"{coref.Agreement:0.000}", $"{coref.LinkPrecision:0.000}", $"{coref.LinkRecall:0.000}", $"{coref.LinkF1:0.000}");
            AnsiConsole.Write(c);
        }
        return 0;
    }

    /// <summary>The run's chapter range from run.json (index mentions records it).</summary>
    public static (int First, int Last) RunChapters(RunFolder run)
    {
        var runJson = JsonNode.Parse(File.ReadAllText(run.File(RunFolder.RunJson)))!;
        return (runJson["chapters"]!["first"]!.GetValue<int>(), runJson["chapters"]!["last"]!.GetValue<int>());
    }

    public static List<ComparisonResult> Compare(string root, Stage2Result stage2, (int First, int Last) chapters)
    {
        var stage0 = IndexingShared.Stage0For(stage2.BookId);
        var text = IndexingShared.BookText(stage2.BookId);
        var results = new List<ComparisonResult>();
        // Every source is compared over the run's chapters only.
        var first = stage0.Chapters.Single(c => c.Number == chapters.First);
        var last = stage0.Chapters.Single(c => c.Number == chapters.Last);
        var (lb, alignment) = LitBank.Load(root, text, stage0.Chapters[0].CharStart);
        var litBank = Comparison.CompareLitBank(stage2.Entities, lb, alignment, IndexingShared.ChapterOf(stage0), first.CharStart, last.CharEnd);
        results.Add(!litBank.Available ? litBank : litBank with
        {
            Note = $"LitBank tokens aligned {alignment.Aligned}/{alignment.Tokens}" + (alignment.Unaligned.Count > 0 ? $"; unaligned: {string.Join("; ", alignment.Unaligned.Take(5))}" : ""),
        });
        var bookNlp = BookNlp.Load(root, stage2.BookId);
        if (bookNlp is null)
            results.Add(Comparison.BookNlpUnavailable(Path.GetRelativePath(root, BookNlp.Folder(root, stage2.BookId)).Replace('\\', '/')));
        else
        {
            // BookNLP covers the whole body.
            results.Add(Comparison.CompareBookNlp(stage2.Entities, bookNlp, first.CharStart, last.CharEnd, IndexingShared.ChapterOf(stage0)) with
            {
                Note = $"BookNLP mentions (PER/LOC/GPE/FAC, PROP+NOM) in chapters {chapters.First}–{chapters.Last}",
            });
        }
        return results;
    }
}

/// <summary>
/// <c>gold review --run &lt;runId&gt; [--run …]</c>: writes gold/&lt;book&gt;/&lt;chapters&gt;.review.md for review. With several
/// runs (same book and chapters) the draft is their union, each line saying how many runs found it.
/// </summary>
public sealed class GoldReviewCommand : Command<GoldReviewCommand.Settings>
{
    public sealed class Settings : GlobalSettings
    {
        [CommandOption("--run <RUN>")][Description("A run id; repeat to draft from the union of several runs.")] public string[] Run { get; init; } = [];
        [CommandOption("--out <FILE>")][Description("Default: gold/<book>/chNN-NN.review.md (chNN for one chapter)")] public string? Out { get; init; }
        [CommandOption("--force")][Description("Overwrite a review file that already has review marks.")] public bool Force { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings, CancellationToken ct)
    {
        if (settings.Run.Length == 0) return IndexingShared.Fail("Give at least one --run.");
        var root = RepoPaths.FindRoot();
        var runs = new List<(RunFolder Run, Stage2Result Stage2, (int First, int Last) Chapters)>();
        foreach (var id in settings.Run)
        {
            RunFolder run;
            try { run = RunFolder.Open(RepoPaths.Runs(root), id); }
            catch (DirectoryNotFoundException ex) { return IndexingShared.Fail(ex.Message); }
            if (!File.Exists(run.File(RunFolder.Stage2Json))) return IndexingShared.Fail($"{run.RunId} has no stage-2.json.");
            runs.Add((run, Stage2File.Load(run), IndexCrosscheckCommand.RunChapters(run)));
        }
        if (runs.Select(r => (r.Stage2.BookId, r.Chapters)).Distinct().Count() > 1)
            return IndexingShared.Fail("All runs must be of the same book and chapters: " + string.Join(", ", runs.Select(r => $"{r.Run.RunId} = {r.Stage2.BookId} ch{r.Chapters.First}-{r.Chapters.Last}")));
        if (runs.Select(r => r.Run.RunId).Distinct().Count() < runs.Count) return IndexingShared.Fail("A run is given twice.");

        var (bookId, (first, last)) = (runs[0].Stage2.BookId, runs[0].Chapters);
        var manifest = BookManifest.Read(Path.Combine(RepoPaths.Books(root), BookManifest.FileName));
        var title = manifest.First(b => b.Id == bookId).Title;
        var stage0 = IndexingShared.Stage0For(bookId);
        var sources = runs.Select(r => new ReviewSource(r.Run.RunId, r.Stage2, IndexCrosscheckCommand.Compare(root, r.Stage2, r.Chapters))).ToList();

        var content = ReviewFile.Generate(title, bookId, first == last ? $"{first}" : $"{first}–{last}", sources,
            IndexingShared.BookText(bookId), IndexingShared.ChapterOf(stage0));
        var path = settings.Out ?? Path.Combine(root, "gold", bookId, IndexingShared.ChapterSlug(first, last) + ".review.md");
        // Never silently replace a reviewed file: gold comes only from it.
        if (File.Exists(path) && !settings.Force && System.Text.RegularExpressions.Regex.IsMatch(File.ReadAllText(path), @"^- \[(x|~|-)\] ", System.Text.RegularExpressions.RegexOptions.Multiline))
            return IndexingShared.Fail($"{Path.GetRelativePath(root, path)} already has review marks; not overwriting it. Use --out <other file>, or --force.");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, content);
        var parsed = ReviewFile.Parse(content.Replace("- [ ] ", "- [x] "));
        AnsiConsole.MarkupLine($"Wrote [bold]{Markup.Escape(Path.GetRelativePath(root, path))}[/] from {sources.Count} run(s): {parsed.Entities.Count} entities, " +
            $"{parsed.Merges.Count(m => m.Text.StartsWith("merge"))} merges, {parsed.Merges.Count(m => m.Text.StartsWith("conflict"))} conflicts, {parsed.CrossChecks.Count} cross-check disagreements.");
        return 0;
    }
}

/// <summary>
/// <c>gold import --book &lt;id&gt; --file &lt;reviewed.md&gt;</c>: parses the reviewed file into gold JSON. Any line it can't
/// parse (or that isn't marked) is reported and nothing is written. Gold JSON only ever comes from a reviewed file.
/// </summary>
public sealed class GoldImportCommand : Command<GoldImportCommand.Settings>
{
    public sealed class Settings : GlobalSettings
    {
        [CommandOption("--book <ID>")] public string Book { get; init; } = "";
        [CommandOption("--file <FILE>")] public string File { get; init; } = "";
        [CommandOption("--out <FILE>")][Description("Default: next to the review file, '.review.md' → '.json'")] public string? Out { get; init; }
        [CommandOption("--supplement <FILE>")][Description("Facts only: a second reviewed facts file (e.g. ch01-03.facts.supplement.review.md) merged into the same gold JSON")] public string? Supplement { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings, CancellationToken ct)
    {
        if (!System.IO.File.Exists(settings.File)) return IndexingShared.Fail($"File not found: {settings.File}");
        if (!settings.File.EndsWith(".review.md") && settings.Out is null) return IndexingShared.Fail("The file must end in .review.md, or give --out.");
        var outPath = settings.Out ?? settings.File[..^".review.md".Length] + ".json";

        // A facts review (gold/<book>/<chapters>.facts.review.md) → facts gold (<chapters>.facts.json).
        if (settings.File.EndsWith(".facts.review.md"))
        {
            if (!BookProfiles.Has(RepoPaths.FindRoot(), settings.Book)) return IndexingShared.Fail($"Unknown book '{settings.Book}'.");
            if (settings.Supplement is { } sup && !System.IO.File.Exists(sup)) return IndexingShared.Fail($"File not found: {sup}");
            var facts = FactsGold.Import(settings.Book, settings.File, outPath, settings.Supplement);
            if (facts.Errors.Count > 0)
            {
                AnsiConsole.MarkupLine($"[red]{facts.Errors.Count} line(s) can't be imported; nothing was written:[/]");
                foreach (var e in facts.Errors) AnsiConsole.MarkupLine($"  line {e.Line}: {Markup.Escape(e.Problem)}  [grey]{Markup.Escape(e.Text)}[/]");
                return 1;
            }
            AnsiConsole.MarkupLine($"Wrote [bold]{Markup.Escape(outPath)}[/]: {facts.Parsed!.Items.Count} items ({facts.Parsed.Rejected.Count} rejected), {facts.Parsed.Orders.Count} orderings"
                + (settings.Supplement is { } s ? $" (with {Markup.Escape(Path.GetFileName(s))})." : "."));
            return 0;
        }

        if (settings.Supplement is not null) return IndexingShared.Fail("--supplement is only for facts reviews (*.facts.review.md).");
        var outcome = Import(settings.Book, settings.File, outPath);
        if (outcome.Errors.Count > 0)
        {
            AnsiConsole.MarkupLine($"[red]{outcome.Errors.Count} line(s) can't be imported; nothing was written:[/]");
            foreach (var e in outcome.Errors) AnsiConsole.MarkupLine($"  line {e.Line}: {Markup.Escape(e.Problem)}  [grey]{Markup.Escape(e.Text)}[/]");
            return 1;
        }
        AnsiConsole.MarkupLine($"Wrote [bold]{Markup.Escape(outPath)}[/]: {outcome.Parsed!.Entities.Count} entities ({outcome.Parsed.Rejected.Count} rejected), {outcome.Parsed.Merges.Count} merge marks, {outcome.Parsed.CrossChecks.Count} cross-check marks.");
        return 0;
    }

    public sealed record ImportOutcome(ParsedReview? Parsed, IReadOnlyList<ParseError> Errors);

    public static ImportOutcome Import(string bookId, string reviewPath, string outPath)
    {
        var bytes = System.IO.File.ReadAllBytes(reviewPath);
        var parsed = ReviewFile.Parse(System.Text.Encoding.UTF8.GetString(bytes));
        if (parsed.Errors.Count > 0) return new ImportOutcome(parsed, parsed.Errors);
        var dupes = parsed.Entities.GroupBy(e => (e.Type, e.Name)).Where(g => g.Count() > 1).ToList();
        if (dupes.Count > 0)
            return new ImportOutcome(parsed, dupes.Select(g => new ParseError(g.Last().Line, $"{g.Key.Type} · {g.Key.Name}", "duplicate entity (same type and name)")).ToList());

        var gold = new
        {
            book = bookId,
            source = new { file = Path.GetFileName(reviewPath), sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)) },
            importedAt = DateTimeOffset.UtcNow,
            entities = parsed.Entities.Select(e => new { type = e.Type, name = e.Name, aliases = e.Aliases, first = new { chapter = e.FirstChapter, quote = e.FirstQuote }, status = e.Status }),
            rejected = parsed.Rejected.Select(e => new { type = e.Type, name = e.Name }),
            mergeReview = parsed.Merges.Select(m => new { mark = m.Mark.ToString().ToLowerInvariant(), line = m.Text }),
            crosscheckReview = parsed.CrossChecks.Select(m => new { mark = m.Mark.ToString().ToLowerInvariant(), line = m.Text }),
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        System.IO.File.WriteAllText(outPath, JsonSerializer.Serialize(gold, RunFolder.Json) + "\n");
        return new ImportOutcome(parsed, []);
    }
}

/// <summary>Frozen Stage 1 input for <c>index resolve --stage1-from</c>.</summary>
public static class FrozenStage1
{
    /// <summary>
    /// Creates a new run in <paramref name="runsRoot"/> whose stage-1.json is <paramref name="source"/>'s (re-filtered to
    /// <paramref name="minVotes"/> when given), with the source's Stage 1 failure lines and its run.json plan fields
    /// (book, chapters, text hash, settings, extract skill, Stage 1 sampling) plus <c>stage1From</c>. No LLM calls.
    /// </summary>
    public static RunFolder Copy(RunFolder source, string runsRoot, int? minVotes = null)
    {
        if (!File.Exists(source.File(RunFolder.Stage1Json))) throw new FileNotFoundException($"{source.RunId} has no stage-1.json.");
        var stage1 = IndexingShared.Read<Stage1Result>(source, RunFolder.Stage1Json);
        if (minVotes is { } m) stage1 = Stage1.WithMinVotes(stage1, m);
        var sourceStage1 = JsonNode.Parse(File.ReadAllText(source.File(RunFolder.Stage1Json)))!;
        var sourceRun = JsonNode.Parse(File.ReadAllText(source.File(RunFolder.RunJson)))!.AsObject();

        var run = RunFolder.Create(runsRoot);
        Stage1File.WriteStage1(run, stage1, sourceStage1["skill"]!.DeepClone(), new Dictionary<string, object?> { ["stage1From"] = source.RunId });
        if (File.Exists(source.File(RunFolder.FailuresJsonl)))
            foreach (var line in File.ReadAllLines(source.File(RunFolder.FailuresJsonl)).Where(l => l.Length > 0).Select(l => JsonNode.Parse(l)!))
                if (line["stage"]?.GetValue<string>() == Stage1.StageName) run.AppendFailure(line);

        var fields = new Dictionary<string, object?>();
        foreach (var key in new[] { "book", "chapters", "textSha256", "git", "model" })
            if (sourceRun[key] is { } v) fields[key] = v.DeepClone();
        var settings = sourceRun["settings"]?.DeepClone().AsObject() ?? [];
        if (minVotes is { } mv) settings["minVotes"] = mv;
        fields["settings"] = settings;
        if (sourceRun["skills"] is JsonObject skills)
            fields["skills"] = new JsonObject(skills.Where(s => s.Key is SkillFile.ExtractMentions or SkillFile.ExtractGraph).Select(s => KeyValuePair.Create(s.Key, s.Value?.DeepClone())));
        // The items Stage 1 extracted go with its mentions.
        if (File.Exists(source.File(ExtractStage.FileName))) File.Copy(source.File(ExtractStage.FileName), run.File(ExtractStage.FileName), overwrite: true);
        if (sourceRun["sampling"]?[Stage1.StageName] is { } sampling)
            fields["sampling"] = new JsonObject { [Stage1.StageName] = sampling.DeepClone() };
        fields["stage1From"] = new JsonObject { ["runId"] = source.RunId, ["minVotes"] = minVotes };
        run.RecordCommand(new { command = $"index resolve --stage1-from {source.RunId}{(minVotes is { } x ? $" --min-votes {x}" : "")}", startedAt = DateTimeOffset.UtcNow }, fields);
        return run;
    }
}
