using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using TemporalGraph.Cli.Indexing;
using TemporalGraph.Cli.Runs;
using TemporalGraph.Cli.Telemetry;
using TemporalGraph.Llm;

namespace TemporalGraph.Cli;

/// <summary>
/// What a run can set over the configuration, with the same names as the keys: <c>--chunk-tokens</c>
/// (<c>Indexing:Chunk:Tokens</c>), <c>--glean</c> / <c>--judge</c> / <c>--judge-band</c> (<c>Indexing:StageA:…</c>),
/// <c>--max-groups-per-call</c>, <c>--samples</c>, <c>--min-votes</c> (<c>Indexing:StageB:…</c>), <c>--parallel</c>
/// (<c>Indexing:Parallel</c>), <c>--cache</c> (<c>Indexing:Cache</c>) and <c>--thinking</c> (<c>Indexing:StageA:Thinking</c>).
/// Null: the configuration's value.
/// </summary>
public sealed record StageOverrides(int? ChunkTokens = null, bool? Glean = null, bool? Judge = null, string? JudgeBand = null, int? MaxGroupsPerCall = null,
    int? Samples = null, int? MinVotes = null, int? Parallel = null, bool? Cache = null, string? Thinking = null)
{
    public static readonly StageOverrides None = new();

    /// <summary>The configuration with these overrides on top (the stage code reads the keys, so one place decides).</summary>
    public IConfiguration Over(IConfiguration config)
    {
        var values = new Dictionary<string, string?>();
        void Set(string key, object? value) { if (value is not null) values[key] = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture); }
        Set("Indexing:Chunk:Tokens", ChunkTokens);
        if (ChunkTokens is { } tokens) values["Indexing:Chunk:ChapterAligned"] = tokens >= 4000 ? "true" : "false";
        Set("Indexing:StageA:Glean", Glean);
        Set("Indexing:StageA:Judge", Judge);
        Set("Indexing:StageA:JudgeBand", JudgeBand);
        Set("Indexing:StageB:MaxGroupsPerCall", MaxGroupsPerCall);
        Set("Indexing:StageB:Samples", Samples);
        Set("Indexing:StageB:MinVotes", MinVotes);
        Set("Indexing:Parallel", Parallel);
        Set("Indexing:Cache", Cache);
        Set("Indexing:StageA:Thinking", Thinking?.Trim());
        return values.Count == 0 ? config : new ConfigurationBuilder().AddConfiguration(config).AddInMemoryCollection(values).Build();
    }

    /// <summary>What is wrong with the values; empty when nothing is.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (ChunkTokens is <= 0) problems.Add("--chunk-tokens must be 1 or more.");
        if (JudgeBand is not null && ExtractSettings.ParseBand(JudgeBand) is null) problems.Add("--judge-band must look like 0.80-0.90.");
        if (MaxGroupsPerCall is < 2) problems.Add("--max-groups-per-call must be 2 or more.");
        if (Samples is < 1) problems.Add("--samples must be 1 or more.");
        if (MinVotes is < 1 || (Samples is { } k && MinVotes is { } m && m > k)) problems.Add("--min-votes must be between 1 and --samples.");
        if (Parallel is < 1) problems.Add("--parallel must be 1 or more.");
        if (Thinking is not null && !ExtractSettings.IsThinking(Thinking)) problems.Add("--thinking must be off, low, medium, high, default or a number of tokens.");
        return problems;
    }
}

public static partial class Pipeline
{
    /// <summary><c>Indexing:Parallel</c> (default 8): model calls in flight per stage.</summary>
    public static int Parallel(IConfiguration config) => Math.Max(1, config.GetValue("Indexing:Parallel", StageRunner.DefaultParallel));

    public static ExtractSettings ExtractSettingsFrom(IConfiguration config)
    {
        var band = ExtractSettings.ParseBand(config["Indexing:StageA:JudgeBand"]) ?? (0.80, 0.90);
        return new ExtractSettings(config.GetValue("Indexing:EvidenceThreshold", 0.9), config.GetValue("Indexing:StageA:Glean", false), config.GetValue("Indexing:StageA:Judge", true),
            band.Low, band.High, config.GetValue("Indexing:StageA:MaxOutputTokens", ExtractStage.DefaultMaxOutputTokens), config["Indexing:StageA:Thinking"]?.Trim() is { Length: > 0 } thinking ? thinking : ExtractSettings.DefaultThinking);
    }

    // ---- Stage 1: extract -------------------------------------------------------------------------------------------

    /// <summary>
    /// Stage 1 (extract) into <paramref name="run"/>: one call per chunk for everything the chunk tells
    /// (<see cref="ExtractStage"/>). Writes stage-1.json (the mentions, as Stage 2 reads them) and extract.json (the items, with
    /// names), and the run's plan into run.json: book, chapters, text hash and the chunking, which the later stages read back.
    /// </summary>
    public static async Task<int> ExtractAsync(string root, RunFolder run, string book, (int First, int Last) chapters, string? llmProfile, StageOverrides overrides, CancellationToken ct)
    {
        if (!BookProfiles.Has(root, book)) return IndexingShared.Fail($"Unknown book '{book}'.");
        if (overrides.Validate() is [var problem, ..]) return IndexingShared.Fail(problem);
        var config = overrides.Over(LlmSettings.LoadConfiguration());
        var settings = ExtractSettingsFrom(config);
        var chunking = ChunkSettings.From(config);
        var skill = SkillFile.Load(root, SkillFile.ExtractGraph);
        var stage0 = Stage0.Prepare(BookProfiles.For(root, book), SourceTexts.Read(root, book), SourceTexts.Sha256(root, book), chunking, run.RunId);
        if (chapters.First < 1 || chapters.Last > stage0.Chapters.Count) return IndexingShared.Fail($"--chapters must be within 1-{stage0.Chapters.Count}.");
        if (IndexingShared.Llm(config, run, ExtractStage.StageName, llmProfile) is not { } llm) return Exit.NotReady;
        using var client = llm.Client;

        var command = $"index extract --book {book} --chapters {chapters.First}-{chapters.Last} --chunk-tokens {chunking.ChunkTokens}{(settings.Glean ? " --glean" : "")}{(llmProfile is { } p ? $" --llm {p}" : "")}";
        var started = DateTimeOffset.UtcNow;
        using var span = StageSpan(run, ExtractStage.StageName, book, skill, llm);
        run.RemoveFailures(f => f["stage"]?.GetValue<string>() == ExtractStage.StageName);
        // The plan first, so a later stage (and a continued run) reads the same chunking even when this stage stops part-way.
        run.RecordCommand(new { command, startedAt = started }, new Dictionary<string, object?>
        {
            ["book"] = book,
            ["chapters"] = new { first = chapters.First, last = chapters.Last },
            ["textSha256"] = stage0.TextSha256,
            ["git"] = IndexingShared.Git(root),
            ["settings"] = new
            {
                evidenceThreshold = settings.EvidenceThreshold, glean = settings.Glean, judge = settings.Judge, judgeBand = $"{settings.JudgeLow:0.00}-{settings.JudgeHigh:0.00}", thinking = settings.Thinking,
                chunkTokens = chunking.ChunkTokens, overlapTokens = chunking.OverlapTokens, chapterAligned = chunking.ChapterAligned, structuredOutput = "json_schema, strict",
                samples = 1, minVotes = 1, parallel = Parallel(config), cache = config.GetValue("Indexing:Cache", true), llm = IndexingShared.LlmRecord(llm.Llm),
            },
            ["model"] = new { type = llm.Llm.Type, model = llm.Llm.Model },
        });

        var runner = new StageRunner(Parallel(config));
        Stage1Result mentions;
        ExtractResult extract;
        try
        {
            using var active = runner.Begin();
            (mentions, extract) = await AnsiConsole.Status().StartAsync($"Stage 1: extracting chapters {chapters.First}–{chapters.Last}…", status =>
                ExtractStage.RunAsync(client, llm.Context, skill, stage0, IndexingShared.BookText(book), chapters.First, chapters.Last, settings, runner, run, ct,
                    (done, of) => status.Status($"Stage 1: chunk {done} of {of}…")));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            span?.SetStatus(ActivityStatusCode.Error, ex.GetType().Name);
            IndexingShared.RecordCost(run, config, ExtractStage.StageName, client);
            return IndexingShared.Fail($"Stage 1 failed: {ex.GetType().Name}: {ex.Message}\nRun {run.RunId}", 1);
        }

        var counts = ExtractStage.Counts(mentions, extract);
        Stage1File.WriteStage1(run, mentions, skill.Record(), new Dictionary<string, object?> { ["shape"] = ExtractStage.Shape });
        IndexingShared.WriteWith(run, ExtractStage.FileName, extract, new Dictionary<string, object?> { ["stage"] = ExtractStage.StageName, ["skill"] = skill.Record(), ["counts"] = counts });
        IndexingShared.RecordCost(run, config, ExtractStage.StageName, client);
        Record(run, command, started, ExtractStage.StageName, [skill], llm.Sampling, new { stage1 = new { extract = settings, parallel = runner.Limit, peakInFlight = runner.Peak } });
        foreach (var (k, v) in counts) span?.SetTag("stage1." + k, v is int or double ? v : JsonSerializer.Serialize(v));
        CliTelemetry.Logger("TemporalGraph.Indexing").LogInformation("Run {RunId}: stage 1 {Counts}", run.RunId, JsonSerializer.Serialize(counts));

        var table = new Table().AddColumns("Chunks", "Calls", "Names (mentions)", "Events", "Facts", "Perceptions", "Dropped", "Judged (kept)", "In flight (peak)");
        table.AddRow(counts["chunks"].ToString()!, counts["calls"].ToString()!, $"{counts["names"]} ({counts["mentions"]})", counts["events"].ToString()!, counts["facts"].ToString()!,
            counts["perceptions"].ToString()!, counts["dropped"].ToString()!, $"{counts["judged"]} ({counts["judgeKept"]})", $"{runner.Limit} ({runner.Peak})");
        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"Run [bold]{run.RunId}[/]: stage 1, {Markup.Escape(Tokens(client))}");
        return extract.Chunks.Count > 0 && extract.Chunks.All(c => c.Unreadable > 0 && c.Events.Count == 0)
            ? IndexingShared.Fail($"Stage 1: no chunk's reply could be read. Run {run.RunId}", 1) : 0;
    }

    // ---- Stage 3: link ----------------------------------------------------------------------------------------------

    /// <summary>
    /// Stage 3 (link; code only): the names of extract.json pointed at stage-2.json's entities (<see cref="LinkStage"/>), into
    /// a stage-3.json of the full-schema shape. An entity added for a name no entity goes by is appended to stage-2.json, flagged
    /// <c>unlinked</c>, so the later stages and build-db find it there.
    /// </summary>
    public static int Link(string root, RunFolder run)
    {
        if (!File.Exists(run.File(ExtractStage.FileName))) return IndexingShared.Fail($"{run.RunId} has no {ExtractStage.FileName}; run index extract first.", Exit.NotReady);
        if (!File.Exists(run.File(RunFolder.Stage2Json))) return IndexingShared.Fail($"{run.RunId} has no stage-2.json; run index resolve first.", Exit.NotReady);
        var config = LlmSettings.LoadConfiguration();
        var threshold = config.GetValue("Indexing:EvidenceThreshold", 0.9);
        var dedupe = config.GetValue("Indexing:Stage3:FactDedupeThreshold", GraphStage3.DefaultDedupeThreshold);
        var extract = IndexingShared.Read<ExtractResult>(run, ExtractStage.FileName);
        DropUnlinked(run);
        var stage2 = Stage2File.Load(run);
        var stage0 = IndexingShared.Stage0For(run, extract.BookId);
        var started = DateTimeOffset.UtcNow;
        using var span = StageSpan(run, LinkStage.StageName, extract.BookId, null, null);
        run.RemoveFailures(f => f["stage"]?.GetValue<string>() == LinkStage.StageName);
        StageLogs.Begin(run, LinkStage.StageName);
        // BookNLP's chains of the run (Stage 2 left them in stage2/booknlp) for the coreference step.
        var chains = BookNlpChains.Exists(run) ? BookNlpChains.Load(run) : [];
        var result = LinkStage.Run(extract, Stage3.Entities(stage2), stage0, IndexingShared.BookText(extract.BookId), threshold, dedupe, run, chains, line => StageLogs.Line(run, line));
        AppendUnlinked(run, result.Unlinked);

        var counts = GraphStage3.Counts(result.Graph);
        IndexingShared.WriteWith(run, RunFolder.Stage3Json, result.Graph, new Dictionary<string, object?>
        {
            ["stage"] = LinkStage.StageName,
            ["producedBy"] = "link (code only): extract.json's names pointed at stage-2.json's entities",
            ["linking"] = new
            {
                namesByHow = result.NamesByHow,
                unlinkedEntities = result.Unlinked.Select(u => new { id = u.Id, name = u.Name, type = u.Type, mentions = u.Mentions.Count, kind = u.Kind }),
                inferredPronouns = result.Graph.Facts.Where(f => f.SubjectSource is not null).Select(f => new { fact = f.Id, subject = f.SubjectSource }),
                dropped = result.Dropped.Select(d => d.Name).Distinct(),
                coreferenceChains = chains.Count > 0,
                // Every name form steps 0–3 did not link: how often it is used, what the earlier order did with it, where it goes now.
                lateForms = result.Names.Where(n => n.Was is "dropped" or "new").GroupBy(n => n.Name).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
                    .Select(g => new
                    {
                        name = g.Key, uses = g.Count(), was = g.GroupBy(n => n.Was).OrderByDescending(w => w.Count()).First().Key,
                        now = g.GroupBy(n => n.How).OrderByDescending(h => h.Count()).ToDictionary(h => h.Key, h => h.Count()),
                        entity = g.Select(n => n.Entity).FirstOrDefault(e => e is not null), nearestAlias = g.FirstOrDefault(n => n.Nearest is not null)?.Nearest,
                        nearestScore = g.FirstOrDefault(n => n.Nearest is not null)?.NearestScore ?? 0,
                    }),
                inferredDiscoveries = result.Inferred.Select(i => new { fact = i.Fact, @event = i.Event, how = i.How, discovery = GDiscovery.InferredByCode }),
            },
            ["counts"] = counts,
        });
        run.RecordCommand(new { command = $"index link --run {run.RunId}", startedAt = started, finishedAt = DateTimeOffset.UtcNow }, new Dictionary<string, object?>());
        StageLogs.End(run, LinkStage.StageName, 0, 0, 0);
        foreach (var (k, v) in counts) span?.SetTag("stage3." + k, v is int or double ? v : JsonSerializer.Serialize(v));

        var g = result.Graph;
        var table = new Table().AddColumns("Events (narrated / referenced)", "Facts", "Discoveries (by code)", "Perceptions", "Retractions", "Identities", "Relations", "Rejected", "Facts merged", "Names linked", "New entities");
        table.AddRow($"{g.Events.Count} ({g.Events.Count(e => e.Kind == Data.EventKinds.Narrated)} / {g.Events.Count(e => e.Kind == Data.EventKinds.Referenced)})", g.Facts.Count.ToString(),
            $"{g.Facts.Sum(f => f.Discoveries.Count)} ({result.Inferred.Count})", g.Perceptions.Count.ToString(), g.Retractions.Count.ToString(), g.Identities.Count.ToString(), g.Relations.Count.ToString(),
            g.Rejected.ToString(), g.FactsMerged.ToString(), string.Join(", ", result.NamesByHow.Select(kv => $"{kv.Key} {kv.Value}")), result.Unlinked.Count.ToString());
        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"Run [bold]{run.RunId}[/]: stage 3 (link), no model calls");
        return 0;
    }

    /// <summary>
    /// The link stage over a run's files, in memory. The run folder is only read and nothing is written: how a change to the
    /// stage is counted on a run that must stay as it is. The entities an earlier link added are left out, as <see cref="Link"/> does.
    /// </summary>
    public static LinkResult LinkInMemory(RunFolder run, double threshold = 0.9, double dedupe = GraphStage3.DefaultDedupeThreshold, Action<string>? log = null)
    {
        var extract = IndexingShared.Read<ExtractResult>(run, ExtractStage.FileName);
        var added = JsonNode.Parse(File.ReadAllText(run.File(RunFolder.Stage2Json)))?["entities"] is JsonArray all
            ? all.Where(e => e?["unlinked"]?.GetValue<bool>() == true).Select(e => e!["id"]!.GetValue<int>()).ToHashSet() : [];
        var loaded = Stage2File.Load(run);
        var stage2 = loaded with { Entities = loaded.Entities.Where(e => !added.Contains(e.Id)).ToList() };
        var chains = BookNlpChains.Exists(run) ? BookNlpChains.Load(run) : [];
        return LinkStage.Run(extract, Stage3.Entities(stage2), IndexingShared.Stage0For(run, extract.BookId), IndexingShared.BookText(extract.BookId), threshold, dedupe, null, chains, log);
    }

    /// <summary>Removes the entities an earlier link added to stage-2.json (running the stage again starts from Stage 2's own).</summary>
    private static void DropUnlinked(RunFolder run)
    {
        var path = run.File(RunFolder.Stage2Json);
        if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject root || root["entities"] is not JsonArray entities) return;
        var kept = entities.Where(e => e?["unlinked"]?.GetValue<bool>() != true).Select(e => e!.DeepClone()).ToArray();
        if (kept.Length == entities.Count) return;
        root["entities"] = new JsonArray(kept);
        File.WriteAllText(path, root.ToJsonString(RunFolder.Json) + "\n");
    }

    private static void AppendUnlinked(RunFolder run, IReadOnlyList<UnlinkedEntity> unlinked)
    {
        if (unlinked.Count == 0) return;
        var path = run.File(RunFolder.Stage2Json);
        var root = (JsonObject)JsonNode.Parse(File.ReadAllText(path))!;
        var entities = (JsonArray)root["entities"]!;
        foreach (var u in unlinked)
            entities.Add(JsonSerializer.SerializeToNode(new
            {
                id = u.Id,
                canonicalName = u.Name,
                type = u.Type,
                aliases = new[] { u.Name },
                firstMention = new { charStart = u.Mentions[0].Start, chapter = u.Mentions[0].Chapter },
                mentions = u.Mentions.Select(m => new { charStart = m.Start, charEnd = m.End, chapter = m.Chapter, text = u.Name, pass = (string?)null }),
                groups = Array.Empty<int>(),
                unlinked = true,
                kind = u.Kind,
            }, RunFolder.Json));
        File.WriteAllText(path, root.ToJsonString(RunFolder.Json) + "\n");
    }
}
