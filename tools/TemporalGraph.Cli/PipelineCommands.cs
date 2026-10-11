using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using Spectre.Console.Cli;
using TemporalGraph.Cli.Indexing;
using TemporalGraph.Cli.Runs;
using TemporalGraph.Cli.Telemetry;
using TemporalGraph.Data;
using TemporalGraph.Llm;
using TemporalGraph.Query;

namespace TemporalGraph.Cli;

/// <summary>
/// Stages 2–5 on the full bi-temporal schema, each runnable on its own from the files the stage before it left in the
/// run folder (stage-1.json → stage-2.json → stage-3.json → stage-4.json → stage-5.json → book.sqlite). The commands
/// (<c>index resolve</c>, <c>facts</c>, <c>time</c>, <c>perceive</c>, <c>build-db</c>) and <c>index --stages a..b</c> all end here.
/// </summary>
public static partial class Pipeline
{
    public const string Stage5Json = "stage-5.json";
    public const string LeakTestsJson = "leak-tests.json";

    /// <summary>True when the run's stage file is of the full-schema shape (<c>"shape": 2</c>).</summary>
    public static bool IsGraphShape(RunFolder run, string file) =>
        File.Exists(run.File(file)) && JsonNode.Parse(File.ReadAllText(run.File(file)))?["shape"]?.GetValue<int>() == GraphStage3.Shape;

    private static Activity? StageSpan(RunFolder run, string stage, string bookId, SkillFile? skill, (IChatClient Client, LlmCallContext Context, LlmSettings Llm, LlmSampling Sampling)? llm)
    {
        var span = IndexingTelemetry.Source.StartActivity($"{stage} {bookId}");
        span?.SetTag(IndexingTelemetry.RunId, run.RunId);
        span?.SetTag(IndexingTelemetry.Stage, stage);
        span?.SetTag(IndexingTelemetry.BookId, bookId);
        if (skill is not null)
        {
            span?.SetTag(IndexingTelemetry.SkillName, skill.Name);
            span?.SetTag(IndexingTelemetry.SkillVersion, skill.Version);
        }
        if (llm is { } l)
        {
            IndexingShared.TagSampling(span, l.Sampling);
            IndexingShared.TagLlm(span, l.Llm);
        }
        return span;
    }

    private static void Record(RunFolder run, string command, DateTimeOffset started, string stage, IEnumerable<SkillFile> skills, LlmSampling? sampling, object settings)
    {
        var fields = new Dictionary<string, object?>
        {
            ["skills"] = skills.ToDictionary(s => s.Name, s => s.Record()),
            ["settings"] = settings,
        };
        if (sampling is not null) fields["sampling"] = new Dictionary<string, object> { [stage] = sampling.Record() };
        run.RecordCommand(new { command, startedAt = started, finishedAt = DateTimeOffset.UtcNow }, fields);
    }

    private static string Tokens(IChatClient client) => client.GetService<CallLogChatClient>() is { } log ? $"{log.Calls} calls, {log.InputTokens}/{log.OutputTokens} tokens in/out" : "";

    // ---- Stage 2 ----------------------------------------------------------------------------------------------------

    /// <summary>Stage 2 on the run's stage-1.json: the BookNLP sidecar (once; its output stays in the run folder), then <see cref="CorefStage2"/>.</summary>
    /// <param name="bookNlpFrom">A run whose sidecar output is copied instead of running BookNLP again (the same book text).</param>
    public static async Task<int> ResolveAsync(string root, RunFolder run, string? llmProfile, string? bookNlpFrom, CancellationToken ct, StageOverrides? overrides = null)
    {
        if (!File.Exists(run.File(RunFolder.Stage1Json))) return IndexingShared.Fail($"{run.RunId} has no stage-1.json; run index extract first.");
        if ((overrides ?? StageOverrides.None).Validate() is [var bad, ..]) return IndexingShared.Fail(bad);
        var config = (overrides ?? StageOverrides.None).Over(LlmSettings.LoadConfiguration());
        var indexing = IndexingShared.IndexingSettings.From(config);
        var skill = SkillFile.Load(root, SkillFile.ResolveBlocks);
        var stage1 = IndexingShared.Read<Stage1Result>(run, RunFolder.Stage1Json);
        var stage0 = IndexingShared.Stage0For(run, stage1.BookId);
        var bookText = IndexingShared.BookText(stage1.BookId);
        // The model is asked a block at a time, k = 3 samples and m = 2 votes per pair (Indexing:StageB).
        var parallel = Parallel(config);
        var maxGroups = config.GetValue("Indexing:StageB:MaxGroupsPerCall", BlockResolve.DefaultMaxGroups);
        var coref = new CorefSettings(indexing.Resolve.JaroWinklerThreshold, indexing.Resolve.EvidencePerSide,
            config.GetValue("Indexing:StageB:Samples", BlockResolve.DefaultSamples), config.GetValue("Indexing:StageB:MinVotes", BlockResolve.DefaultMinVotes),
            config.GetValue("Indexing:Stage2:NounPhraseMinMentions", 3), config.GetValue("Indexing:Stage2:NounPhraseCandidates", 3), parallel);
        if (coref.MinVotes < 1 || coref.MinVotes > coref.Samples) return IndexingShared.Fail($"Indexing:StageB:MinVotes must be between 1 and Samples ({coref.Samples}).");

        // The sidecar: BookNLP once per book, persisted under the run folder.
        if (bookNlpFrom is not null && !BookNlpChains.Exists(run))
        {
            RunFolder source;
            try { source = RunFolder.Open(RepoPaths.Runs(root), bookNlpFrom); }
            catch (DirectoryNotFoundException ex) { return IndexingShared.Fail(ex.Message); }
            if (!BookNlpChains.Exists(source)) return IndexingShared.Fail($"{source.RunId} has no {BookNlpChains.Folder}/{BookNlpChains.MentionsFile}.");
            BookNlpChains.CopyFrom(source, run);
        }
        if (!BookNlpChains.Exists(run))
        {
            var bookNlp = BookNlpSettings.From(config);
            using var sidecar = IndexingTelemetry.Source.StartActivity($"booknlp {stage1.BookId}");
            sidecar?.SetTag(IndexingTelemetry.RunId, run.RunId);
            var watch = Stopwatch.StartNew();
            // The sidecar's minutes are Stage 2's, and none of them is a model call; run.log says where the time went.
            StageLogs.Line(run, "stage-2: BookNLP (the coreference sidecar; no model calls; several minutes the first time a text is read) started");
            var problem = AnsiConsole.Status().Start("Stage 2: BookNLP (the coreference sidecar; several minutes on a first run)…", _ => BookNlpChains.Run(root, run, stage0, bookNlp));
            StageLogs.Line(run, $"stage-2: BookNLP {(problem is null ? "done" : "failed")} · {watch.Elapsed.TotalSeconds:0.0} s");
            if (problem is not null) return IndexingShared.Fail("Stage 2: " + problem, 1);
            run.RecordCommand(new { command = "booknlp sidecar", startedAt = DateTimeOffset.UtcNow.AddSeconds(-watch.Elapsed.TotalSeconds), seconds = Math.Round(watch.Elapsed.TotalSeconds, 1) }, new Dictionary<string, object?>());
        }
        var chains = BookNlpChains.Load(run);

        if (IndexingShared.Llm(config, run, CorefStage2.StageName, llmProfile) is not { } llm) return Exit.NotReady;
        using var client = llm.Client;
        var command = $"index resolve --run {run.RunId}{(llmProfile is { } p ? $" --llm {p}" : "")}";
        var started = DateTimeOffset.UtcNow;
        using var span = StageSpan(run, CorefStage2.StageName, stage1.BookId, skill, llm);
        run.RemoveFailures(f => f["stage"]?.GetValue<string>() == CorefStage2.StageName);
        CorefResult result;
        BlockResolve.BlockStats blockStats;
        var runner = new StageRunner(parallel);
        try
        {
            using var active = runner.Begin();
            (result, blockStats) = await AnsiConsole.Status().StartAsync("Stage 2: resolving…", status =>
                BlockResolve.RunAsync(client, llm.Context, skill, stage0, bookText, stage1.Mentions, chains, coref, maxGroups, indexing.EvidenceThreshold, runner, run, ct,
                    (done, of) => status.Status($"Stage 2: block {done} of {of}…")));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            span?.SetStatus(ActivityStatusCode.Error, ex.GetType().Name);
            IndexingShared.RecordCost(run, config, CorefStage2.StageName, client);
            return IndexingShared.Fail($"Stage 2 failed: {ex.GetType().Name}: {ex.Message}", 1);
        }

        var chapterOf = IndexingShared.ChapterOf(stage0);
        var counts = CorefStage2.Counts(result);
        counts["blocks"] = blockStats;
        counts["pairsBlockingRemoved"] = Math.Max(0, blockStats.CandidatePairs - blockStats.PairsInBlocks);
        object Decision(ClusterDecision d) => new
        {
            a = d.NameA, b = d.NameB, clusters = new[] { d.Pair.A, d.Pair.B }, why = d.Pair.Why, relation = d.Relation, votes = d.VotesByRelation,
            same = d.Relation == ClusterRelations.Same, canonicalName = d.CanonicalName, reason = d.Reason, quote = d.Quote,
            quoteAt = d.QuoteMatch is null ? null : new[] { d.QuoteMatch.CharStart, d.QuoteMatch.CharEnd },
        };
        IndexingShared.WriteWith(run, RunFolder.Stage2Json, new
        {
            entities = result.Entities.Select(e => new
            {
                id = e.Id,
                canonicalName = e.CanonicalName,
                type = e.Type,
                aliases = e.Aliases,
                firstMention = new { charStart = e.FirstMention, chapter = chapterOf(e.FirstMention) },
                mentions = e.Mentions.Select(m => new { charStart = m.CharStart, charEnd = m.CharEnd, chapter = m.Chapter, text = m.Text, pass = m.Pass == "booknlp" ? "booknlp" : null }),
                groups = e.Groups,
            }),
            identityReveals = result.Identities,
            merges = result.Decisions.Where(d => d.Relation == ClusterRelations.Same).Select(Decision),
            notMerged = result.Decisions.Where(d => d.Relation != ClusterRelations.Same).Select(Decision),
            conflicts = result.Notes,
            clusters = result.Clusters.Select(c => new { id = c.Id, type = c.Type, source = c.Source, nounPhrase = c.Nominal, surfaces = c.Surfaces, mentions = c.Mentions.Count }),
        }, new Dictionary<string, object?>
        {
            ["stage"] = CorefStage2.StageName,
            ["shape"] = GraphStage3.Shape,
            ["book"] = stage1.BookId,
            ["skill"] = skill.Record(),
            ["settings"] = result.Settings,
            ["booknlp"] = File.Exists(Path.Combine(BookNlpChains.FolderOf(run), BookNlpChains.RunInfoFile)) ? JsonNode.Parse(File.ReadAllText(Path.Combine(BookNlpChains.FolderOf(run), BookNlpChains.RunInfoFile))) : null,
            ["counts"] = counts,
        });
        IndexingShared.RecordCost(run, config, CorefStage2.StageName, client);
        Record(run, command, started, CorefStage2.StageName, [skill], llm.Sampling, new { stage2 = result.Settings, llm = IndexingShared.LlmRecord(llm.Llm) });
        foreach (var (k, v) in counts) span?.SetTag("stage2." + k, v is int or double or string ? v : JsonSerializer.Serialize(v));
        CliTelemetry.Logger("TemporalGraph.Indexing").LogInformation("Run {RunId}: stage 2 {Counts}", run.RunId, JsonSerializer.Serialize(counts));

        var table = new Table().AddColumns("Chain mentions", "Clusters", "Noun-phrase chains (attached)", "Blocks (calls)", "Merges", "Identity reveals", "Characters", "Locations");
        table.AddRow(counts["chainMentions"].ToString()!, counts["clusters"].ToString()!, $"{counts["nounPhraseChains"]} ({counts["nounPhraseChainsAttached"]})", $"{blockStats.Blocks + blockStats.NounPhraseCalls} ({blockStats.Calls})",
            counts["merges"].ToString()!, counts["identityReveals"].ToString()!, counts["characters"].ToString()!, counts["locations"].ToString()!);
        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"Blocking: {blockStats.CandidatePairs} candidate pairs before, {blockStats.PairsInBlocks} decided inside {blockStats.Blocks} block(s); {counts["pairsBlockingRemoved"]} pairs never asked.");
        AnsiConsole.MarkupLine($"Run [bold]{run.RunId}[/]: stage 2, {Markup.Escape(Tokens(client))}");
        return 0;
    }

    // ---- Stage 4 ----------------------------------------------------------------------------------------------------

    public static async Task<int> TimeAsync(string root, RunFolder run, string? llmProfile, CancellationToken ct, StageOverrides? overrides = null)
    {
        if (!IsGraphShape(run, RunFolder.Stage3Json)) return IndexingShared.Fail($"{run.RunId} has no stage-3.json of the full-schema shape; run index link first.");
        var config = (overrides ?? StageOverrides.None).Over(LlmSettings.LoadConfiguration());
        var voting = new VotingSettings(1, 1);   // One call per chapter, one sample
        var skill = SkillFile.Load(root, SkillFile.AssignStoryTime);
        var anchorSkill = SkillFile.Load(root, SkillFile.PickAnchorDate);
        var stage3 = IndexingShared.Read<GraphStage3Result>(run, RunFolder.Stage3Json);
        if (IndexingShared.Llm(config, run, TimelineStage4.StageName, llmProfile) is not { } llm) return Exit.NotReady;
        using var client = llm.Client;

        var command = $"index time --run {run.RunId}{(llmProfile is { } p ? $" --llm {p}" : "")}";
        var started = DateTimeOffset.UtcNow;
        using var span = StageSpan(run, TimelineStage4.StageName, stage3.BookId, skill, llm);
        var stage0 = IndexingShared.Stage0For(run, stage3.BookId);
        var runner = new StageRunner(Parallel(config));
        using var active = runner.Begin();
        run.RemoveFailures(f => f["stage"]?.GetValue<string>() == TimelineStage4.StageName);
        TimelineStage4Result result;
        try
        {
            result = await AnsiConsole.Status().StartAsync("Stage 4: story time…", status =>
                TimelineStage4.RunByChapterAsync(client, llm.Context, anchorSkill, skill, stage0, stage3, runner, run, ct,
                    (done, of) => status.Status($"Stage 4: chapter {done} of {of}…")));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            span?.SetStatus(ActivityStatusCode.Error, ex.GetType().Name);
            IndexingShared.RecordCost(run, config, TimelineStage4.StageName, client);
            return IndexingShared.Fail($"Stage 4 failed: {ex.GetType().Name}: {ex.Message}\nRun {run.RunId}", 1);
        }

        var counts = TimelineStage4.Counts(result);
        IndexingShared.WriteWith(run, RunFolder.Stage4Json, result, new Dictionary<string, object?>
        {
            ["stage"] = TimelineStage4.StageName,
            ["skills"] = new[] { anchorSkill.Record(), skill.Record() },
            ["context"] = $"the anchor, the Events of the {TimelineStage4.ContextChapters} chapters before, and the chapter's Events and Facts; no text",
            ["parallel"] = new { limit = runner.Limit, peakInFlight = runner.Peak },
            ["counts"] = counts,
        });
        IndexingShared.RecordCost(run, config, TimelineStage4.StageName, client);
        Record(run, command, started, TimelineStage4.StageName, [anchorSkill, skill], llm.Sampling,
            new { stage4 = new { samples = voting.Samples, minVotes = voting.MinVotes, contextChapters = TimelineStage4.ContextChapters }, llm = IndexingShared.LlmRecord(llm.Llm) });
        foreach (var (k, v) in counts) span?.SetTag("stage4." + k, v is int or double or string ? v : JsonSerializer.Serialize(v));
        CliTelemetry.Logger("TemporalGraph.Indexing").LogInformation("Run {RunId}: stage 4 {Counts}", run.RunId, JsonSerializer.Serialize(counts));

        var table = new Table().AddColumns("Anchor", "Events", "Facts", "By source", "Timeline rows", "Rejected");
        table.AddRow($"{result.Anchor.Edtf} ({result.Anchor.Votes} votes, {result.Anchor.VotedAt})", counts["events"].ToString()!, counts["facts"].ToString()!,
            string.Join(", ", ((Dictionary<string, int>)counts["bySource"]).Select(kv => $"{kv.Key} {kv.Value}")), result.Timeline.Count.ToString(), result.Rejected.ToString());
        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"Run [bold]{run.RunId}[/]: stage 4, {Markup.Escape(Tokens(client))}");
        return 0;
    }

    // ---- Stage 5 ----------------------------------------------------------------------------------------------------

    public static async Task<int> PerceiveAsync(string root, RunFolder run, string? llmProfile, CancellationToken ct, StageOverrides? overrides = null)
    {
        if (!IsGraphShape(run, RunFolder.Stage4Json)) return IndexingShared.Fail($"{run.RunId} has no stage-4.json of the full-schema shape; run index time first.");
        var config = (overrides ?? StageOverrides.None).Over(LlmSettings.LoadConfiguration());
        var indexing = IndexingShared.IndexingSettings.From(config);
        var voting = new VotingSettings(1, 1);   // One call per chapter, one sample, over the seeded edges and the events (no text)
        var skill = SkillFile.Load(root, SkillFile.RevisePerceptions);
        var stage2 = Stage2File.Load(run);
        var stage3 = IndexingShared.Read<GraphStage3Result>(run, RunFolder.Stage3Json);
        var stage4 = IndexingShared.Read<TimelineStage4Result>(run, RunFolder.Stage4Json);
        if (IndexingShared.Llm(config, run, PerceptionStage5.StageName, llmProfile) is not { } llm) return Exit.NotReady;
        using var client = llm.Client;

        var command = $"index perceive --run {run.RunId}{(llmProfile is { } p ? $" --llm {p}" : "")}";
        var started = DateTimeOffset.UtcNow;
        using var span = StageSpan(run, PerceptionStage5.StageName, stage3.BookId, skill, llm);
        var stage0 = IndexingShared.Stage0For(run, stage3.BookId);
        run.RemoveFailures(f => f["stage"]?.GetValue<string>() == PerceptionStage5.StageName);
        PerceptionStage5Result result;
        try
        {
            result = await AnsiConsole.Status().StartAsync("Stage 5: perceptions…", status =>
                PerceptionStage5.RunAsync(client, llm.Context, skill, stage0, IndexingShared.BookText(stage3.BookId), Stage3.Entities(stage2), stage3, stage4, indexing.EvidenceThreshold, run, ct,
                    voting, (done, of) => status.Status($"Stage 5: chapter {done} of {of}…"), withText: false, cache: new CacheHints("1h")));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            span?.SetStatus(ActivityStatusCode.Error, ex.GetType().Name);
            IndexingShared.RecordCost(run, config, PerceptionStage5.StageName, client);
            return IndexingShared.Fail($"Stage 5 failed: {ex.GetType().Name}: {ex.Message}\nRun {run.RunId}", 1);
        }

        var counts = PerceptionStage5.Counts(result);
        IndexingShared.WriteWith(run, Stage5Json, result, new Dictionary<string, object?> { ["stage"] = PerceptionStage5.StageName, ["skill"] = skill.Record(), ["counts"] = counts });
        IndexingShared.RecordCost(run, config, PerceptionStage5.StageName, client);
        Record(run, command, started, PerceptionStage5.StageName, [skill], llm.Sampling, new { stage5 = new { samples = voting.Samples, minVotes = voting.MinVotes }, llm = IndexingShared.LlmRecord(llm.Llm) });
        foreach (var (k, v) in counts) span?.SetTag("stage5." + k, v is int or double ? v : JsonSerializer.Serialize(v));
        CliTelemetry.Logger("TemporalGraph.Indexing").LogInformation("Run {RunId}: stage 5 {Counts}", run.RunId, JsonSerializer.Serialize(counts));

        var table = new Table().AddColumns("PERCEIVES edges", "By source", "Closed", "Fact retractions", "Perception retractions", "Rejected");
        table.AddRow(result.Edges.Count.ToString(), string.Join(", ", ((Dictionary<string, int>)counts["bySource"]).Select(kv => $"{kv.Key} {kv.Value}")), result.Closed.ToString(),
            result.FactRetractions.Count.ToString(), result.PerceptionRetractions.ToString(), result.Rejected.ToString());
        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"Run [bold]{run.RunId}[/]: stage 5, {Markup.Escape(Tokens(client))}");
        return 0;
    }

    // ---- build-db ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// The full-schema graph of a run into <c>runs/&lt;runId&gt;/book.sqlite</c> (<see cref="GraphDb"/>), then the leak tests on it, written
    /// to <c>leak-tests.json</c>. Exit 4 when a leak test fails (the database is left for inspection).
    /// </summary>
    public static int BuildDb(string root, RunFolder run, bool force)
    {
        var path = run.File(RunFolder.BookSqlite);
        if (File.Exists(path) && !force) return IndexingShared.Fail($"{Path.GetRelativePath(root, path)} exists; use --force to replace it.");
        if (!IsGraphShape(run, RunFolder.Stage4Json)) return IndexingShared.Fail($"{run.RunId} has no stage-4.json of the full-schema shape; run index time first.");
        if (!File.Exists(run.File(Stage5Json))) return IndexingShared.Fail($"{run.RunId} has no {Stage5Json}; run index perceive first.");
        var stage2 = Stage2File.Load(run);
        var stage3 = IndexingShared.Read<GraphStage3Result>(run, RunFolder.Stage3Json);
        var stage4 = IndexingShared.Read<TimelineStage4Result>(run, RunFolder.Stage4Json);
        var stage5 = IndexingShared.Read<PerceptionStage5Result>(run, Stage5Json);
        using var span = IndexingTelemetry.Source.StartActivity($"build-db {stage3.BookId}");
        span?.SetTag(IndexingTelemetry.RunId, run.RunId);
        span?.SetTag(IndexingTelemetry.BookId, stage3.BookId);

        if (File.Exists(path)) File.Delete(path);
        GraphDbCounts counts;
        StageLogs.Line(run, "build-db: start");
        try { counts = GraphDb.Build(path, IndexingShared.Stage0For(run, stage3.BookId), stage2, stage3, stage4, stage5); }
        catch (InvalidOperationException ex)
        {
            StageLogs.Line(run, "build-db: failed: " + ex.Message);
            return IndexingShared.Fail("build-db: " + ex.Message, 1);
        }
        // The layout, once, into the file just written. Without Node.js the build still succeeds: the file has no layout and
        // the web lays the graph out itself (in a worker).
        var layout = GraphLayoutStep.Run(root, path);
        StageLogs.Line(run, "build-db: " + GraphLayoutStep.Line(layout));
        if (layout.Summary is { } laidOut) run.Write(GraphLayoutStep.FileName, laidOut);
        var leaks = LeakTests.Run(BookGraph.Load(path));
        StageLogs.Line(run, $"build-db: done · leak tests {(leaks.All(l => l.Passed) ? "pass" : "FAIL (" + string.Join(", ", leaks.Where(l => !l.Passed).Select(l => l.Name)) + ")")}");
        run.Write(LeakTestsJson, new { runId = run.RunId, book = stage3.BookId, ranAt = DateTimeOffset.UtcNow, passed = leaks.All(l => l.Passed), tests = leaks });
        foreach (var (k, v) in counts.Rows) span?.SetTag("rows." + k, v);
        run.RecordCommand(new { command = $"index build-db --run {run.RunId}", startedAt = DateTimeOffset.UtcNow, rows = counts.Rows, graph = counts, leakTestsPassed = leaks.All(l => l.Passed) },
            new Dictionary<string, object?>());

        var table = new Table().AddColumns("Table", "Rows");
        foreach (var (k, v) in counts.Rows) table.AddRow(k, v.ToString());
        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"Nodes: {Markup.Escape(string.Join(", ", counts.Labels.Select(kv => $"{kv.Value} {kv.Key}")))}");
        AnsiConsole.MarkupLine($"Edges: {Markup.Escape(string.Join(", ", counts.EdgeTypes.Select(kv => $"{kv.Value} {kv.Key}")))}");
        AnsiConsole.MarkupLine(Markup.Escape(char.ToUpperInvariant(GraphLayoutStep.Line(layout)[0]) + GraphLayoutStep.Line(layout)[1..]));
        AnsiConsole.MarkupLine($"Story time: {counts.StoryFromLlm} from the model, {counts.StoryPropagated} propagated by code, {counts.StoryNull} NULL; anchor {Markup.Escape(counts.AnchorEdtf)}");
        var leakTable = new Table().AddColumns("Leak test", "Checked", "Result");
        foreach (var l in leaks) leakTable.AddRow(l.Name, l.Checked.ToString(), l.Passed ? "[green]pass[/]" : $"[red]FAIL ({l.Failures.Count})[/]");
        AnsiConsole.Write(leakTable);
        foreach (var f in leaks.Where(l => !l.Passed).SelectMany(l => l.Failures.Take(5).Select(x => $"{l.Name}: {x}"))) AnsiConsole.MarkupLine($"[red]{Markup.Escape(f)}[/]");
        AnsiConsole.MarkupLine($"Wrote [bold]{Markup.Escape(Path.GetRelativePath(root, path))}[/]");
        // The database was written and a leak test is bad.
        return leaks.All(l => l.Passed) ? Exit.Ok
            : CliContext.Problem(Exit.CheckFailed, $"Leak tests failed ({string.Join(", ", leaks.Where(l => !l.Passed).Select(l => l.Name))}): runs/{run.RunId}/{LeakTestsJson}", "leak-test");
    }

    /// <summary>"2..5", "2-5" or "3" → the stages, within 2..5; null when it can't be read.</summary>
    public static (int First, int Last)? ParseStages(string spec)
    {
        var parts = spec.Split(["..", "-"], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is not (1 or 2) || !parts.All(p => int.TryParse(p, out _))) return null;
        var (a, b) = (int.Parse(parts[0]), int.Parse(parts[^1]));
        return a is >= 2 and <= 5 && b is >= 2 and <= 5 && a <= b ? (a, b) : null;
    }
}

/// <summary>
/// The options a run can set over the <c>Indexing</c> configuration, with the same names as the keys. <c>index run</c>,
/// <c>index rebuild</c> and the stage commands that use them derive their settings from this.
/// </summary>
public class StageSettings : GlobalSettings
{
    [CommandOption("--chunk-tokens <N>")][Description("Indexing:Chunk:Tokens (8000): a chapter is one chunk, split at paragraphs into equal parts above this size. 1000: the 1,000-token windows of before the one-pass pipeline.")] public int? ChunkTokens { get; init; }
    [CommandOption("--glean")][Description("Indexing:StageA:Glean (off): a second \"what did you miss\" call per chunk on the cached text.")] public bool Glean { get; init; }
    [CommandOption("--judge <BOOL>")][Description("Indexing:StageA:Judge (true): ask the model once per chunk about items whose evidence is just under the threshold.")] public bool? Judge { get; init; }
    [CommandOption("--judge-band <LOW-HIGH>")][Description("Indexing:StageA:JudgeBand (0.80-0.90): the evidence scores the judge is asked about.")] public string? JudgeBand { get; init; }
    [CommandOption("--max-groups-per-call <N>")][Description("Indexing:StageB:MaxGroupsPerCall (25): name groups in one resolution call.")] public int? MaxGroupsPerCall { get; init; }
    [CommandOption("--samples <K>")][Description("Indexing:StageB:Samples (3): answers asked per block of name groups.")] public int? Samples { get; init; }
    [CommandOption("--min-votes <M>")][Description("Indexing:StageB:MinVotes (2): answers that must put two groups in one set.")] public int? MinVotes { get; init; }
    [CommandOption("--parallel <N>")][Description("Indexing:Parallel (8): model calls in flight per stage.")] public int? Parallel { get; init; }
    [CommandOption("--cache <BOOL>")][Description("Indexing:Cache (true): cache each stage's system text at the provider (Anthropic).")] public bool? Cache { get; init; }

    [CommandOption("--thinking <LEVEL>")][Description("Indexing:StageA:Thinking: Stage 1's thinking, billed as output: off, low, medium, high, default (nothing sent: the model's own default) or a number of tokens.")] public string? Thinking { get; init; }

    public StageOverrides Overrides() => new(ChunkTokens, Glean ? true : null, Judge, JudgeBand, MaxGroupsPerCall, Samples, MinVotes, Parallel, Cache, Thinking);
}

/// <summary>
/// <c>index extract --book &lt;id&gt; [--chapters A-B] [--run &lt;runId&gt;]</c>: Stage 1. One model call per chunk for everything the
/// chunk tells (names, events, facts, perceptions…), into a new run folder (or <c>--run</c>): stage-1.json and extract.json.
/// </summary>
public sealed class IndexExtractCommand : AsyncCommand<IndexExtractCommand.Settings>
{
    public sealed class Settings : StageSettings
    {
        [CommandOption("--book <ID>")] public string Book { get; init; } = "";
        [CommandOption("--chapters <RANGE>")][Description("e.g. 1-3; default: every chapter.")] public string? Chapters { get; init; }
        [CommandOption("--run <RUN>")][Description("Write into this run folder; default: a new one.")] public string? Run { get; init; }
        [CommandOption("--llm <NAME>")][Description("A named LLM profile (Llm:Profiles:<name>); default: Indexing:Llm.")] public string? Llm { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        var root = RepoPaths.FindRoot();
        if (!BookProfiles.Has(root, settings.Book)) return IndexingShared.Fail($"Unknown book '{settings.Book}'.");
        var all = IndexingShared.Stage0For(settings.Book).Chapters.Count;
        if ((settings.Chapters is null ? (1, all) : IndexingShared.ParseChapters(settings.Chapters)) is not { } range) return IndexingShared.Fail("--chapters must look like 1-3.");
        RunFolder run;
        try { run = settings.Run is { } id ? RunFolder.Open(RepoPaths.Runs(root), id) : RunFolder.Create(RepoPaths.Runs(root)); }
        catch (DirectoryNotFoundException ex) { return IndexingShared.Fail(ex.Message); }
        var code = await Pipeline.ExtractAsync(root, run, settings.Book, range, settings.Llm, settings.Overrides(), ct);
        AnsiConsole.MarkupLine($"Run [bold]{run.RunId}[/]: {Markup.Escape(run.Path)}");
        return code;
    }
}

/// <summary>
/// <c>index --stages 2..5 (--run &lt;runId&gt; | --stage1-from &lt;runId&gt;)</c>: runs the listed stages in order on one run, each from the
/// stage file before it (2 resolve, 3 link, 4 time, 5 perceive). With <c>--stage1-from</c>, Stage 1's output is reused: a new run gets
/// a copy of that run's stage-1.json and extract.json and nothing is extracted again. Stage 1 itself is <c>index extract</c>.
/// </summary>
public sealed class IndexStagesCommand : AsyncCommand<IndexStagesCommand.Settings>
{
    public sealed class Settings : StageSettings
    {
        [CommandOption("--stages <RANGE>")][Description("The stages to run, e.g. 2..5 (2 resolve, 3 link, 4 time, 5 perceive).")] public string? Stages { get; init; }
        [CommandOption("--run <RUN>")][Description("The run to continue (it must hold the file of the stage before the first one listed).")] public string Run { get; init; } = "";
        [CommandOption("--stage1-from <RUN>")][Description("Copy this run's Stage 1 output into a new run and run the stages on it.")] public string? Stage1From { get; init; }
        [CommandOption("--booknlp-from <RUN>")][Description("Stage 2: copy this run's BookNLP output instead of running BookNLP again.")] public string? BookNlpFrom { get; init; }
        [CommandOption("--llm <NAME>")][Description("A named LLM profile (Llm:Profiles:<name>); default: Indexing:Llm.")] public string? Llm { get; init; }
        [CommandOption("--build-db")][Description("After the last stage (5), also write runs/<runId>/book.sqlite and run the leak tests.")] public bool BuildDb { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        if (settings.Stages is null) return IndexingShared.Fail("Give --stages (e.g. --stages 2..5) with --run or --stage1-from, or a sub-command: index run | extract | resolve | link | time | perceive | build-db | promote | …");
        if (Pipeline.ParseStages(settings.Stages) is not { } stages) return IndexingShared.Fail("--stages must be within 2..5, e.g. 2..5 or 3 (Stage 1 is index extract).");
        var root = RepoPaths.FindRoot();
        RunFolder run;
        try
        {
            if (settings.Stage1From is { } from)
            {
                if (settings.Run.Length > 0) return IndexingShared.Fail("Give --run or --stage1-from, not both.");
                if (stages.First != 2) return IndexingShared.Fail("--stage1-from starts at Stage 2.");
                run = FrozenStage1.Copy(RunFolder.Open(RepoPaths.Runs(root), from), RepoPaths.Runs(root));
                AnsiConsole.MarkupLine($"Stage 1 reused from [bold]{Markup.Escape(from)}[/] → new run [bold]{run.RunId}[/]");
            }
            else run = RunFolder.Open(RepoPaths.Runs(root), settings.Run);
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or FileNotFoundException or InvalidOperationException) { return IndexingShared.Fail(ex.Message); }

        var overrides = settings.Overrides();
        for (var stage = stages.First; stage <= stages.Last; stage++)
        {
            var code = stage switch
            {
                2 => await Pipeline.ResolveAsync(root, run, settings.Llm, settings.BookNlpFrom, ct, overrides),
                3 => Pipeline.Link(root, run),
                4 => await Pipeline.TimeAsync(root, run, settings.Llm, ct, overrides),
                _ => await Pipeline.PerceiveAsync(root, run, settings.Llm, ct, overrides),
            };
            if (code != 0) return code;
        }
        if (settings.BuildDb && stages.Last == 5) return Pipeline.BuildDb(root, run, force: true);
        AnsiConsole.MarkupLine($"Run [bold]{run.RunId}[/]: {Markup.Escape(run.Path)}");
        return 0;
    }
}

/// <summary><c>index perceive --run &lt;runId&gt;</c>: Stage 5 (perception seeding by code, one call per chapter over the seeded edges and
/// the chapter's events for the changes, closing by code) on a run's stage-3.json and stage-4.json.</summary>
public sealed class IndexPerceiveCommand : AsyncCommand<IndexPerceiveCommand.Settings>
{
    public sealed class Settings : GlobalSettings
    {
        [CommandOption("--run <RUN>")] public string Run { get; init; } = "";
        [CommandOption("--llm <NAME>")][Description("A named LLM profile (Llm:Profiles:<name>); default: Indexing:Llm.")] public string? Llm { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        var root = RepoPaths.FindRoot();
        RunFolder run;
        try { run = RunFolder.Open(RepoPaths.Runs(root), settings.Run); }
        catch (DirectoryNotFoundException ex) { return IndexingShared.Fail(ex.Message); }
        return await Pipeline.PerceiveAsync(root, run, settings.Llm, ct);
    }
}