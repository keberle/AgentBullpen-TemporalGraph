using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using Spectre.Console.Cli;
using TemporalGraph.Cli.Gold;
using TemporalGraph.Cli.Indexing;
using TemporalGraph.Llm;
using TemporalGraph.Cli.Runs;
using TemporalGraph.Cli.Telemetry;
using TemporalGraph.Data;

namespace TemporalGraph.Cli;

/// <summary>
/// <c>index link --run &lt;runId&gt;</c>: Stage 3, code only. The names in the run's extract.json are pointed at
/// stage-2.json's entities and the result is checked and assembled into stage-3.json (<see cref="Pipeline.Link"/>). The
/// <c>index facts</c> command (one voted model call per chunk with the whole entity list) and <c>index facts-stability</c> are gone.
/// </summary>
public sealed class IndexLinkCommand : Command<IndexLinkCommand.Settings>
{
    public sealed class Settings : GlobalSettings
    {
        [CommandOption("--run <RUN>")][Description("The run: it has extract.json (index extract) and stage-2.json (index resolve).")] public string Run { get; init; } = "";
    }

    public override int Execute(CommandContext context, Settings settings, CancellationToken ct)
    {
        var root = RepoPaths.FindRoot();
        RunFolder run;
        try { run = RunFolder.Open(RepoPaths.Runs(root), settings.Run); }
        catch (DirectoryNotFoundException ex) { return IndexingShared.Fail(ex.Message); }
        return Pipeline.Link(root, run);
    }
}

/// <summary>
/// <c>index time --run &lt;runId&gt;</c>: Stage 4. One call for the anchor and one call per chapter over the chapter's Events and
/// Facts (no text), one sample each, the chapters side by side; code resolves the relative dates and fills the rest.
/// </summary>
public sealed class IndexTimeCommand : AsyncCommand<IndexTimeCommand.Settings>
{
    public sealed class Settings : GlobalSettings
    {
        [CommandOption("--run <RUN>")] public string Run { get; init; } = "";
        [CommandOption("--llm <NAME>")][Description("A named LLM profile (Llm:Profiles:<name>); default: Indexing:Llm.")] public string? Llm { get; init; }
        [CommandOption("--parallel <N>")][Description("Model calls in flight (default Indexing:Parallel, 8).")] public int? Parallel { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        var root = RepoPaths.FindRoot();
        RunFolder run;
        try { run = RunFolder.Open(RepoPaths.Runs(root), settings.Run); }
        catch (DirectoryNotFoundException ex) { return IndexingShared.Fail(ex.Message); }
        return await Pipeline.TimeAsync(root, run, settings.Llm, ct, new StageOverrides(Parallel: settings.Parallel));
    }
}

/// <summary>
/// <c>index build-db --run &lt;runId&gt; [--force]</c>: writes the run's graph into <c>runs/&lt;runId&gt;/book.sqlite</c>, a scratch
/// book database for inspection and the later graph engine (never committed or embedded).
/// </summary>
public sealed class IndexBuildDbCommand : Command<IndexBuildDbCommand.Settings>
{
    public sealed class Settings : GlobalSettings
    {
        [CommandOption("--run <RUN>")] public string Run { get; init; } = "";
        [CommandOption("--force")][Description("Replace an existing book.sqlite.")] public bool Force { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings, CancellationToken ct)
    {
        var root = RepoPaths.FindRoot();
        RunFolder run;
        try { run = RunFolder.Open(RepoPaths.Runs(root), settings.Run); }
        catch (DirectoryNotFoundException ex) { return IndexingShared.Fail(ex.Message); }
        if (!File.Exists(run.File(RunFolder.Stage3Json))) return IndexingShared.Fail($"{run.RunId} has no stage-3.json; run index link first.");
        // A run of the full schema is written by GraphDb (and leak-tested); the code below builds the earlier shape.
        if (Pipeline.IsGraphShape(run, RunFolder.Stage3Json)) return Pipeline.BuildDb(root, run, settings.Force);
        var path = run.File(RunFolder.BookSqlite);
        if (File.Exists(path) && !settings.Force) return IndexingShared.Fail($"{Path.GetRelativePath(root, path)} exists; use --force to replace it.");

        var stage2 = Stage2File.Load(run);
        var stage3 = IndexingShared.Read<Stage3Result>(run, RunFolder.Stage3Json);
        var stage4 = File.Exists(run.File(RunFolder.Stage4Json)) ? IndexingShared.Read<Stage4Result>(run, RunFolder.Stage4Json) : null;
        using var span = IndexingTelemetry.Source.StartActivity($"build-db {stage3.BookId}");
        span?.SetTag(IndexingTelemetry.RunId, run.RunId);
        span?.SetTag(IndexingTelemetry.BookId, stage3.BookId);

        if (File.Exists(path)) File.Delete(path);
        var counts = ScratchDb.Build(path, IndexingShared.Stage0For(stage3.BookId), stage2, stage3, stage4);
        foreach (var (k, v) in counts) span?.SetTag("rows." + k, v);
        run.RecordCommand(new { command = $"index build-db --run {run.RunId}", startedAt = DateTimeOffset.UtcNow, rows = counts }, new Dictionary<string, object?>());

        var table = new Table().AddColumns("Table", "Rows");
        foreach (var (k, v) in counts) table.AddRow(k, v.ToString());
        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"Wrote [bold]{Markup.Escape(Path.GetRelativePath(root, path))}[/]{(stage4 is null ? " (no stage-4.json: no story time)" : "")}");
        return 0;
    }
}


/// <summary>Frozen input for <c>index facts --stage2-from</c> and <c>index time --stage3-from</c>.</summary>
public static class FrozenStages
{
    private static readonly Dictionary<string, int> SkillStage = new()
    {
        [SkillFile.ExtractMentions] = 1, [SkillFile.ResolveEntities] = 2, [SkillFile.ExtractFacts] = 3, [SkillFile.AssignStoryTime] = 4,
    };

    /// <summary>
    /// <paramref name="runId"/> opened as is, or (with <paramref name="from"/>) a new frozen copy of it through
    /// <paramref name="throughStage"/>. Prints the error and returns null when either is wrong.
    /// </summary>
    public static RunFolder? OpenOrCopy(string root, string runId, string? from, int throughStage, int? stage3MinVotes)
    {
        try
        {
            if (from is null) return RunFolder.Open(RepoPaths.Runs(root), runId);
            if (runId.Length > 0) { IndexingShared.Fail("Give --run or the frozen-input option, not both."); return null; }
            var source = RunFolder.Open(RepoPaths.Runs(root), from);
            var run = Copy(source, RepoPaths.Runs(root), throughStage, stage3MinVotes);
            AnsiConsole.MarkupLine($"Frozen Stages 1–{throughStage} from [bold]{source.RunId}[/]{(stage3MinVotes is { } m ? $" (Stage 3 min votes {m})" : "")} → new run [bold]{run.RunId}[/]");
            return run;
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or FileNotFoundException or InvalidOperationException or ArgumentOutOfRangeException)
        {
            IndexingShared.Fail(ex.Message);
            return null;
        }
    }

    /// <summary>
    /// A new run in <paramref name="runsRoot"/> with <paramref name="source"/>'s stage-1.json … stage-N.json
    /// (N = <paramref name="throughStage"/>, 2 or 3; stage 3 re-filtered to <paramref name="stage3MinVotes"/> when given),
    /// the failure lines of those stages, and its run.json plan fields (book, chapters, text hash, git, model, settings, and
    /// the skills and sampling of those stages) plus <c>frozenFrom</c>. No LLM calls.
    /// </summary>
    public static RunFolder Copy(RunFolder source, string runsRoot, int throughStage, int? stage3MinVotes = null)
    {
        if (throughStage is not (2 or 3)) throw new ArgumentOutOfRangeException(nameof(throughStage));
        string[] files = throughStage == 2 ? [RunFolder.Stage1Json, RunFolder.Stage2Json] : [RunFolder.Stage1Json, RunFolder.Stage2Json, RunFolder.Stage3Json];
        foreach (var f in files)
            if (!File.Exists(source.File(f))) throw new FileNotFoundException($"{source.RunId} has no {f}.");
        if (stage3MinVotes is not null && throughStage != 3) throw new InvalidOperationException("Stage 3 min votes need a Stage 3 copy.");
        Stage3Result? refiltered = stage3MinVotes is { } m ? Stage3.WithMinVotes(IndexingShared.Read<Stage3Result>(source, RunFolder.Stage3Json), m) : null;

        var run = RunFolder.Create(runsRoot);
        foreach (var f in files)
        {
            if (f == RunFolder.Stage3Json && refiltered is not null)
            {
                var old = JsonNode.Parse(File.ReadAllText(source.File(f)))!;
                IndexingShared.WriteWith(run, f, refiltered, new Dictionary<string, object?>
                {
                    ["stage"] = Stage3.StageName, ["skill"] = old["skill"]?.DeepClone(), ["counts"] = Stage3.Counts(refiltered),
                    ["stage3From"] = new { runId = source.RunId, minVotes = stage3MinVotes },
                });
            }
            else File.Copy(source.File(f), run.File(f));
        }
        var stages = Enumerable.Range(1, throughStage).Select(i => $"stage-{i}").ToHashSet();
        if (File.Exists(source.File(RunFolder.FailuresJsonl)))
            foreach (var line in File.ReadAllLines(source.File(RunFolder.FailuresJsonl)).Where(l => l.Length > 0).Select(l => JsonNode.Parse(l)!))
                if (line["stage"]?.GetValue<string>() is { } st && stages.Contains(st)) run.AppendFailure(line);

        var sourceRun = JsonNode.Parse(File.ReadAllText(source.File(RunFolder.RunJson)))!.AsObject();
        var fields = new Dictionary<string, object?>();
        foreach (var key in new[] { "book", "chapters", "textSha256", "git", "model", "settings", "stage1From" })
            if (sourceRun[key] is { } v) fields[key] = v.DeepClone();
        if (sourceRun["skills"] is JsonObject skills)
            fields["skills"] = new JsonObject(skills.Where(kv => SkillStage.TryGetValue(kv.Key, out var st) && st <= throughStage).Select(kv => KeyValuePair.Create(kv.Key, kv.Value?.DeepClone())));
        if (sourceRun["sampling"] is JsonObject sampling)
            fields["sampling"] = new JsonObject(sampling.Where(kv => stages.Contains(kv.Key)).Select(kv => KeyValuePair.Create(kv.Key, kv.Value?.DeepClone())));
        fields["frozenFrom"] = new JsonObject { ["runId"] = source.RunId, ["throughStage"] = throughStage, ["stage3MinVotes"] = stage3MinVotes };
        var option = throughStage == 2 ? $"index facts --stage2-from {source.RunId}" : $"index time --stage3-from {source.RunId}{(stage3MinVotes is { } x ? $" --stage3-min-votes {x}" : "")}";
        run.RecordCommand(new { command = option, startedAt = DateTimeOffset.UtcNow }, fields);
        return run;
    }
}

/// <summary>Writes a run's entities, edges, Fact/Event nodes, positions and time fields into a new book database.</summary>
public static class ScratchDb
{
    private static readonly JsonSerializerOptions Compact = new(RunFolder.Json) { WriteIndented = false };

    /// <summary>
    /// <list type="bullet">
    /// <item><c>chapters</c> / <c>positions</c>: the whole book, from Stage 0 (as <c>index prepare</c>).</item>
    /// <item>Each Stage 2 entity: a node labelled Character or Location (properties: name, aliases, stage2Id, and
    /// aliasFirstPos = each alias → the char offset of its first mention; evidence = its first mention) and a <c>characters</c> / <c>locations</c> row pointing at it.
    /// A Character also gets shortName (<see cref="ShortNames.Pick"/>: the name the book uses most), and its row short_name and
    /// initials (of the short name).</item>
    /// <item>Each Stage 3 Fact/Event: a node (properties: text, stage3Id, chapter, and its story time; evidence = its quote).
    /// Its links become edges: STATES and PARTICIPATED_IN from the entity to the node, ABOUT and LOCATED_AT from the node
    /// to the entity.</item>
    /// <item>Each Stage 3 entity edge: an edge (properties: stage3Id, kind for RELATED_TO).</item>
    /// <item>revealed_pos = the evidence start on every edge. Story columns and fabula_seq: the item's Stage 4 values; a
    /// node's link edges carry the node's values, except STATES (the claim's time is not when it was said).</item>
    /// <item><c>event_order</c>: a Stage 4 ordering between two items, each item standing for its own edge (an entity
    /// edge) or its first non-STATES link edge (a node; else its STATES edge).</item>
    /// </list>
    /// </summary>
    public static Dictionary<string, int> Build(string path, Stage0Result stage0, Stage2Result stage2, Stage3Result stage3, Stage4Result? stage4)
    {
        BookDatabase.Create(path);
        using var db = BookDatabase.OpenReadWrite(path);
        db.Chapters.AddRange(stage0.Chapters.Select(c => new Chapter { Number = c.Number, Title = c.Title, CharStart = c.CharStart, CharEnd = c.CharEnd }));
        db.Positions.AddRange(stage0.Chapters.SelectMany(c => c.Paragraphs).Select(p => new Position { Chapter = p.Chapter, Paragraph = p.Paragraph, CharStart = p.CharStart, CharEnd = p.CharEnd }));

        string Json(object o) => JsonSerializer.Serialize(o, Compact);
        var entityNodes = new Dictionary<int, Node>();
        var owners = ShortNames.Owners(stage2.Entities.Where(e => e.Type == MentionType.Character));
        foreach (var e in stage2.Entities)
        {
            var label = e.Type == MentionType.Character ? Ontology.Character : Ontology.Location;
            var first = e.Mentions.OrderBy(m => m.CharStart).First();
            var aliasFirstPos = e.Mentions.GroupBy(m => m.Text).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Min(m => m.CharStart));
            var shortName = e.Type == MentionType.Character ? ShortNames.Pick(e, owners) : null;
            var node = new Node
            {
                Properties = shortName is null
                    ? Json(new { name = e.CanonicalName, aliases = e.Aliases, stage2Id = e.Id, aliasFirstPos })
                    : Json(new { name = e.CanonicalName, shortName, aliases = e.Aliases, stage2Id = e.Id, aliasFirstPos }),
                EvidenceStart = first.CharStart,
                EvidenceEnd = first.CharEnd,
                Labels = [new NodeLabel { Label = label }],
            };
            entityNodes[e.Id] = node;
            db.Nodes.Add(node);
            if (e.Type == MentionType.Character)
                db.Characters.Add(new Character { Name = e.CanonicalName, ShortName = shortName, Initials = TemporalGraph.Chat.ChatCharacters.DeriveInitials(shortName!), Node = node });
            else db.Locations.Add(new Location { Name = e.CanonicalName, Node = node });
        }

        var times = (stage4?.Items ?? []).ToDictionary(i => i.Id);
        void Time(Edge edge, string itemId)
        {
            if (!times.TryGetValue(itemId, out var t)) return;
            edge.StoryFromEdtf = t.StoryFrom?.Edtf;
            edge.StoryFromLo = t.StoryFrom?.Lo;
            edge.StoryFromHi = t.StoryFrom?.Hi;
            edge.StoryUntilEdtf = t.StoryUntil?.Edtf;
            edge.StoryUntilLo = t.StoryUntil?.Lo;
            edge.StoryUntilHi = t.StoryUntil?.Hi;
            edge.FabulaSeq = t.FabulaSeq;
        }

        var anchors = new Dictionary<string, Edge>();
        foreach (var e in stage3.Edges)
        {
            if (!entityNodes.TryGetValue(e.Source, out var s) || !entityNodes.TryGetValue(e.Target, out var t)) continue;
            var edge = new Edge
            {
                Source = s, Target = t, Type = e.Type,
                Properties = Json(new { stage3Id = e.Id, kind = e.Kind }),
                RevealedPos = e.EvidenceMatch.CharStart,
                EvidenceStart = e.EvidenceMatch.CharStart,
                EvidenceEnd = e.EvidenceMatch.CharEnd,
            };
            Time(edge, e.Id);
            db.Edges.Add(edge);
            anchors[e.Id] = edge;
        }

        foreach (var n in stage3.Nodes)
        {
            times.TryGetValue(n.Id, out var t);
            var node = new Node
            {
                Properties = Json(new
                {
                    text = n.Text, stage3Id = n.Id, chapter = n.Chapter,
                    storyFromEdtf = t?.StoryFrom?.Edtf, storyFromLo = t?.StoryFrom?.Lo, storyFromHi = t?.StoryFrom?.Hi,
                    storyUntilEdtf = t?.StoryUntil?.Edtf, storyUntilLo = t?.StoryUntil?.Lo, storyUntilHi = t?.StoryUntil?.Hi,
                    fabulaSeq = t?.FabulaSeq, reasoning = t?.Reasoning, alsoEvidence = n.AlsoEvidence,
                }),
                EvidenceStart = n.EvidenceMatch.CharStart,
                EvidenceEnd = n.EvidenceMatch.CharEnd,
                Labels = [new NodeLabel { Label = n.Label }],
            };
            db.Nodes.Add(node);
            var linkEdges = new List<Edge>();
            foreach (var l in n.Links)
            {
                if (!entityNodes.TryGetValue(l.Entity, out var entity)) continue;
                var fromEntity = l.Type is Ontology.States or Ontology.ParticipatedIn;
                var edge = new Edge
                {
                    Source = fromEntity ? entity : node, Target = fromEntity ? node : entity, Type = l.Type,
                    Properties = Json(new { stage3Id = n.Id }),
                    RevealedPos = n.EvidenceMatch.CharStart,
                    EvidenceStart = n.EvidenceMatch.CharStart,
                    EvidenceEnd = n.EvidenceMatch.CharEnd,
                };
                if (l.Type != Ontology.States) Time(edge, n.Id);
                db.Edges.Add(edge);
                linkEdges.Add(edge);
            }
            if ((linkEdges.FirstOrDefault(x => x.Type != Ontology.States) ?? linkEdges.FirstOrDefault()) is { } anchor) anchors[n.Id] = anchor;
        }

        foreach (var o in stage4?.Order ?? [])
            if (anchors.TryGetValue(o.A, out var a) && anchors.TryGetValue(o.B, out var b))
                db.EventOrder.Add(new EventOrder { AEdge = a, BEdge = b, Relation = o.Relation });

        db.SaveChanges();
        return new Dictionary<string, int>
        {
            ["chapters"] = db.Chapters.Count(),
            ["positions"] = db.Positions.Count(),
            ["nodes"] = db.Nodes.Count(),
            ["node_labels"] = db.NodeLabels.Count(),
            ["edges"] = db.Edges.Count(),
            ["event_order"] = db.EventOrder.Count(),
            ["characters"] = db.Characters.Count(),
            ["locations"] = db.Locations.Count(),
        };
    }
}

/// <summary>
/// <c>gold facts-review --run &lt;runId&gt; [--run …]</c>: writes gold/&lt;book&gt;/&lt;chapters&gt;.facts.review.md, the draft facts review
/// from the union of the runs' Stage 3/4 output.
/// </summary>
public sealed class GoldFactsReviewCommand : Command<GoldFactsReviewCommand.Settings>
{
    public sealed class Settings : GlobalSettings
    {
        [CommandOption("--run <RUN>")][Description("A run id; repeat to draft from the union of several runs.")] public string[] Run { get; init; } = [];
        [CommandOption("--out <FILE>")][Description("Default: gold/<book>/chNN-NN.facts.review.md")] public string? Out { get; init; }
        [CommandOption("--force")][Description("Overwrite a review file that already has review marks.")] public bool Force { get; init; }
        [CommandOption("--min-runs <N>")][Description("List only items found by at least N of the runs (default 1).")] public int MinRuns { get; init; } = 1;
        [CommandOption("--rest-out <FILE>")][Description("With --min-runs: write the left-out items here (a scratch file; don't commit it).")] public string? RestOut { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings, CancellationToken ct)
    {
        if (settings.Run.Length == 0) return IndexingShared.Fail("Give at least one --run.");
        var root = RepoPaths.FindRoot();
        var sources = new List<FactsReviewSource>();
        var chapters = new List<(int, int)>();
        foreach (var id in settings.Run)
        {
            RunFolder run;
            try { run = RunFolder.Open(RepoPaths.Runs(root), id); }
            catch (DirectoryNotFoundException ex) { return IndexingShared.Fail(ex.Message); }
            if (!File.Exists(run.File(RunFolder.Stage3Json))) return IndexingShared.Fail($"{run.RunId} has no stage-3.json.");
            var stage4 = File.Exists(run.File(RunFolder.Stage4Json)) ? IndexingShared.Read<Stage4Result>(run, RunFolder.Stage4Json) : null;
            sources.Add(new FactsReviewSource(run.RunId, Stage2File.Load(run), IndexingShared.Read<Stage3Result>(run, RunFolder.Stage3Json), stage4));
            chapters.Add(IndexCrosscheckCommand.RunChapters(run));
        }
        if (sources.Select((s, i) => (s.Stage3.BookId, chapters[i])).Distinct().Count() > 1) return IndexingShared.Fail("All runs must be of the same book and chapters.");
        if (sources.Select(s => s.RunId).Distinct().Count() < sources.Count) return IndexingShared.Fail("A run is given twice.");
        if (settings.MinRuns < 1 || settings.MinRuns > sources.Count) return IndexingShared.Fail($"--min-runs must be between 1 and the number of runs ({sources.Count}).");
        if (settings.RestOut is not null && settings.MinRuns == 1) return IndexingShared.Fail("--rest-out needs --min-runs 2 or more.");

        var bookId = sources[0].Stage3.BookId;
        var (first, last) = chapters[0];
        var title = BookManifest.Read(Path.Combine(RepoPaths.Books(root), BookManifest.FileName)).First(b => b.Id == bookId).Title;
        var stage0 = IndexingShared.Stage0For(bookId);
        var chapterLabel = first == last ? $"{first}" : $"{first}–{last}";
        var content = FactsReviewFile.Generate(title, bookId, chapterLabel, sources, IndexingShared.BookText(bookId), IndexingShared.ChapterOf(stage0), settings.MinRuns);
        var path = settings.Out ?? Path.Combine(root, "gold", bookId, IndexingShared.ChapterSlug(first, last) + ".facts.review.md");
        if (File.Exists(path) && !settings.Force && System.Text.RegularExpressions.Regex.IsMatch(File.ReadAllText(path), @"^- \[(x|~|-)\] ", System.Text.RegularExpressions.RegexOptions.Multiline))
            return IndexingShared.Fail($"{Path.GetRelativePath(root, path)} already has review marks; not overwriting it. Use --out <other file>, or --force.");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, content);
        if (settings.RestOut is { } restPath)
        {
            var restContent = FactsReviewFile.Generate(title, bookId, chapterLabel, sources, IndexingShared.BookText(bookId), IndexingShared.ChapterOf(stage0), settings.MinRuns, rest: true);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(restPath))!);
            File.WriteAllText(restPath, restContent);
            AnsiConsole.MarkupLine($"Left-out items → [bold]{Markup.Escape(restPath)}[/]: " + string.Join(", ", FactsReviewFile.LineCounts(restContent).Select(kv => $"{kv.Value} {kv.Key}")));
        }
        var sizes = FactsReviewFile.LineCounts(content);
        AnsiConsole.MarkupLine($"Wrote [bold]{Markup.Escape(Path.GetRelativePath(root, path))}[/] from {sources.Count} run(s): " +
            string.Join(", ", sizes.Select(kv => $"{kv.Value} {kv.Key}")));
        return 0;
    }
}
