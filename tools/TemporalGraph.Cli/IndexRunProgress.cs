using System.Globalization;
using System.Text.Json.Nodes;
using Spectre.Console;
using Spectre.Console.Rendering;
using TemporalGraph.Cli.Books;
using TemporalGraph.Cli.Runs;

namespace TemporalGraph.Cli;

/// <summary>
/// What <c>index run</c> planned, written into run.json's <c>plan</c> at the start so that another process
/// (<c>index watch</c>, <c>index status</c>) can say how far the run is: the stages and chapters, the chunks, the model calls planned
/// per stage (chunks × samples; Stage 5: chapters × samples; Stage 2's pairs are not known ahead) and the token estimate per stage.
/// </summary>
public sealed record RunPlanInfo(int FirstStage, int LastStage, int FirstChapter, int LastChapter, int Chunks, int FirstChunk,
    IReadOnlyDictionary<string, long> PlannedCalls, IReadOnlyDictionary<string, long> EstimatedTokens)
{
    public int Chapters => LastChapter - FirstChapter + 1;

    public static RunPlanInfo? Read(JsonNode? runJson)
    {
        try
        {
            if (runJson?["plan"] is not JsonObject plan) return null;
            static IReadOnlyDictionary<string, long> Map(JsonNode? n) => (n as JsonObject)?.ToDictionary(kv => kv.Key, kv => kv.Value!.GetValue<long>()) ?? [];
            return new RunPlanInfo(plan["firstStage"]!.GetValue<int>(), plan["lastStage"]!.GetValue<int>(), plan["firstChapter"]!.GetValue<int>(), plan["lastChapter"]!.GetValue<int>(),
                plan["chunks"]!.GetValue<int>(), plan["firstChunk"]!.GetValue<int>(), Map(plan["plannedCalls"]), Map(plan["estimatedTokens"]));
        }
        catch (Exception ex) when (ex is InvalidOperationException or NullReferenceException or FormatException) { return null; }
    }
}

/// <summary>The words for where a run is: the footer under the bars, the "Stopped at…" line, the line every 60 s, and the time left.</summary>
public static class RunProgress
{
    /// <summary>"chunk 61/142" for a stage per chunk, "chapter 7/15" for Stage 5, "17 calls" for Stage 2; "" before the first call.</summary>
    public static string Unit(int stage, RunPlanInfo plan, StageProgress? s)
    {
        if (s is null || s.Calls == 0) return stage == 1 ? $"chunk 0/{plan.Chunks}" : stage is 4 or 5 ? $"chapter 0/{plan.Chapters}" : "";
        if (stage == 1 && s.ChunkIndex is { } chunk) return $"chunk {Math.Clamp(chunk - plan.FirstChunk + 1, 1, Math.Max(1, plan.Chunks))}/{plan.Chunks}";
        if (stage is 4 or 5 && s.Chapter is { } chapter) return $"chapter {Math.Clamp(chapter - plan.FirstChapter + 1, 1, Math.Max(1, plan.Chapters))}/{plan.Chapters}";
        return $"{s.Calls:N0} call{(s.Calls == 1 ? "" : "s")}";
    }

    /// <summary>"Stopped at stage 3, chunk 61/142.".</summary>
    public static string StoppedAt(int stage, RunPlanInfo plan, StageProgress? s) =>
        stage > 5 ? "Stopped at build-db." : Unit(stage, plan, s) is { Length: > 0 } unit ? $"Stopped at stage {stage}, {unit}." : $"Stopped at stage {stage}.";

    /// <summary>"chapter 7/15 · chunk 61/142 · last call 3.2 s", as far as the stage's calls say.</summary>
    public static string Where(int stage, RunPlanInfo plan, StageProgress? s)
    {
        var parts = new List<string>();
        if (s?.Chapter is { } chapter) parts.Add($"chapter {Math.Clamp(chapter - plan.FirstChapter + 1, 1, Math.Max(1, plan.Chapters))}/{plan.Chapters}");
        if (stage == 1 && s?.ChunkIndex is { } chunk) parts.Add($"chunk {Math.Clamp(chunk - plan.FirstChunk + 1, 1, Math.Max(1, plan.Chunks))}/{plan.Chunks}");
        if (s?.LastLatencyMs is { } ms) parts.Add($"last call {(ms / 1000).ToString("0.0", CultureInfo.InvariantCulture)} s");
        return string.Join(" · ", parts);
    }

    /// <summary>"in flight 5/8 · cache hits 62 %": the model calls being answered now out of the limit, and the share of the
    /// stage's input so far that was read from the prompt cache. "" when no stage is asking the model.</summary>
    public static string Flight(Indexing.StageRunner? runner, StageProgress? s)
    {
        var parts = new List<string>();
        if (runner is not null) parts.Add($"in flight {runner.InFlight}/{runner.Limit}");
        if (s is { InputTokens: > 0 }) parts.Add($"cache hits {Math.Round(s.CacheShare * 100).ToString("0", CultureInfo.InvariantCulture)} %");
        return string.Join(" · ", parts);
    }

    /// <summary>
    /// The calls a stage is planned to make, as shown beside the calls made ("34/36"): the plan's count (Stage 1: chunks ×
    /// (1 + glean)), raised to the calls already scheduled when a stage asks more than planned (a judge call, a chunk asked again in
    /// halves). So the count never passes its total and the bar never passes 100 %. 0: not known ahead (Stage 2, build-db).
    /// </summary>
    /// <param name="runner">The stage's gate in this process (it counts a call when it is scheduled); null from another process, where
    /// the calls that have landed raise the total.</param>
    public static long Planned(RunPlanInfo plan, int stage, StageProgress? s, Indexing.StageRunner? runner = null)
    {
        var planned = plan.PlannedCalls.GetValueOrDefault(IndexPlan.StageKey(stage));
        return planned <= 0 ? 0 : Math.Max(planned, Math.Max(s?.Calls ?? 0, runner?.Calls ?? 0));
    }

    /// <summary>"stage 3: 48/142 calls · 310k/41k tokens": the line a run without a terminal writes every 60 s.</summary>
    public static string Line(int stage, RunPlanInfo plan, StageProgress? s)
    {
        var calls = s?.Calls ?? 0;
        var planned = Planned(plan, stage, s, Indexing.StageRunner.Current);
        return $"stage {stage}: {(planned > 0 ? $"{calls}/{planned}" : calls.ToString(CultureInfo.InvariantCulture))} calls · {RunMonitor.Short(s?.InputTokens ?? 0)}/{RunMonitor.Short(s?.OutputTokens ?? 0)} tokens";
    }

    /// <summary>
    /// The time the run still needs: the current stage's calls left at the pace of its calls so far, plus the stages not started, their
    /// token estimate at the tokens per second the run has made so far. Null before the first call has been answered.
    /// </summary>
    public static TimeSpan? Remaining(RunPlanInfo plan, RunMonitor monitor, int currentStage, DateTimeOffset now)
    {
        var tokens = monitor.TotalInputTokens + monitor.TotalOutputTokens;
        var first = monitor.Stages.Values.Where(s => s.FirstCall is not null).Select(s => s.FirstCall!.Value).DefaultIfEmpty(now).Min();
        var seconds = (now - first).TotalSeconds;
        if (tokens <= 0 || seconds <= 0) return null;
        var rate = tokens / seconds;

        double left = 0;
        for (var stage = Math.Max(1, currentStage); stage <= plan.LastStage; stage++)
        {
            var key = IndexPlan.StageKey(stage);
            var done = monitor.Stages.GetValueOrDefault(key);
            var estimate = plan.EstimatedTokens.GetValueOrDefault(key);
            if (stage > currentStage || done is null || done.Calls == 0)
            {
                left += estimate / rate;
                continue;
            }
            var planned = plan.PlannedCalls.GetValueOrDefault(key);
            var stageSeconds = done.FirstCall is { } began ? (now - began).TotalSeconds : 0;
            left += planned > 0 && stageSeconds > 0
                ? stageSeconds / done.Calls * Math.Max(0, planned - done.Calls)
                : Math.Max(0, estimate - done.InputTokens - done.OutputTokens) / rate;
        }
        return TimeSpan.FromSeconds(left);
    }

    /// <summary>"run ETA 12 min": an estimate, from the calls so far and the per-stage estimate.</summary>
    public static string EtaText(TimeSpan? left) => left is { } t ? $"run ETA {RunMonitor.Span(t)}" : "run ETA not known yet";

    /// <summary>A bar of <paramref name="width"/> cells for a share between 0 and 1, as markup.</summary>
    public static string Bar(double share, int width = 40)
    {
        var filled = (int)Math.Round(Math.Clamp(share, 0, 1) * width);
        return $"[green]{new string('━', filled)}[/][grey]{new string('━', width - filled)}[/]";
    }
}

/// <summary>
/// A run as <c>index status --run</c> and <c>index watch --run</c> show it, read from the run folder's files alone
/// (run.json, cost.json, calls.jsonl), so it works from another process while the run is going.
/// </summary>
public sealed class RunView
{
    private readonly string _root;
    private readonly RunMonitor _monitor;

    public string RunId { get; }
    public string Dir { get; }

    public RunView(string root, string runId)
    {
        (_root, RunId) = (root, runId);
        Dir = Path.Combine(RepoPaths.Runs(root), runId);
        _monitor = new RunMonitor(Dir);
    }

    public sealed record State(string RunId, string? Book, IReadOnlyList<StageRow> Stages, StageRow Total, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt,
        double? ElapsedSeconds, long CallsSoFar, string? CurrentStage, string? CurrentChunk, double? Share, double? RemainingSeconds, bool Cancelled, bool CancelRequested,
        bool? LeakTestsPassed, int? Exit, bool Running)
    {
        /// <summary>Not being worked on by an <c>index run</c>: it ended, or it was not made by <c>index run</c> (nothing to wait for).</summary>
        public bool Finished => !Running;
    }

    /// <summary>Reads what is new in the run's files and returns where the run is.</summary>
    public State Read()
    {
        _monitor.Poll();
        JsonNode? runJson = null;
        try { if (File.Exists(Path.Combine(Dir, RunFolder.RunJson))) runJson = JsonNode.Parse(RunFolder.ReadShared(Path.Combine(Dir, RunFolder.RunJson))); }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException) { }   // being written: the next read has it
        var plan = RunPlanInfo.Read(runJson);
        var now = DateTimeOffset.UtcNow;
        static DateTimeOffset? Time(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var text) && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t) ? t : null;
        var commands = runJson?["commands"] as JsonArray;
        var started = Time(runJson?["startedAt"]) ?? Time(commands?.FirstOrDefault()?["startedAt"]);
        // `index run` says "running" from its first line to its last; a run folder without that word is not being worked on.
        var running = runJson?["state"] is JsonValue stateValue && stateValue.TryGetValue<string>(out var stateText) && stateText == "running";
        // A run that index run did not make has no finishedAt of its own: its last command's is the end.
        var finished = running ? null : Time(runJson?["finishedAt"]) ?? Time(commands?.LastOrDefault()?["finishedAt"]) ?? Time(commands?.LastOrDefault()?["startedAt"]);
        var stageNumber = runJson?["currentStage"] is JsonValue cs && cs.TryGetValue<int>(out var n) ? n : (int?)null;

        // Finished stages from cost.json; the stage in progress (and any stage cost.json lacks) from the calls logged so far.
        var rows = IndexPlan.Stages(Dir).Select(r => r.Stage == "total" ? r with { Stage = "(all stages)" } : r).ToList();
        foreach (var live in _monitor.Stages.Values.Where(s => s.Stage.Length > 0 && rows.All(r => r.Stage != s.Stage)))
            rows.Add(new StageRow(live.Stage, live.Calls, live.InputTokens - live.CacheReadTokens - live.CacheWriteTokens, live.OutputTokens, null, CacheReadTokens: live.CacheReadTokens, CacheWriteTokens: live.CacheWriteTokens));

        string? currentStage = null, currentChunk = null;
        double? share = null, remaining = null;
        if (running && stageNumber is { } stage)
        {
            currentStage = IndexPlan.StageLabel(stage);
            var progress = _monitor.Stages.GetValueOrDefault(IndexPlan.StageKey(stage));
            if (plan is not null)
            {
                currentChunk = RunProgress.Unit(stage, plan, progress) is { Length: > 0 } unit ? unit : null;
                var planned = RunProgress.Planned(plan, stage, progress);
                if (planned > 0) share = Math.Clamp((progress?.Calls ?? 0) / (double)planned, 0, 1);
                remaining = RunProgress.Remaining(plan, _monitor, stage, now)?.TotalSeconds;
            }
        }
        var run = RunFolder.Open(RepoPaths.Runs(_root), RunId);
        return new State(RunId, BookShelf.RunBook(Dir), rows, IndexPlan.Total(rows), started, finished,
            started is { } s && (running || finished is not null) ? Math.Round(((finished ?? now) - s).TotalSeconds, 1) : null, Math.Max(_monitor.TotalCalls, rows.Sum(r => r.Calls)),
            currentStage, currentChunk, share, remaining is { } left ? Math.Round(left, 0) : null,
            runJson?["cancelled"] is JsonValue c && c.TryGetValue<bool>(out var was) && was, File.Exists(run.File(IndexRunner.CancelFile)), IndexRunner.LeakTests(run),
            runJson?["exit"] is JsonValue e && e.TryGetValue<int>(out var code) ? code : null, running);
    }

    /// <summary>The stage table of <c>index status --run</c>, then the line of where the run is and, while it runs, the current stage's bar and the time left.</summary>
    public static IRenderable Render(State state, bool watching)
    {
        var table = new Table().Border(TableBorder.Rounded).Title(Markup.Escape($"Run {state.RunId}")).AddColumn("Stage").AddColumn(new TableColumn("Calls").RightAligned())
            .AddColumn(new TableColumn("Tokens in").RightAligned()).AddColumn(new TableColumn("Cache read").RightAligned()).AddColumn(new TableColumn("Tokens out").RightAligned()).AddColumn(new TableColumn("Cost").RightAligned());
        foreach (var r in state.Stages.Append(state.Total))
            table.AddRow(Markup.Escape(r.Stage), r.Calls.ToString("N0"), r.AllInputTokens.ToString("N0"), r.CacheReadTokens.ToString("N0"), r.OutputTokens.ToString("N0"), r.Cost is { } c ? c.ToString("0.00") : "not known");
        var lines = new List<IRenderable> { table, new Markup(Summary(state)) };
        if (!state.Finished && state.CurrentStage is not null)
        {
            lines.Add(new Markup($"{Markup.Escape(state.CurrentStage)} {(state.Share is { } share ? $"{RunProgress.Bar(share)} {share * 100:0}%" : "[grey](calls not known ahead)[/]")}"));
            lines.Add(new Markup(Markup.Escape(RunProgress.EtaText(Left(state)))));
        }
        if (watching) lines.Add(new Markup(state.Finished ? "[grey]The run has finished.[/]" : $"[grey]{Markup.Escape(IndexRunner.WatchingNotice)}[/]"));
        return new Rows(lines);
    }

    private static TimeSpan? Left(State state) => state.RemainingSeconds is { } s ? TimeSpan.FromSeconds(s) : null;

    /// <summary>The last lines of the run's run.log (ten by default), oldest first.</summary>
    public IReadOnlyList<string> LogTail(int lines = 10)
    {
        var path = Path.Combine(Dir, RunLog.FileName);
        try { return File.Exists(path) ? RunFolder.ReadShared(path).Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(lines).Select(l => l.TrimEnd('\r')).ToList() : []; }
        catch (IOException) { return []; }
    }

    /// <summary>What <c>index watch</c> shows at a terminal: the progress above and, below it, the last lines of run.log.</summary>
    public static IRenderable Watch(State state, IReadOnlyList<string> log)
    {
        var tail = new Panel(log.Count == 0 ? new Markup("[grey](nothing logged yet)[/]") : new Rows(log.Select(l => (IRenderable)new Text(l))))
            .Header(RunLog.FileName).Border(BoxBorder.Rounded).Expand();
        return new Layout("watch").SplitRows(new Layout("progress", Render(state, watching: true)), new Layout("log", tail).Size(12));
    }

    /// <summary>"Book … · running, stage 1 extract, chunk 61/142 · 1,204 calls so far · elapsed 12 min 4 s".</summary>
    /// <param name="plain">Without markup.</param>
    public static string Summary(State state, bool plain) => plain ? Markup.Remove(Summary(state)) : Summary(state);

    public static string Summary(State state)
    {
        var what = state.Finished ? state.Cancelled ? "[yellow]cancelled[/]" : state.Exit is > 0 ? $"[red]stopped with exit {state.Exit}[/]" : "finished"
            : state.CurrentStage is null ? "running" : $"running, stage {Markup.Escape(state.CurrentStage)}{(state.CurrentChunk is { } chunk ? ", " + Markup.Escape(chunk) : "")}";
        return $"Book {Markup.Escape(state.Book ?? "(not recorded)")} · {what} · {state.CallsSoFar:N0} call{(state.CallsSoFar == 1 ? "" : "s")} so far"
            + (state.ElapsedSeconds is { } elapsed ? $" · elapsed {RunMonitor.Span(TimeSpan.FromSeconds(elapsed))}" : "")
            + (state.CancelRequested && !state.Finished ? " · [yellow]stop requested[/]" : "");
    }
}
