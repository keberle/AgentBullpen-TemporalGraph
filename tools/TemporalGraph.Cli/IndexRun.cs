using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Spectre.Console;
using Spectre.Console.Cli;
using TemporalGraph.Cli.Books;
using TemporalGraph.Cli.Indexing;
using TemporalGraph.Cli.Runs;
using TemporalGraph.Data;
using TemporalGraph.Llm;
using TemporalGraph.Query;

namespace TemporalGraph.Cli;

/// <param name="Stages">"0-5", "2..5" or "3"; null: 0-5.</param>
/// <param name="FromRun">A run whose earlier stage outputs are copied into the new run (with <paramref name="Stages"/> starting at 2 or later).</param>
/// <param name="Yes">Don't ask before the first model call (only a terminal is ever asked).</param>
/// <param name="PromoteByRule"><c>index rebuild</c>: promote when <see cref="PromoteRule.Decide"/> says yes, without asking.</param>
public sealed record IndexRunOptions(string? Book, string? Chapters = null, string? Stages = null, string? FromRun = null, string? Llm = null,
    decimal? BudgetUsd = null, bool Yes = false, bool Promote = false, long? BudgetTokens = null, bool Confirmed = false, bool DryRun = false,
    StageOverrides? Overrides = null, bool PromoteByRule = false);

/// <summary><c>index rebuild</c>. <paramref name="Books"/> empty: every book of the manifest, in its order.</summary>
public sealed record RebuildOptions(IReadOnlyList<string> Books, string? Llm = null, decimal? BudgetUsd = null, long? BudgetTokens = null, bool DryRun = false, bool Yes = false,
    StageOverrides? Overrides = null);

/// <summary>One book of a rebuild: its estimate from the pre-flight, then what its run did (null fields until it has run).</summary>
public sealed record RebuildRow(string Book, int Chapters, int Chunks, long EstimatedInputTokens, long EstimatedOutputTokens, decimal? EstimatedCost,
    string? RunId = null, int? Exit = null, bool? Promoted = null, int? Nodes = null, int? Edges = null, long? InputTokens = null, long? OutputTokens = null, decimal? Cost = null);

/// <summary>One stage of a run, as the counts table and <c>index status</c> show it.</summary>
/// <param name="InputTokens">Input billed at the input price (with a prompt cache: what was neither read from it nor written to it).</param>
/// <param name="CacheReadTokens">Input read from the prompt cache.</param>
/// <param name="CacheWriteTokens">Input written to the prompt cache.</param>
public sealed record StageRow(string Stage, long Calls, long InputTokens, long OutputTokens, decimal? Cost, double? Seconds = null, int? Exit = null,
    long CacheReadTokens = 0, long CacheWriteTokens = 0)
{
    /// <summary>Every input token the stage sent, whatever it was billed at.</summary>
    public long AllInputTokens => InputTokens + CacheReadTokens + CacheWriteTokens;
}

/// <summary>The cost estimate shown before the first model call. <see cref="Cost"/> is null when no price is configured.</summary>
public sealed record CostEstimate(int Chunks, long InputTokens, long OutputTokens, decimal? Cost, string? Currency, string Basis);

public static class IndexPlan
{
    /// <summary>1 extract (was mentions), 3 link (was facts; code only now).</summary>
    public static readonly IReadOnlyList<string> StageNames = ["prepare", "extract", "resolve", "link", "time", "perceive"];

    /// <summary>"0-5", "2..5" or "3" → the stages, within 0..5; null when it can't be read.</summary>
    public static (int First, int Last)? ParseStages(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec)) return (0, 5);
        var parts = spec.Split(["..", "-"], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is not (1 or 2) || !parts.All(p => int.TryParse(p, out _))) return null;
        var (a, b) = (int.Parse(parts[0]), int.Parse(parts[^1]));
        return a is >= 0 and <= 5 && b is >= 0 and <= 5 && a <= b ? (a, b) : null;
    }

    /// <summary>
    /// Tokens per chunk and stage (in, out): the averages over the three whole-book runs of the one-pass pipeline with Claude Haiku
    /// 5.5: <i>The Hound of the Baskervilles</i> (run 20261011T084201Z-39620c, 16 chunks), <i>Treasure Island</i>
    /// (20261011T090427Z-529463, 34) and <i>The Three Musketeers</i> (20261011T093011Z-6fd5d7, 73): 123 chunks, 10,614,968 tokens in
    /// and 4,932,241 out. All three ran with glean, before Stage 1's reply was made smaller, so Stage 1 is on the high side.
    /// Input is every input token sent (billed as input, read from the prompt cache or written to it); output includes the model's
    /// thinking. Stage 2 asks per block of names and Stages 4 and 5 per chapter; their totals are spread over the chunks.
    /// </summary>
    public static readonly IReadOnlyDictionary<int, (long In, long Out)> TokensPerChunk = new Dictionary<int, (long, long)>
    {
        [1] = (30_385, 27_734),
        [2] = (29_842, 4_672),
        [3] = (0, 0),
        [4] = (11_067, 5_768),
        [5] = (15_007, 1_925),
    };

    /// <summary>The books and chunks behind <see cref="TokensPerChunk"/>.</summary>
    public const int EstimateBooks = 3, EstimateChunks = 123;

    /// <summary>The basis in a few words, for the line a run starts with.</summary>
    public const string EstimateBasisShort = "Haiku 5.5 per-chunk average of 3 books, 123 chapter-sized chunks";

    public const string EstimateBasis = "an estimate: tokens per chapter-sized chunk, the average of 3 books indexed with Claude Haiku 5.5 (The Hound of the Baskervilles, Treasure Island and The Three Musketeers: 123 chunks; 86,301 in and 40,099 out each, cache reads counted as input), times this book's chunks";

    public static CostEstimate Estimate(int chunks, (int First, int Last) stages, Pricing pricing)
    {
        long tokensIn = 0, tokensOut = 0;
        for (var s = Math.Max(1, stages.First); s <= stages.Last; s++)
        {
            tokensIn += TokensPerChunk[s].In * chunks;
            tokensOut += TokensPerChunk[s].Out * chunks;
        }
        decimal? cost = pricing.Known ? Math.Round((tokensIn * pricing.InputPerMillionTokens!.Value + tokensOut * pricing.OutputPerMillionTokens!.Value) / 1_000_000m, 2) : null;
        return new CostEstimate(chunks, tokensIn, tokensOut, cost, pricing.Known ? pricing.Currency : null, EstimateBasis);
    }

    /// <summary>The name a stage's calls carry in calls.jsonl and cost.json: "stage-3".</summary>
    public static string StageKey(int stage) => $"stage-{stage}";

    /// <summary>"3 link"; 6 is build-db.</summary>
    public static string StageLabel(int stage) => stage is >= 0 and <= 5 ? $"{stage} {StageNames[stage]}" : "build-db";

    /// <summary>The samples each stage asks for per chunk (Stage 5: per chapter), from the <c>Indexing</c> section.</summary>
    public static IReadOnlyDictionary<int, int> Samples(IConfiguration config)
    {
        var indexing = IndexingShared.IndexingSettings.From(config);
        return new Dictionary<int, int>
        {
            // One sample everywhere but the block answers of Stage 2; Stage 3 is code.
            [1] = 1,
            [2] = config.GetValue("Indexing:StageB:Samples", BlockResolve.DefaultSamples),
            [3] = 0,
            [4] = 1,
            [5] = 1,
        };
    }

    /// <summary>A run's stages from cost.json (<c>stages</c> when it has them; else the one total).</summary>
    public static IReadOnlyList<StageRow> Stages(string runDir)
    {
        var path = Path.Combine(runDir, RunFolder.CostJson);
        if (!File.Exists(path)) return [];
        try
        {
            var json = JsonNode.Parse(RunFolder.ReadShared(path));
            StageRow Row(string name, JsonNode? n) => new(name, n?["calls"]?.GetValue<long>() ?? 0, n?["inputTokens"]?.GetValue<long>() ?? 0,
                n?["outputTokens"]?.GetValue<long>() ?? 0, BookShelf.Money(n?["cost"]), CacheReadTokens: n?["cacheReadTokens"]?.GetValue<long>() ?? 0, CacheWriteTokens: n?["cacheWriteTokens"]?.GetValue<long>() ?? 0);
            return json?["stages"] is JsonObject stages ? stages.Select(kv => Row(kv.Key, kv.Value)).ToList() : [Row("total", json)];
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or FormatException) { return []; }
    }

    public static StageRow Total(IReadOnlyList<StageRow> rows) =>
        new("total", rows.Sum(r => r.Calls), rows.Sum(r => r.InputTokens), rows.Sum(r => r.OutputTokens), rows.Any(r => r.Cost is null) ? null : rows.Sum(r => r.Cost!.Value),
            rows.Any(r => r.Seconds is not null) ? rows.Sum(r => r.Seconds ?? 0) : null, CacheReadTokens: rows.Sum(r => r.CacheReadTokens), CacheWriteTokens: rows.Sum(r => r.CacheWriteTokens));
}

/// <summary>
/// <c>index run</c>, <c>index status</c> and <c>index cancel</c>, apart from the console. <c>index run</c> calls the same stage
/// code as the stage commands (Stage 0, <see cref="IndexMentionsCommand.RunAsync"/>, <see cref="Pipeline"/>), in one run folder.
/// </summary>
/// Ctrl+C or <c>index cancel</c> stops a run after the model calls already sent (exit 130, run.json <c>cancelled</c>, the
/// command that continues it); run.json carries the plan and the stage reached, so <c>index watch</c> can follow it from another terminal.
/// <param name="runStage">Tests: runs a stage (1 extract … 5 perceive, 6 build-db) in place of the stage code and returns its exit code, so
/// what happens after the pre-flight can be checked without a model. Null: the stage code.</param>
/// <param name="stderr">Where the one-line notices go; null: <see cref="Console.Error"/>.</param>
/// <param name="poll">How often a run looks at its files (the cancel flag, calls.jsonl); null: once a second.</param>
/// <param name="heartbeat">Without a terminal, how often a line says where the run is; null: every 60 s.</param>
/// <param name="addBook">Tests: what the book picker's "+ Add a book…" entry runs; null: the intake at the console.</param>
public sealed class IndexRunner(string root, IAnsiConsole console, bool interactive, Func<IConfiguration> loadConfig,
    Func<int, RunFolder, CancellationToken, Task<int>>? runStage = null, TextWriter? stderr = null, TimeSpan? poll = null, TimeSpan? heartbeat = null,
    Func<CancellationToken, Task<string?>>? addBook = null)
{
    private TextWriter Stderr => stderr ?? CliContext.Stderr;
    private TimeSpan Poll => poll ?? TimeSpan.FromSeconds(1);
    private TimeSpan Heartbeat => heartbeat ?? TimeSpan.FromSeconds(60);

    /// <summary>
    /// The price the estimate uses: the profile's own (<c>Llm:Profiles:&lt;name&gt;:Pricing</c>, input and output per million tokens), else
    /// the <c>Pricing</c> section the run folders' cost.json uses.
    /// </summary>
    public static Pricing PricingFor(IConfiguration config, string? profile)
    {
        var own = Pricing.From(config.GetSection(profile is null ? LlmSettings.Section : $"{LlmSettings.Section}:Profiles:{profile}"));
        return own.Known ? own : Pricing.From(config);
    }

    public const string CancelFile = "cancel";

    public static IndexRunner ForConsole() => new(RepoPaths.FindRoot(), AnsiConsole.Console, CliContext.Interactive, () => LlmSettings.LoadConfiguration());

    private IReadOnlyList<BookEntry> Manifest()
    {
        var path = Path.Combine(RepoPaths.Books(root), BookManifest.FileName);
        return File.Exists(path) ? BookManifest.Read(path) : [];
    }

    // ── pre-flight ─────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <param name="Profile">The LLM profile the stages will use ("Llm" for the default section).</param>
    public sealed record Plan(string Book, (int First, int Last) Stages, (int First, int Last) Chapters, Stage0Result Stage0, CostEstimate Estimate, string? FromRun, string Profile = LlmSettings.Section, string? Model = null,
        StageOverrides? Overrides = null);

    /// <summary>Everything that can be refused before a model is called. Returns the plan, or the exit code of the refusal.</summary>
    public (Plan? Plan, int Exit) Preflight(IndexRunOptions o)
    {
        if (string.IsNullOrWhiteSpace(o.Book)) return (null, CliContext.Fail(Exit.BadInput, "Give --book <id> (`books list` shows them)."));
        var book = o.Book!;
        if (Manifest().All(b => b.Id != book)) return (null, CliContext.Fail(Exit.BadInput, $"Unknown book '{book}' (not in data/books/books.json). Add it with `books add`."));
        if (IndexPlan.ParseStages(o.Stages) is not { } stages) return (null, CliContext.Fail(Exit.BadInput, "--stages must be within 0-5, e.g. 0-5, 2..5 or 3."));
        if (o.FromRun is not null && stages.First < 2) return (null, CliContext.Fail(Exit.BadInput, "--from-run copies a run's earlier stage outputs: give --stages starting at 2 or later (e.g. --stages 3-5)."));
        if (o.FromRun is null && stages.First >= 2)
            return (null, CliContext.Fail(Exit.NotReady, $"Stage {stages.First} needs the output of the stage before it: give --from-run <runId>, or start at stage 0.", "stage-input-missing"));
        if (o.BudgetUsd is < 0) return (null, CliContext.Fail(Exit.BadInput, "--budget-usd must be 0 or more."));
        if (o.BudgetTokens is < 0) return (null, CliContext.Fail(Exit.BadInput, "--budget-tokens must be 0 or more."));
        if (!BookProfiles.Has(root, book)) return (null, CliContext.Fail(Exit.NotReady, $"'{book}' has no source text (data/books/source/{book}.txt). Fetch it with `books add`.", "no-source-text"));

        Stage0Result stage0;
        try { stage0 = Stage0.Prepare(BookProfiles.For(root, book), SourceTexts.Read(root, book), SourceTexts.Sha256(root, book), ChunkSettings.From((o.Overrides ?? StageOverrides.None).Over(loadConfig()))); }
        catch (InvalidDataException ex) { return (null, CliContext.Fail(Exit.NotReady, $"Stage 0 cannot read '{book}': {ex.Message}", "stage0-check")); }
        if (stage0.Checks.FirstOrDefault(c => !c.Passed) is { } failed)
            return (null, CliContext.Fail(Exit.NotReady,
                $"Stage 0 check '{failed.Name}' fails for '{book}': {failed.Details}. Fix data/books/profiles/{book}.json (or `books add <ref> --id {book} --force --expect-chapters <n>`), then run again.", "stage0-check"));

        var all = (First: 1, Last: stage0.Chapters.Count);
        var chapters = all;
        if (o.Chapters is { Length: > 0 } spec)
        {
            if (IndexingShared.ParseChapters(spec) is not { } range || range.First < 1 || range.Last > all.Last)
                return (null, CliContext.Fail(Exit.BadInput, $"--chapters must look like 1-3, within 1-{all.Last}."));
            chapters = range;
        }

        var config = loadConfig();
        var profile = o.Llm ?? IndexingShared.IndexingSettings.From(config).DefaultLlmProfile;
        var llm = LlmSettings.From(config, profile);
        if (o.FromRun is { } from)
        {
            if (!Directory.Exists(Path.Combine(RepoPaths.Runs(root), from))) return (null, CliContext.Fail(Exit.BadInput, $"--from-run: no run '{from}' under runs/."));
            // A continued run covers the chapters of the run it continues, and must be of the same book.
            var source = RunFolder.Open(RepoPaths.Runs(root), from);
            if (BookShelf.RunBook(source.Path) is { } sourceBook && sourceBook != book)
                return (null, CliContext.Fail(Exit.BadInput, $"--from-run {from} is a run of '{sourceBook}', not of '{book}'."));
            try { chapters = IndexCrosscheckCommand.RunChapters(source); }
            catch (Exception ex) when (ex is FileNotFoundException or InvalidOperationException or InvalidDataException or NullReferenceException or System.Text.Json.JsonException)
            {
                return (null, CliContext.Fail(Exit.NotReady, $"--from-run {from}: its run.json does not say which chapters it covers.", "stage-input-missing"));
            }
        }

        var chunks = stage0.Chunks.Count(c => c.Chapter >= chapters.First && c.Chapter <= chapters.Last);
        var estimate = IndexPlan.Estimate(chunks, stages, PricingFor(config, profile));
        var name = profile ?? LlmSettings.Section;
        // The budgets. A dollar budget cannot be kept without a price, so it refuses; a token budget needs none. Either one refuses.
        if (stages.Last >= 1)
        {
            if (o.BudgetUsd is not null && estimate.Cost is null)
                return (null, CliContext.Fail(Exit.NotReady,
                    $"--budget-usd needs a configured price for profile '{name}' and model '{llm.Model}'; set Llm:Profiles:{name}:Pricing (input and output per million tokens) or use --budget-tokens. Nothing was run.", "price-unknown"));
            if (o.BudgetUsd is { } budget && estimate.Cost is { } cost && cost > budget)
                return (null, CliContext.Fail(Exit.NotReady, $"The estimated cost, {cost:0.00} {estimate.Currency}, is over --budget-usd {budget:0.00} ({estimate.Basis}). Nothing was run.", "over-budget"));
            if (o.BudgetTokens is { } tokens && estimate.InputTokens + estimate.OutputTokens > tokens)
                return (null, CliContext.Fail(Exit.NotReady,
                    $"The estimated tokens, {estimate.InputTokens + estimate.OutputTokens:N0} ({estimate.InputTokens:N0} in and {estimate.OutputTokens:N0} out for {estimate.Chunks} chunks), are over --budget-tokens {tokens:N0} ({estimate.Basis}). Nothing was run.", "over-budget"));
            if (llm.Validate() is { Count: > 0 } problems)
                return (null, CliContext.Fail(Exit.NotReady, $"No model is configured for indexing ({string.Join("; ", problems)}). Run `{CliName.Wrapper} setup`.", "model-not-configured"));
        }
        return (new Plan(book, stages, chapters, stage0, estimate, o.FromRun, name, llm.Model, o.Overrides), Exit.Ok);
    }

    // ── run ────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    public const string WatchingNotice = "watching only — Ctrl+C leaves the watch, the run continues";

    /// <summary>The newest <c>index run</c> of a book, when it stopped (cancelled or failed) at a stage a run can continue from (2 to 5).</summary>
    public (string RunId, int Stage, DateTimeOffset? At)? StoppedRun(string book)
    {
        // The book's `index run`s (a run with a plan; not a Stage 0 run of `books add`, not a chat), the newest by the time it started.
        var runs = new List<(string Id, JsonNode Json, DateTimeOffset Started)>();
        foreach (var candidate in BookShelf.RunsByBook(root).GetValueOrDefault(book) ?? [])
        {
            try
            {
                if (JsonNode.Parse(RunFolder.ReadShared(Path.Combine(RepoPaths.Runs(root), candidate, RunFolder.RunJson))) is { } node && node["plan"] is not null)
                    runs.Add((candidate, node, node["startedAt"] is JsonValue t && t.TryGetValue<DateTimeOffset>(out var started) ? started : DateTimeOffset.MinValue));
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException) { }
        }
        foreach (var (id, json, _) in runs.OrderByDescending(r => r.Started).ThenByDescending(r => r.Id, StringComparer.Ordinal).Take(1))
        {
            var state = json["state"] is JsonValue s && s.TryGetValue<string>(out var text) ? text : null;
            if (state is not ("cancelled" or "failed") || json["stoppedAtStage"] is not JsonValue v || !v.TryGetValue<int>(out var stage) || stage is < 2 or > 5) return null;
            if (!File.Exists(Path.Combine(RepoPaths.Runs(root), id, RunFolder.Stage1Json))) return null;
            return (id, stage, json["finishedAt"] is JsonValue f && f.TryGetValue<DateTimeOffset>(out var at) ? at : null);
        }
        return null;
    }

    public const string StartQuestion = "Start indexing?";
    public const string ContinueChoice = "Continue the stopped run from stage ";
    public const string NewRunChoice = "Start a new full run";

    /// <summary>
    /// "Index a book" at a terminal. The book (the book picker), a pre-flight panel (title, chapters, chunks, model,
    /// estimate, the last run and its state), then one question: <c>Start indexing?</c> (the default is no when the cost is not known),
    /// or, when the book's newest run stopped, a choice between continuing it and a new full run. No stage picker: a person
    /// wants it to start; <c>--stages</c> and <c>--from-run</c> are for the command line.
    /// </summary>
    /// <returns>The options to run with; null with the exit code when nothing is to run (back, "no", a refusal).</returns>
    private async Task<(IndexRunOptions? Options, int Exit)> AskInteractiveAsync(IndexRunOptions o, CancellationToken ct)
    {
        while (true)
        {
            var book = await BookPicker.PickAsync(console, root, _ => true, b => $"{b.Id} — {b.Title}", addBook ?? BookPicker.AddAtConsole, ct);
            if (book is null) return (null, Exit.Ok);
            var (plan, refused) = Preflight(o with { Book = book, Stages = null, FromRun = null });
            if (plan is null) return (null, refused);

            var estimate = plan.Estimate;
            var entry = Manifest().FirstOrDefault(b => b.Id == book);
            var last = BookShelf.RunsByBook(root).GetValueOrDefault(book)?.LastOrDefault();
            var stopped = StoppedRun(book);
            var table = new Table().Border(TableBorder.None).HideHeaders().AddColumn("").AddColumn("");
            table.AddRow("Book", Markup.Escape($"{entry?.Title ?? book} ({book})"));
            table.AddRow("Chapters", (plan.Chapters.Last - plan.Chapters.First + 1).ToString());
            table.AddRow("Chunks", estimate.Chunks.ToString("N0"));
            table.AddRow("Model", Markup.Escape($"{plan.Model} (profile {plan.Profile})"));
            table.AddRow("Estimate", Markup.Escape($"about {estimate.InputTokens + estimate.OutputTokens:N0} tokens; cost {(estimate.Cost is { } c ? $"about {c:0.00} {estimate.Currency}" : $"not known (no price configured for {plan.Profile})")}"));
            table.AddRow("Last run", Markup.Escape(last is null ? "none" : $"{last} · {RunView.Summary(new RunView(root, last).Read(), plain: true)}"));
            console.Write(new Panel(table).Header("Index a book: stages 0-5, then the graph and the leak tests").Border(BoxBorder.Rounded));

            if (stopped is { } s)
            {
                console.MarkupLine(Markup.Escape($"A run stopped at stage {s.Stage}{(s.At is { } at ? $" on {at.ToLocalTime():yyyy-MM-dd HH:mm}" : "")} (run {s.RunId})."));
                var (resume, fresh) = ($"{ContinueChoice}{s.Stage}", NewRunChoice);
                var choice = await console.AskAsync(new SelectionPrompt<string>().Title($"{StartQuestion} {Prompts.EscBack}").AddChoices(resume, fresh, Prompts.Back).AddCancelResult(Prompts.Back), ct);
                if (choice == Prompts.Back) continue;   // back to the books
                return (choice == resume ? o with { Book = book, FromRun = s.RunId, Stages = $"{s.Stage}-5", Confirmed = true } : o with { Book = book, Stages = null, Confirmed = true }, Exit.Ok);
            }
            var unknown = estimate.Cost is null;
            if (!await console.ConfirmAsync(unknown ? $"{StartQuestion} (the cost is not known: no price configured for {Markup.Escape(plan.Profile)})" : StartQuestion, !unknown, ct))
            {
                console.MarkupLine("Nothing was run.");
                return (null, Exit.Ok);
            }
            return (o with { Book = book, Stages = null, Confirmed = true }, Exit.Ok);
        }
    }

    public async Task<int> RunAsync(IndexRunOptions o, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(o.Book) && o.FromRun is { } continued && BookShelf.RunBook(Path.Combine(RepoPaths.Runs(root), continued)) is { } itsBook)
            o = o with { Book = itsBook };   // `index run --from-run <id> --stages N-5` is enough to continue a run
        if (string.IsNullOrWhiteSpace(o.Book))
        {
            if (!interactive) return CliContext.NeedsTerminal("index run needs a book", "index run --book <id> [--chapters 1-3] [--stages 0-5] [--budget-tokens <n>] [--promote]");
            var (asked, exit0) = await AskInteractiveAsync(o, ct);
            if (asked is null) return exit0;
            o = asked;
        }

        var (plan, refused) = Preflight(o);
        if (plan is null) return refused;
        var estimate = plan.Estimate;
        var estimateText = plan.Stages.Last == 0 ? "no model calls"
            : $"about {estimate.InputTokens:N0} tokens in and {estimate.OutputTokens:N0} out for {estimate.Chunks} chunks; cost {(estimate.Cost is { } c ? $"about {c:0.00} {estimate.Currency}" : "not known (no price configured)")} — {estimate.Basis}";
        console.MarkupLine($"[bold]{Markup.Escape(plan.Book)}[/]: stages {plan.Stages.First}-{plan.Stages.Last}, chapters {plan.Chapters.First}-{plan.Chapters.Last}. Estimate: {Markup.Escape(estimateText)}.");
        if (o.DryRun)
        {
            // The pre-flight and the estimate, and nothing else.
            CliContext.Result = new { dryRun = true, book = plan.Book, stages = new { first = plan.Stages.First, last = plan.Stages.Last }, chapters = new { first = plan.Chapters.First, last = plan.Chapters.Last }, estimate, fromRun = plan.FromRun };
            console.MarkupLine("Dry run: nothing was run.");
            return Exit.Ok;
        }
        // A run whose cost is not known needs an explicit yes (the default answer is no); with a known cost Enter starts it.
        var costUnknown = plan.Stages.Last >= 1 && estimate.Cost is null;
        if (costUnknown && o.Yes && !o.Confirmed) Stderr.WriteLine("Cost not known (no price configured); running because --yes was given.");
        if (interactive && !o.Yes && !o.Confirmed && plan.Stages.Last >= 1 && !(costUnknown
                ? await console.ConfirmAsync($"Cost is not known (no price configured for {Markup.Escape(plan.Profile)}). Start anyway?", false, ct)
                : await console.ConfirmAsync("Start?", true, ct)))
        {
            console.MarkupLine("Nothing was run.");
            return Exit.Ok;
        }

        RunFolder run;
        try
        {
            run = plan.FromRun is { } from ? CopyStages(RunFolder.Open(RepoPaths.Runs(root), from), plan.Stages.First) : RunFolder.Create(RepoPaths.Runs(root));
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or InvalidOperationException)
        {
            return CliContext.Fail(Exit.NotReady, $"--from-run {plan.FromRun}: {ex.Message}", "stage-input-missing");
        }
        // A stop that was asked of the run being continued has been answered; its flag goes.
        if (plan.FromRun is { } stopped && Path.Combine(RepoPaths.Runs(root), stopped, CancelFile) is var flag && File.Exists(flag)) File.Delete(flag);

        var command = $"index run --book {plan.Book} --chapters {plan.Chapters.First}-{plan.Chapters.Last} --stages {plan.Stages.First}-{plan.Stages.Last}"
            + (plan.FromRun is { } f ? $" --from-run {f}" : "") + (o.Llm is { } l ? $" --llm {l}" : "") + (o.Promote ? " --promote" : "");
        var started = DateTimeOffset.UtcNow;
        var rows = new List<StageRow>();
        var buildDb = plan.Stages.Last == 5;
        var steps = Enumerable.Range(plan.Stages.First, plan.Stages.Last - plan.Stages.First + 1).Select(s => (Stage: s, Name: IndexPlan.StageLabel(s))).ToList();
        if (buildDb) steps.Add((6, "build-db"));

        // The plan in run.json (for `index watch`), the log, and three lines before the first model call.
        var info = PlanInfo(plan);
        var runPath = Path.GetRelativePath(root, run.Path).Replace('\\', '/');
        var log = RunLog.For(run, Stderr);
        RunLog.Current = log;
        run.Merge(new Dictionary<string, object?>
        {
            ["book"] = plan.Book,
            ["chapters"] = new { first = plan.Chapters.First, last = plan.Chapters.Last },
            ["startedAt"] = started,
            ["state"] = "running",
            ["currentStage"] = steps[0].Stage,
            ["plan"] = new { firstStage = info.FirstStage, lastStage = info.LastStage, firstChapter = info.FirstChapter, lastChapter = info.LastChapter, chunks = info.Chunks, firstChunk = info.FirstChunk, plannedCalls = info.PlannedCalls, estimatedTokens = info.EstimatedTokens },
        });
        log.Write($"run {run.RunId}: {command}");
        console.MarkupLine($"Run [bold]{run.RunId}[/] · {Markup.Escape(plan.Book)} · stages {plan.Stages.First}-{plan.Stages.Last} · {info.Chapters} chapter{(info.Chapters == 1 ? "" : "s")}, {info.Chunks} chunk{(info.Chunks == 1 ? "" : "s")}"
            + (plan.Stages.Last >= 1 ? $" · model {Markup.Escape(plan.Model ?? "?")}" : ""));
        console.MarkupLine(Markup.Escape(plan.Stages.Last == 0 ? "Estimate: no model calls"
            : $"Estimate: about {estimate.InputTokens + estimate.OutputTokens:N0} tokens (basis: {IndexPlan.EstimateBasisShort}); cost {(estimate.Cost is { } known ? $"about {known:0.00} {estimate.Currency}" : $"not known (no price for {plan.Profile})")}"));
        console.MarkupLine(Markup.Escape($"Log {runPath}/{RunLog.FileName} · Watch: {CliName.Wrapper} index watch --run {run.RunId} · Stop: Ctrl+C, or {CliName.Wrapper} index cancel --run {run.RunId}"));

        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var exit = Exit.Ok;
        var cancelled = false;
        var ticker = new Ticker(this, run, log, info, cancel, bars: interactive);
        try
        {
            using (ticker)
            {
                exit = await WithProgress(plan, run, o, steps, rows, cancel, ticker);
            }
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }

        // A warning that came many times is said once, with its count; each one is in run.log.
        foreach (var w in ticker.Monitor.Warnings.GroupBy(kv => kv.Key.Stage).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var text = $"{w.Key}: {string.Join(", ", w.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Value:N0} × {kv.Key.Type}"))} — see {RunLog.FileName}";
            if (interactive) console.MarkupLine($"[yellow]{Markup.Escape(text)}[/]");
            else Stderr.WriteLine(text);
        }

        var reached = ticker.Stage;
        string? continueWith = null;
        if (cancelled)
        {
            // Where it stopped and the command that goes on from there. Nothing is promoted.
            var where = RunProgress.StoppedAt(reached, info, ticker.Monitor.Stages.GetValueOrDefault(IndexPlan.StageKey(reached)));
            continueWith = ContinueCommand(plan, run, reached, o.Promote);
            var byFlag = File.Exists(run.File(CancelFile));
            exit = CliContext.Problem(Exit.Cancelled, $"{(byFlag ? $"Cancelled by `index cancel --run {run.RunId}`." : "Cancelled.")} {where} Continue: {CliName.Wrapper} {continueWith}", "cancelled");
            log.Write($"cancelled{(byFlag ? " by index cancel" : "")}: {where}");
            console.MarkupLine($"[yellow]{Markup.Escape(where)}[/] Continue: [bold]{Markup.Escape(CliName.Wrapper + " " + continueWith)}[/]");
        }

        var leaks = LeakTests(run);
        if (exit == Exit.Failed && !cancelled && leaks is false)
            exit = CliContext.Problem(Exit.CheckFailed, $"A leak test failed (runs/{run.RunId}/{Pipeline.LeakTestsJson}); nothing was promoted.", "leak-test");
        var promoted = false;
        var unchanged = false;
        if (exit == Exit.Ok && buildDb && leaks is true)
        {
            // Without --promote the rule decides: the default of the question at a terminal, the answer itself for a rebuild.
            bool promote;
            try
            {
                if (o.Promote && !o.PromoteByRule) promote = true;
                else if (o.PromoteByRule || interactive)
                {
                    var (byRule, rule) = PromoteRule.Decide(root, run, plan.Book, plan.Chapters, plan.Stage0.Chapters.Count);
                    log.Write($"run {run.RunId}: promote by default? {rule}");
                    console.MarkupLine($"[grey]Promote by default? {Markup.Escape(char.ToUpperInvariant(rule[0]) + rule[1..])}.[/]");
                    promote = o.PromoteByRule ? byRule : await console.ConfirmAsync($"Promote this run's graph to data/books/{plan.Book}.sqlite?", byRule, ct);
                }
                else promote = false;
            }
            catch (OperationCanceledException)
            {
                // The run itself is whole; only the question was left.
                promote = false;
                cancelled = true;
                exit = CliContext.Cancelled();
            }
            if (promote) (exit, promoted, unchanged) = Promote(run, plan.Book);
        }

        var finished = DateTimeOffset.UtcNow;
        run.RecordCommand(new { command, startedAt = started, finishedAt = finished, exit, cancelled, promoted },
            new Dictionary<string, object?>
            {
                ["cancelled"] = cancelled,
                ["book"] = plan.Book,
                ["finishedAt"] = finished,
                ["exit"] = exit,
                ["state"] = cancelled ? "cancelled" : exit == Exit.Ok ? "finished" : "failed",
                ["currentStage"] = reached,
                ["stoppedAtStage"] = cancelled || exit != Exit.Ok ? reached : null,
            });
        log.Write($"run {run.RunId}: {(cancelled ? "cancelled" : exit == Exit.Ok ? "done" : $"exit {exit}")}{(promoted ? unchanged ? ", already promoted (unchanged)" : ", promoted" : "")}");
        var graph = File.Exists(run.File(RunFolder.BookSqlite)) && exit is Exit.Ok or Exit.CheckFailed ? GraphCounts(run.File(RunFolder.BookSqlite)) : null;
        CliContext.Result = new
        {
            runId = run.RunId,
            book = plan.Book,
            chapters = new { first = plan.Chapters.First, last = plan.Chapters.Last },
            stages = rows,
            total = IndexPlan.Total(rows),
            estimate,
            graph,
            leakTestsPassed = leaks,
            promoted,
            unchanged,
            cancelled,
            stoppedAtStage = cancelled || exit != Exit.Ok ? reached : (int?)null,
            continueWith,
            path = runPath,
            log = $"{runPath}/{RunLog.FileName}",
        };

        RenderRows(rows, $"Run {run.RunId}");
        if (graph is not null) console.MarkupLine($"Graph: {graph.Nodes:N0} nodes, {graph.Edges:N0} edges ({Markup.Escape(string.Join(", ", graph.Labels.Select(kv => $"{kv.Value} {kv.Key}")))}); leak tests {(leaks is true ? "[green]pass[/]" : "[red]FAIL[/]")}.");
        if (exit == Exit.Ok)
        {
            console.MarkupLine($"Run [bold]{run.RunId}[/] done{(promoted ? unchanged ? $": it is already promoted to data/books/{Markup.Escape(plan.Book)}.sqlite, unchanged" : $", promoted to data/books/{Markup.Escape(plan.Book)}.sqlite" : "")}.");
            // The next command, on stderr.
            CliContext.Next(promoted ? $"chat --book {plan.Book}   ·   query --book {plan.Book}"
                : buildDb ? $"index promote --run {run.RunId}   ·   query --db {runPath}/{RunFolder.BookSqlite}"
                : plan.Stages.Last == 0 ? $"index run --book {plan.Book}"
                : $"index run --from-run {run.RunId} --stages {Math.Max(2, plan.Stages.Last + 1)}-5", Stderr);
        }
        else if (!cancelled)
        {
            continueWith = ContinueCommand(plan, run, reached, o.Promote);
            console.MarkupLine($"[red]Run {run.RunId} stopped with exit {exit} at {Markup.Escape(reached > 5 ? "build-db" : $"stage {reached}")}.[/] Once it is fixed, continue: [bold]{Markup.Escape(CliName.Wrapper + " " + continueWith)}[/]");
        }
        if (exit != Exit.Ok && !cancelled && interactive) foreach (var p in CliContext.Problems) console.MarkupLine($"[red]{Markup.Escape(p.Detail)}[/]");
        if (ReferenceEquals(RunLog.Current, log) && exit == Exit.Ok) RunLog.Current = null;
        return exit;
    }

    /// <summary>The command that goes on from the stage a run stopped at. Stages 0 and 1 leave nothing to copy, so they start again.</summary>
    public static string ContinueCommand(Plan plan, RunFolder run, int stage, bool promote)
    {
        if (stage > 5) return $"index build-db --run {run.RunId}";
        if (stage >= 2) return $"index run --from-run {run.RunId} --stages {stage}-{plan.Stages.Last}{(promote ? " --promote" : "")}";
        var all = plan.Chapters.First == 1 && plan.Chapters.Last == plan.Stage0.Chapters.Count;
        return $"index run --book {plan.Book}{(all ? "" : $" --chapters {plan.Chapters.First}-{plan.Chapters.Last}")} --stages 0-{plan.Stages.Last}{(promote ? " --promote" : "")}";
    }

    /// <summary>The plan as run.json records it: the model calls planned per stage and the token estimate per stage.</summary>
    private RunPlanInfo PlanInfo(Plan plan)
    {
        var chunks = plan.Stage0.Chunks.Where(c => c.Chapter >= plan.Chapters.First && c.Chapter <= plan.Chapters.Last).ToList();
        var chapters = plan.Chapters.Last - plan.Chapters.First + 1;
        var samples = IndexPlan.Samples(loadConfig());
        var planned = new Dictionary<string, long>();
        var tokens = new Dictionary<string, long>();
        for (var s = Math.Max(1, plan.Stages.First); s <= plan.Stages.Last; s++)
        {
            // Stage 1 a call per chunk, Stage 4 one for the anchor and one per chapter, Stage 5 one per chapter; Stage 2's
            // blocks are not known before Stage 1 has run, and Stage 3 calls no model.
            // With glean each chunk is asked twice. A judge call, or a chunk asked again in halves, is added as it is scheduled
            // (RunProgress.Planned), so the count never passes its total.
            var glean = Pipeline.ExtractSettingsFrom((plan.Overrides ?? StageOverrides.None).Over(loadConfig())).Glean;
            if (s is 1 or 4 or 5) planned[IndexPlan.StageKey(s)] = Math.Max(1, s == 1 ? chunks.Count * (glean ? 2 : 1) : s == 4 ? chapters + 1 : chapters);
            tokens[IndexPlan.StageKey(s)] = (IndexPlan.TokensPerChunk[s].In + IndexPlan.TokensPerChunk[s].Out) * chunks.Count;
        }
        return new RunPlanInfo(plan.Stages.First, plan.Stages.Last, plan.Chapters.First, plan.Chapters.Last, chunks.Count, chunks.Count > 0 ? chunks.Min(c => c.Index) : 0, planned, tokens);
    }

    // ── rebuild ────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// re-indexes books through the whole pipeline and promotes each: <c>index run --book &lt;id&gt; --stages 0-5 --promote</c> per
    /// book, in order, each in its own run folder. Every book is pre-flighted first (Stage 0 checks, model, estimate, the budgets, which
    /// apply per book) and the summed estimate is shown; if any book would be refused, nothing is run. One question for the whole set at
    /// a terminal. The first book that fails stops the rebuild with its exit code; later books are not started.
    /// </summary>
    public async Task<int> RebuildAsync(RebuildOptions o, CancellationToken ct)
    {
        var manifest = Manifest().Select(b => b.Id).ToList();
        var books = o.Books.Count > 0 ? o.Books.Distinct().ToList() : manifest;
        if (books.Count == 0) return CliContext.Fail(Exit.NotReady, "There is no book in data/books/books.json. Add one with `books add <gutenberg-number>`.", "no-books");
        if (books.FirstOrDefault(b => !manifest.Contains(b)) is { } unknown)
            return CliContext.Fail(Exit.BadInput, $"Unknown book '{unknown}' (not in data/books/books.json).");

        // Pre-flight, every book: first without the budgets, so the table shows every estimate; then with them, book by book.
        var rows = new List<RebuildRow>();
        var profile = LlmSettings.Section;
        foreach (var book in books)
        {
            var (plan, refused) = Preflight(new IndexRunOptions(book, Stages: "0-5", Llm: o.Llm, Overrides: o.Overrides));
            if (plan is null) return Refused(book, refused, rows, o.DryRun);
            profile = plan.Profile;
            rows.Add(new RebuildRow(book, plan.Chapters.Last - plan.Chapters.First + 1, plan.Estimate.Chunks, plan.Estimate.InputTokens, plan.Estimate.OutputTokens, plan.Estimate.Cost));
        }
        RenderPlan(rows);
        foreach (var book in books)
        {
            var (plan, refused) = Preflight(new IndexRunOptions(book, Stages: "0-5", Llm: o.Llm, BudgetUsd: o.BudgetUsd, BudgetTokens: o.BudgetTokens, Overrides: o.Overrides));
            if (plan is null) return Refused(book, refused, rows, o.DryRun);
        }
        if (o.DryRun)
        {
            CliContext.Result = RebuildResult(rows, dryRun: true);
            console.MarkupLine("Dry run: nothing was run.");
            return Exit.Ok;
        }

        var unknownCost = rows.Count(r => r.EstimatedCost is null);
        if (unknownCost > 0 && o.Yes) Stderr.WriteLine("Cost not known (no price configured); running because --yes was given.");
        if (interactive && !o.Yes && !(unknownCost > 0
                ? await console.ConfirmAsync($"Cost is not known for {unknownCost} of {rows.Count} book{(rows.Count == 1 ? "" : "s")} (no price configured for {Markup.Escape(profile)}). Rebuild anyway?", false, ct)
                : await console.ConfirmAsync($"Rebuild {rows.Count} book{(rows.Count == 1 ? "" : "s")}?", true, ct)))
        {
            console.MarkupLine("Nothing was run.");
            return Exit.Ok;
        }

        var exit = Exit.Ok;
        for (var i = 0; i < books.Count && exit == Exit.Ok; i++)
        {
            console.Write(new Rule(Markup.Escape($"{i + 1} of {books.Count}: {books[i]}")).LeftJustified());
            exit = await RunAsync(new IndexRunOptions(books[i], Stages: "0-5", Llm: o.Llm, BudgetUsd: o.BudgetUsd, Yes: o.Yes, Promote: true, BudgetTokens: o.BudgetTokens, Confirmed: true, Overrides: o.Overrides, PromoteByRule: true), ct);
            var result = CliContext.Result is null ? null : System.Text.Json.JsonSerializer.SerializeToNode(CliContext.Result, RunFolder.Json);
            rows[i] = rows[i] with
            {
                RunId = result?["runId"]?.GetValue<string>(),
                Exit = exit,
                Promoted = result?["promoted"]?.GetValue<bool>() ?? false,
                Nodes = result?["graph"]?["nodes"]?.GetValue<int>(),
                Edges = result?["graph"]?["edges"]?.GetValue<int>(),
                InputTokens = (result?["total"]?["allInputTokens"] ?? result?["total"]?["inputTokens"])?.GetValue<long>(),
                OutputTokens = result?["total"]?["outputTokens"]?.GetValue<long>(),
                Cost = BookShelf.Money(result?["total"]?["cost"]),
            };
            RenderDone([rows[i]], $"{books[i]}: {(exit == Exit.Ok ? "done" : exit == Exit.Cancelled ? "stopped" : $"stopped with exit {exit}")}");
        }
        CliContext.Result = RebuildResult(rows, dryRun: false);
        RenderDone(rows, "Rebuild");
        var notStarted = rows.Count(r => r.Exit is null);
        console.MarkupLine(exit == Exit.Ok ? $"Rebuilt {rows.Count} book{(rows.Count == 1 ? "" : "s")}; {rows.Count(r => r.Promoted == true)} promoted. Rebuild the API (`aspire run`) to see them in the web app."
            : exit == Exit.Cancelled ? $"[yellow]The rebuild was stopped at {Markup.Escape(rows.First(r => r.Exit is not (null or Exit.Ok)).Book)}; {notStarted} book{(notStarted == 1 ? " was" : "s were")} not started.[/] The line above says how to continue that book; then rebuild the others with `index rebuild --book <id>`."
            : $"[red]The rebuild stopped at {Markup.Escape(rows.First(r => r.Exit is not (null or Exit.Ok)).Book)} with exit {exit}; {notStarted} book{(notStarted == 1 ? " was" : "s were")} not started.[/] Continue that book with `index run --book <id> --from-run <runId> --stages <n>-5 --promote`.");
        return exit;
    }

    /// <summary>A book the pre-flight refuses: the problem names the book; with the estimates gathered so far as the result.</summary>
    private int Refused(string book, int exit, List<RebuildRow> rows, bool dryRun)
    {
        if (CliContext.Problems.Count > 0 && CliContext.Problems[^1] is var p) CliContext.Problems[^1] = p with { Detail = $"{book}: {p.Detail}" };
        CliContext.Result = RebuildResult(rows, dryRun);
        console.MarkupLine($"[red]{Markup.Escape(book)} would be refused (exit {exit}); nothing was run.[/]");
        return exit;
    }

    private static object RebuildResult(IReadOnlyList<RebuildRow> rows, bool dryRun) => new
    {
        dryRun,
        books = rows,
        estimate = new
        {
            chunks = rows.Sum(r => r.Chunks),
            inputTokens = rows.Sum(r => r.EstimatedInputTokens),
            outputTokens = rows.Sum(r => r.EstimatedOutputTokens),
            cost = rows.Any(r => r.EstimatedCost is null) ? null : (decimal?)rows.Sum(r => r.EstimatedCost!.Value),
            basis = IndexPlan.EstimateBasis,
        },
    };

    private void RenderPlan(IReadOnlyList<RebuildRow> rows)
    {
        var table = new Table().Border(TableBorder.Rounded).Title("Rebuild plan (stages 0-5, then promote)").AddColumn("Book").AddColumn(new TableColumn("Chapters").RightAligned())
            .AddColumn(new TableColumn("Chunks").RightAligned()).AddColumn(new TableColumn("Est. tokens in").RightAligned())
            .AddColumn(new TableColumn("Est. tokens out").RightAligned()).AddColumn(new TableColumn("Est. cost").RightAligned());
        foreach (var r in rows)
            table.AddRow(Markup.Escape(r.Book), r.Chapters.ToString(), r.Chunks.ToString("N0"), r.EstimatedInputTokens.ToString("N0"), r.EstimatedOutputTokens.ToString("N0"), r.EstimatedCost is { } c ? c.ToString("0.00") : "not known");
        table.AddRow("[bold]total[/]", rows.Sum(r => r.Chapters).ToString(), rows.Sum(r => r.Chunks).ToString("N0"), rows.Sum(r => r.EstimatedInputTokens).ToString("N0"),
            rows.Sum(r => r.EstimatedOutputTokens).ToString("N0"), rows.Any(r => r.EstimatedCost is null) ? "not known" : rows.Sum(r => r.EstimatedCost!.Value).ToString("0.00"));
        console.Write(table);
        console.MarkupLine($"[grey]{Markup.Escape(char.ToUpperInvariant(IndexPlan.EstimateBasis[0]) + IndexPlan.EstimateBasis[1..])}.[/]");
    }

    private void RenderDone(IReadOnlyList<RebuildRow> rows, string title)
    {
        var table = new Table().Border(TableBorder.Rounded).Title(Markup.Escape(title)).AddColumn("Book").AddColumn("Run").AddColumn(new TableColumn("Nodes").RightAligned())
            .AddColumn(new TableColumn("Edges").RightAligned()).AddColumn(new TableColumn("Tokens in").RightAligned()).AddColumn(new TableColumn("Tokens out").RightAligned())
            .AddColumn(new TableColumn("Cost").RightAligned()).AddColumn("Promoted");
        foreach (var r in rows)
            table.AddRow(Markup.Escape(r.Book), Markup.Escape(r.RunId ?? (r.Exit is null ? "not started" : "-")), r.Nodes?.ToString("N0") ?? "-", r.Edges?.ToString("N0") ?? "-",
                r.InputTokens?.ToString("N0") ?? "-", r.OutputTokens?.ToString("N0") ?? "-", r.Exit is null ? "-" : r.Cost is { } c ? c.ToString("0.00") : "not known",
                r.Promoted == true ? "[green]yes[/]" : r.Exit is null ? "-" : r.Exit == Exit.Ok ? "no" : r.Exit == Exit.Cancelled ? "[yellow]stopped[/]" : $"[red]no (exit {r.Exit})[/]");
        console.Write(table);
    }

    /// <summary>A new run with <paramref name="source"/>'s stage outputs before <paramref name="first"/>: stage-1.json and the plan fields (as
    /// <c>--stage1-from</c> does), then the later stage files and Stage 2's BookNLP folder.</summary>
    public RunFolder CopyStages(RunFolder source, int first)
    {
        var run = FrozenStage1.Copy(source, RepoPaths.Runs(root));
        var files = new List<string>();
        if (first >= 3) files.Add(RunFolder.Stage2Json);
        if (first >= 4) files.Add(RunFolder.Stage3Json);
        if (first >= 5) files.Add(RunFolder.Stage4Json);
        foreach (var file in files)
        {
            if (!File.Exists(source.File(file))) throw new FileNotFoundException($"{source.RunId} has no {file}.");
            File.Copy(source.File(file), run.File(file), overwrite: true);
        }
        var bookNlp = Path.Combine(source.Path, "stage2");
        if (first >= 3 && Directory.Exists(bookNlp))
            foreach (var file in Directory.GetFiles(bookNlp, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(run.Path, Path.GetRelativePath(source.Path, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, overwrite: true);
            }
        return run;
    }

    public const string HoldVariable = "TEMPORALGRAPH_STAGE_HOLD_SECONDS";

    /// <summary>
    /// A rehearsal aid: with <c>TEMPORALGRAPH_STAGE_HOLD_SECONDS=30</c> every stage waits that long before it starts, so Ctrl+C,
    /// <c>index cancel</c> and <c>index watch</c> can be tried on a run that calls no model (<c>--stages 0-0</c> ends in a few
    /// hundredths of a second otherwise). The wait ends at once when the run is stopped. Not set: no wait.
    /// </summary>
    private static double? HoldSeconds() => HoldForTest.Value ??
        (double.TryParse(Environment.GetEnvironmentVariable(HoldVariable), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s) && s > 0 ? s : null);

    /// <summary>Tests: the hold, for the test's own calls only (the environment variable would reach every test of the process).</summary>
    internal static readonly AsyncLocal<double?> HoldForTest = new();

    /// <summary>One step: the stage code the stage commands call, timed, with its tokens from cost.json afterwards.</summary>
    private async Task<StageRow> Step(int stage, string name, Plan plan, RunFolder run, IndexRunOptions o, CancellationToken ct)
    {
        // A stop between two stages: the stage that ended is whole, and this one is where the run stopped.
        ct.ThrowIfCancellationRequested();
        if (HoldSeconds() is { } hold) await Task.Delay(TimeSpan.FromSeconds(hold), ct);
        var watch = Stopwatch.StartNew();
        var before = IndexPlan.Total(IndexPlan.Stages(run.Path));
        int code;
        switch (stage)
        {
            case >= 1 when runStage is not null:
                code = await runStage(stage, run, ct);
                break;
            case 0:
                run.Write(RunFolder.Stage0Json, new { stage = Stage0.StageName, books = new[] { IndexPrepareCommand.Stage0Report(plan.Stage0) } });
                run.Write(RunFolder.ChecksJson, plan.Stage0.Checks);
                code = Exit.Ok;
                break;
            case 1:
                code = await Pipeline.ExtractAsync(root, run, plan.Book, plan.Chapters, o.Llm, o.Overrides ?? StageOverrides.None, ct);
                break;
            case 2:
                // A run continued at Stage 2 reuses the source run's BookNLP output when it has one (the same text).
                var bookNlp = plan.FromRun is { } from && Directory.Exists(Path.Combine(RepoPaths.Runs(root), from, "stage2")) ? from : null;
                code = await Pipeline.ResolveAsync(root, run, o.Llm, bookNlp, ct, o.Overrides);
                break;
            case 3: code = Pipeline.Link(root, run); break;
            case 4: code = await Pipeline.TimeAsync(root, run, o.Llm, ct, o.Overrides); break;
            case 5: code = await Pipeline.PerceiveAsync(root, run, o.Llm, ct, o.Overrides); break;
            default: code = Pipeline.BuildDb(root, run, force: true); break;
        }
        var after = IndexPlan.Total(IndexPlan.Stages(run.Path));
        decimal? cost = after.Cost is { } a ? a - (before.Cost ?? 0) : null;
        return new StageRow(name, after.Calls - before.Calls, after.InputTokens - before.InputTokens, after.OutputTokens - before.OutputTokens,
            stage is 0 or 3 or 6 ? 0 : cost, Math.Round(watch.Elapsed.TotalSeconds, 1), code, after.CacheReadTokens - before.CacheReadTokens, after.CacheWriteTokens - before.CacheWriteTokens);
    }

    /// <summary>
    /// The steps under one Spectre <c>Progress</c>. Every stage and build-db is a task from the start, not started, so the
    /// list of bars is the list of stages; a stage this run does not have says so. A stage counts model calls (chunks × samples for
    /// Stages 1, 3 and 4, chapters × samples for Stage 5; Stage 2's pairs and build-db are not known ahead), and its description
    /// carries the calls, the tokens and the cost as they land. Under the bars, through the render hook: where the run is, the last
    /// call, the time the run still needs. Spectre decides how to draw: bars at a terminal, lines at milestones when the output is
    /// redirected; then the per-stage lines on stderr and one line a minute say the rest. The stages' own tables go to a console of
    /// their own while it runs (one live display at a time); they are in the run folder's console.log. No prompt is shown in here.
    /// </summary>
    private async Task<int> WithProgress(Plan plan, RunFolder run, IndexRunOptions o, List<(int Stage, string Name)> steps, List<StageRow> rows, CancellationTokenSource cancel, Ticker ticker)
    {
        var exit = Exit.Ok;
        var shown = AnsiConsole.Console;
        var said = new StringWriter();
        var pricing = PricingFor(loadConfig(), plan.Profile == LlmSettings.Section ? null : plan.Profile);
        string Describe(int stage, string label, StageProgress? s)
        {
            var planned = RunProgress.Planned(ticker.Plan, stage, s, Indexing.StageRunner.Current);
            if (s is null || s.Calls == 0) return planned > 0 ? $"{label} · 0/{planned} calls" : label;
            var cost = pricing.Cost(s.InputTokens - s.CacheReadTokens - s.CacheWriteTokens, s.OutputTokens, s.CacheReadTokens, s.CacheWriteTokens);
            return $"{label} · {(planned > 0 ? $"{s.Calls}/{planned}" : s.Calls.ToString())} calls · {RunMonitor.Short(s.InputTokens)} in / {RunMonitor.Short(s.OutputTokens)} out"
                + (cost is { } c ? pricing.Currency is null or "USD" ? $" · ${c:0.00}" : $" · {c:0.00} {pricing.Currency}" : "");
        }
        AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(said), Ansi = AnsiSupport.No, ColorSystem = ColorSystemSupport.NoColors, Interactive = InteractionSupport.No });
        try
        {
            await console.Progress()
                .Columns(new TaskDescriptionColumn { Alignment = Justify.Left }, new ProgressBarColumn(), new PercentageColumn(), new ElapsedTimeColumn(), new RemainingTimeColumn(), new SpinnerColumn())
                .UseRenderHook((bars, _) => new Rows(bars, new Markup($"[grey]{Markup.Escape(ticker.Footer)}[/]")))
                .StartAsync(async progress =>
                {
                    var inRun = steps.Select(s => s.Stage).ToHashSet();
                    var tasks = Enumerable.Range(0, 7).ToDictionary(stage => stage, stage => progress.AddTask(
                        inRun.Contains(stage) ? Markup.Escape(Describe(stage, IndexPlan.StageLabel(stage), null)) : $"[grey]{Markup.Escape(IndexPlan.StageLabel(stage))} · not in this run[/]",
                        autoStart: false, maxValue: Math.Max(1, ticker.Plan.PlannedCalls.GetValueOrDefault(IndexPlan.StageKey(stage)))));
                    foreach (var (stage, name) in steps)
                    {
                        var task = tasks[stage];
                        task.IsIndeterminate = stage is 2 or 6;
                        task.StartTask();
                        ticker.Begin(stage, name, s =>
                        {
                            task.Description = Markup.Escape(Describe(stage, name, s));
                            if (task.IsIndeterminate) return;
                            // The total first (it grows when a judge call is scheduled), then the value, never past it.
                            if (RunProgress.Planned(ticker.Plan, stage, s, Indexing.StageRunner.Current) is var total and > 0 && total > task.MaxValue) task.MaxValue = total;
                            task.Value = Math.Min(task.MaxValue, s?.Calls ?? 0);
                        });
                        if (!interactive) Stderr.WriteLine($"stage {name}: started · run {run.RunId}");
                        var row = await Step(stage, name, plan, run, o, cancel.Token);
                        rows.Add(row);
                        ticker.End(row);
                        task.IsIndeterminate = false;
                        task.Value = task.MaxValue;
                        task.StopTask();
                        // One line per stage on stderr when there is no terminal.
                        if (!interactive) Stderr.WriteLine($"stage {name}: {(row.Exit == Exit.Ok ? "done" : $"exit {row.Exit}")} · {row.Calls} calls · {row.AllInputTokens}/{row.OutputTokens} tokens in/out ({row.CacheReadTokens} read from the cache) · {row.Seconds:0.0} s · run {run.RunId}");
                        if (row.Exit != Exit.Ok) { exit = row.Exit!.Value; break; }
                    }
                });
        }
        finally
        {
            AnsiConsole.Console = shown;
            File.WriteAllText(run.File("console.log"), said.ToString());
        }
        return exit;
    }

    /// <summary>
    /// What follows a run while its stages work, once every <c>poll</c> (a second): the stop asked by
    /// <c>index cancel</c> (the file <c>runs/&lt;runId&gt;/cancel</c>, the one cross-process control), the calls and the warnings that
    /// landed in calls.jsonl and failures.jsonl (into run.log: a line per chunk done, per retry and per warning), the numbers the
    /// progress display shows, the line under the bars and, without a terminal, a line on stderr every 60 s.
    /// </summary>
    private sealed class Ticker : IDisposable
    {
        private readonly IndexRunner _owner;
        private readonly RunFolder _run;
        private readonly RunLog _log;
        private readonly CancellationTokenSource _cancel;
        private readonly CancellationTokenSource _stop = new();
        private readonly bool _bars;
        private readonly Lock _tick = new();
        private readonly Task _loop;
        private readonly CallLogLines _lines;
        private Action<StageProgress?>? _onCalls;
        private string _name = "";
        private DateTimeOffset _nextBeat, _nextEta = DateTimeOffset.MinValue;
        private string _eta = RunProgress.EtaText(null);
        private bool _flagSeen;

        public RunPlanInfo Plan { get; }
        public RunMonitor Monitor { get; }

        /// <summary>The stage the run is in (6: build-db).</summary>
        public int Stage { get; private set; }

        /// <summary>The stages' numbers as of the last tick, for the progress columns (read from the render thread).</summary>
        public IReadOnlyDictionary<string, StageProgress> Snapshot { get; private set; } = new Dictionary<string, StageProgress>();

        /// <summary>"chapter 7/15 · chunk 61/142 · last call 3.2 s · run ETA 12 min · Ctrl+C to stop".</summary>
        public string Footer { get; private set; } = $"{RunProgress.EtaText(null)} · Ctrl+C to stop";

        public Ticker(IndexRunner owner, RunFolder run, RunLog log, RunPlanInfo plan, CancellationTokenSource cancel, bool bars)
        {
            (_owner, _run, _log, Plan, _cancel, _bars) = (owner, run, log, plan, cancel, bars);
            Monitor = new RunMonitor(run.Path);
            Monitor.Poll();   // only what this run adds
            _lines = new CallLogLines(log, (stage, index) => int.TryParse(stage.Replace("stage-", ""), out var number) ? Unit(number, index) : $"chunk {index}");
            Stage = plan.FirstStage;
            _nextBeat = DateTimeOffset.UtcNow + owner.Heartbeat;
            _loop = Task.Run(async () =>
            {
                while (!_stop.IsCancellationRequested)
                {
                    Tick();
                    try { await Task.Delay(owner.Poll, _stop.Token); }
                    catch (OperationCanceledException) { }
                }
            });
        }

        public void Begin(int stage, string name, Action<StageProgress?>? onCalls = null)
        {
            lock (_tick)
            {
                (Stage, _name, _onCalls) = (stage, name, onCalls);
                _log.Write($"stage {name}: start");
                try { _run.Merge(new Dictionary<string, object?> { ["currentStage"] = stage }); }
                catch (IOException) { }
            }
        }

        public void End(StageRow row)
        {
            Tick();
            lock (_tick)
            {
                _lines.StageEnded(IndexPlan.StageKey(Stage));
                _log.Write($"stage {_name}: {(row.Exit == Exit.Ok ? "done" : $"exit {row.Exit}")} · {row.Calls} calls · {row.AllInputTokens}/{row.OutputTokens} tokens in/out ({row.CacheReadTokens} read from the cache) · {row.Seconds:0.0} s");
                _onCalls = null;
            }
        }

        private string Unit(int stage, int index) =>
            stage == 5 ? $"chapter {Math.Clamp(index - Plan.FirstChapter + 1, 1, Math.Max(1, Plan.Chapters))}/{Plan.Chapters}" : $"chunk {Math.Clamp(index - Plan.FirstChunk + 1, 1, Math.Max(1, Plan.Chunks))}/{Plan.Chunks}";

        private void Tick()
        {
            lock (_tick)
            {
                try
                {
                    if (!_flagSeen && File.Exists(_run.File(CancelFile)))
                    {
                        _flagSeen = true;
                        _log.Write($"stop asked by `index cancel --run {_run.RunId}`");
                        _ = _cancel.CancelAsync();   // not on this thread: what waits on the token goes on from there
                    }
                    var (calls, warnings) = Monitor.Poll();
                    _lines.Add(calls, warnings);

                    Snapshot = Monitor.Stages.ToDictionary(kv => kv.Key, kv => new StageProgress(kv.Key)
                    {
                        Calls = kv.Value.Calls, InputTokens = kv.Value.InputTokens, CacheReadTokens = kv.Value.CacheReadTokens, CacheWriteTokens = kv.Value.CacheWriteTokens, OutputTokens = kv.Value.OutputTokens, Retries = kv.Value.Retries,
                        Chapter = kv.Value.Chapter, ChunkIndex = kv.Value.ChunkIndex, LastLatencyMs = kv.Value.LastLatencyMs, FirstCall = kv.Value.FirstCall, LastCall = kv.Value.LastCall,
                    });
                    var current = Snapshot.GetValueOrDefault(IndexPlan.StageKey(Stage));
                    _onCalls?.Invoke(current);

                    var time = DateTimeOffset.UtcNow;
                    if (time >= _nextEta)   // every 5 s, once there is something to go by
                    {
                        var left = RunProgress.Remaining(Plan, Monitor, Stage, time);
                        _eta = RunProgress.EtaText(left);
                        if (left is not null) _nextEta = time.AddSeconds(5);
                    }
                    var where = RunProgress.Where(Stage, Plan, current);
                    Footer = $"{(where.Length > 0 ? where + " · " : "")}{(Stage is >= 1 and <= 5 && RunProgress.Flight(StageRunner.Current, current) is { Length: > 0 } flight ? flight + " · " : "")}{_eta} · Ctrl+C to stop";
                    if (!_bars && time >= _nextBeat && Stage is >= 1 and <= 5)
                    {
                        // No bars without a terminal: a line a minute, so a log tail shows life.
                        _owner.Stderr.WriteLine(RunProgress.Line(Stage, Plan, current));
                        _nextBeat = time + _owner.Heartbeat;
                    }
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or UnauthorizedAccessException) { }
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            try { _loop.Wait(TimeSpan.FromSeconds(5)); }
            catch (AggregateException) { }
            Tick();   // what landed since the last tick
            _stop.Dispose();
        }
    }

    /// <summary>The run's leak-tests.json: true passed, false failed, null not run.</summary>
    public static bool? LeakTests(RunFolder run)
    {
        var path = run.File(Pipeline.LeakTestsJson);
        try { return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))?["passed"]?.GetValue<bool>() : null; }
        catch (System.Text.Json.JsonException) { return null; }
    }

    public sealed record GraphSummary(int Nodes, int Edges, IReadOnlyDictionary<string, int> Labels, IReadOnlyDictionary<string, int> EdgeTypes, int TimelineRows);

    public static GraphSummary GraphCounts(string dbPath)
    {
        var graph = BookGraph.Load(dbPath);
        return new GraphSummary(graph.Nodes.Count, graph.Relationships.Count,
            graph.Nodes.SelectMany(n => n.Labels).GroupBy(l => l).OrderBy(g => g.Key).ToDictionary(g => g.Key, g => g.Count()),
            graph.Relationships.GroupBy(r => r.Type).OrderBy(g => g.Key).ToDictionary(g => g.Key, g => g.Count()), graph.Timeline.Count);
    }

    /// <summary>The roster a newly indexed book starts with: its eight most-mentioned characters.</summary>
    public const int DefaultRoster = 8;

    /// <summary><c>index promote</c> for the run, then the default chat roster when the book has none.</summary>
    internal (int Exit, bool Promoted, bool Unchanged) Promote(RunFolder run, string book)
    {
        var target = Path.Combine(RepoPaths.Books(root), book + ".sqlite");
        var manifestPath = Path.Combine(RepoPaths.Books(root), BookManifest.FileName);
        // The run that is already promoted is left as it is.
        if (Promotion.AlreadyPromoted(run, target, IndexPrepareCommand.ProvenancePath(root, book))) return (Exit.Ok, true, true);
        if (!File.Exists(target)) BookDatabase.Create(target);
        try
        {
            Promotion.Promote(run, book, target, SourceTexts.Sha256(root, book), IndexPrepareCommand.ProvenancePath(root, book), manifestPath, PromoteRule.Score(root, run, book));
        }
        catch (InvalidOperationException ex) { return (CliContext.Fail(Exit.Failed, "Not promoted: " + ex.Message, "promote-failed"), false, false); }

        var manifest = BookManifest.Read(manifestPath);
        if (manifest.FirstOrDefault(b => b.Id == book) is { Chat: null or { Count: 0 } } entry)
        {
            var roster = Stage2File.Load(run).Entities.Where(e => e.Type == MentionType.Character).OrderByDescending(e => e.Mentions.Count)
                .Take(DefaultRoster).Select(e => e.CanonicalName).ToList();
            if (roster.Count > 0) BookManifest.Write(manifestPath, manifest.Select(b => b.Id == book ? entry with { Chat = roster } : b));
        }
        SeedQueries(root, book);
        return (Exit.Ok, true, false);
    }

    /// <summary>The book whose tool queries a newly indexed book starts with: the first one on the full schema.</summary>
    public const string ReferenceQueries = "hound-of-the-baskervilles";

    /// <summary>
    /// A book without <c>queries/&lt;id&gt;/</c> gets the tool queries of the full schema (the reference book's top-level .cypher files; they
    /// name no character or place), so `query`, `chat` and the API's tools work on it at once. Its own samples are written by hand.
    /// </summary>
    public static IReadOnlyList<string> SeedQueries(string root, string book)
    {
        var (from, to) = (Path.Combine(root, "queries", ReferenceQueries), Path.Combine(root, "queries", book));
        if (Directory.Exists(to) || !Directory.Exists(from)) return [];
        Directory.CreateDirectory(to);
        var copied = new List<string>();
        foreach (var file in Directory.GetFiles(from, "*.cypher"))
        {
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
            copied.Add(Path.GetFileName(file));
        }
        return copied;
    }

    // ── status ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private void RenderRows(IReadOnlyList<StageRow> rows, string title)
    {
        if (rows.Count == 0) return;
        var table = new Table().Border(TableBorder.Rounded).Title(Markup.Escape(title)).AddColumn("Stage").AddColumn(new TableColumn("Calls").RightAligned())
            .AddColumn(new TableColumn("Tokens in").RightAligned()).AddColumn(new TableColumn("Cache read").RightAligned()).AddColumn(new TableColumn("Tokens out").RightAligned())
            .AddColumn(new TableColumn("Cost").RightAligned()).AddColumn(new TableColumn("Seconds").RightAligned());
        foreach (var r in rows.Append(IndexPlan.Total(rows)))
            table.AddRow(Markup.Escape(r.Stage) + (r.Exit is > 0 ? $" [red](exit {r.Exit})[/]" : ""), r.Calls.ToString("N0"), r.AllInputTokens.ToString("N0"), r.CacheReadTokens.ToString("N0"), r.OutputTokens.ToString("N0"),
                r.Cost is { } c ? c.ToString("0.00") : "not known", r.Seconds is { } s ? s.ToString("0.0") : "-");
        console.Write(table);

        // Cost by stage; with no price configured, tokens by stage, said so.
        var priced = rows.All(r => r.Cost is not null) && rows.Sum(r => r.Cost ?? 0) > 0;
        var parts = rows.Where(r => priced ? r.Cost > 0 : r.AllInputTokens + r.OutputTokens > 0).ToList();
        if (parts.Count == 0) return;
        Color[] colors = [Color.Blue, Color.Green, Color.Yellow, Color.Fuchsia, Color.Aqua, Color.Orange1, Color.Grey];
        var chart = new BreakdownChart().Width(72);
        for (var i = 0; i < parts.Count; i++)
            chart.AddItem(Markup.Escape(parts[i].Stage), priced ? (double)parts[i].Cost!.Value : parts[i].AllInputTokens + parts[i].OutputTokens, colors[i % colors.Length]);
        console.MarkupLine(priced ? "Cost by stage:" : "Tokens by stage (no price is configured, so the cost is not known):");
        console.Write(chart);
    }

    public int Status(string? book, string? runId)
    {
        if (runId is null && book is null)
        {
            var books = BookShelf.List(root);
            CliContext.Result = new { books };
            console.Write(BooksRunner.Table(books));
            console.MarkupLine("One book: [bold]index status --book <id>[/]. One run: [bold]index status --run <runId>[/].");
            return Exit.Ok;
        }

        IReadOnlyList<string> runs = [];
        if (runId is null)
        {
            if (Manifest().All(b => b.Id != book)) return CliContext.Fail(Exit.BadInput, $"Unknown book '{book}' (not in data/books/books.json).");
            runs = BookShelf.RunsByBook(root).GetValueOrDefault(book!) ?? [];
            if (runs.Count == 0)
            {
                CliContext.Result = new { book, indexedRun = BookShelf.IndexedRun(root, book!), runs };
                console.MarkupLine($"{Markup.Escape(book!)} has no run under runs/. Start one with [bold]index run --book {Markup.Escape(book!)}[/].");
                return Exit.Ok;
            }
            var list = new Table().Border(TableBorder.Rounded).AddColumn("Run").AddColumn(new TableColumn("Calls").RightAligned()).AddColumn(new TableColumn("Tokens in").RightAligned())
                .AddColumn(new TableColumn("Tokens out").RightAligned()).AddColumn("Stages");
            foreach (var id in runs.TakeLast(12))
            {
                var stages = IndexPlan.Stages(Path.Combine(RepoPaths.Runs(root), id));
                var total = IndexPlan.Total(stages);
                list.AddRow(Markup.Escape(id), total.Calls.ToString("N0"), total.InputTokens.ToString("N0"), total.OutputTokens.ToString("N0"), Markup.Escape(string.Join(", ", stages.Select(s => s.Stage))));
            }
            console.Write(list);
            runId = runs[^1];
        }

        var dir = Path.Combine(RepoPaths.Runs(root), runId);
        if (!Directory.Exists(dir)) return CliContext.Fail(Exit.BadInput, $"No run '{runId}' under runs/.");
        var rows = IndexPlan.Stages(dir);
        var state = new RunView(root, runId).Read();
        var files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(f => new { path = Path.GetRelativePath(dir, f).Replace('\\', '/'), bytes = new FileInfo(f).Length }).ToList();
        CliContext.Result = StatusResult(state, rows, runs, files);

        RenderRows(rows, $"Run {runId}");
        console.MarkupLine($"{RunView.Summary(state)} · leak tests {(state.LeakTestsPassed is null ? "not run" : state.LeakTestsPassed.Value ? "[green]pass[/]" : "[red]FAIL[/]")}");
        var tree = new Tree(Markup.Escape($"runs/{runId}"));
        var folders = new Dictionary<string, TreeNode>();
        foreach (var f in files)
        {
            var slash = f.path.LastIndexOf('/');
            var label = Markup.Escape($"{f.path[(slash + 1)..]} ({f.bytes:N0} bytes)");
            if (slash < 0) tree.AddNode(label);
            else (folders.TryGetValue(f.path[..slash], out var node) ? node : folders[f.path[..slash]] = tree.AddNode(Markup.Escape(f.path[..slash] + "/"))).AddNode(label);
        }
        console.Write(tree);
        return Exit.Ok;
    }

    /// <summary>The result of <c>index status --run</c> and of <c>index watch</c>: its stages, tokens and cost, plus the elapsed time, the calls so
    /// far and the stage and chunk the run is in.</summary>
    private object StatusResult(RunView.State state, IReadOnlyList<StageRow> rows, IReadOnlyList<string> bookRuns, object? files)
    {
        var dir = Path.Combine(RepoPaths.Runs(root), state.RunId);
        JsonNode? runJson = null;
        try { if (File.Exists(Path.Combine(dir, RunFolder.RunJson))) runJson = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, RunFolder.RunJson))); }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException) { }
        return new
        {
            runId = state.RunId,
            book = state.Book,
            bookRuns,
            stages = rows,
            total = IndexPlan.Total(rows),
            running = !state.Finished,
            startedAt = state.StartedAt,
            finishedAt = state.FinishedAt,
            elapsedSeconds = state.ElapsedSeconds,
            callsSoFar = state.CallsSoFar,
            currentStage = state.CurrentStage,
            currentChunk = state.CurrentChunk,
            remainingSeconds = state.RemainingSeconds,
            leakTestsPassed = state.LeakTestsPassed,
            cancelled = state.Cancelled,
            cancelRequested = state.CancelRequested,
            promoted = state.Book is { } b && BookShelf.IndexedRun(root, b) == state.RunId,
            commands = (runJson?["commands"] as JsonArray)?.Select(c => c?["command"]?.GetValue<string>()).ToList(),
            files,
        };
    }

    /// <summary>
    /// <c>index watch --run &lt;id&gt;</c>: follows a run from another terminal, reading its files once a second: the stage
    /// table of <c>index status --run</c>, a bar for the stage it is in and the time it still needs. It ends with 0 when run.json has
    /// <c>finishedAt</c>, and with 130 on Ctrl+C, which leaves the watch and not the run. Without a terminal: a line every 60 s.
    /// </summary>
    public async Task<int> WatchAsync(string runId, CancellationToken ct)
    {
        var dir = Path.Combine(RepoPaths.Runs(root), runId);
        if (string.IsNullOrWhiteSpace(runId) || !Directory.Exists(dir)) return CliContext.Fail(Exit.BadInput, $"No run '{runId}' under runs/.");
        var view = new RunView(root, runId);
        var state = view.Read();
        try
        {
            if (state.Finished) console.Write(RunView.Render(state, watching: true));
            else if (interactive)
            {
                // One live display, once a second: the progress above, the last ten lines of run.log below (the newest at the bottom).
                await console.Live(RunView.Watch(state, view.LogTail())).Cropping(VerticalOverflowCropping.Top).StartAsync(async live =>
                {
                    while (!state.Finished)
                    {
                        await Task.Delay(Poll, ct);
                        state = view.Read();
                        live.UpdateTarget(RunView.Watch(state, view.LogTail()));
                    }
                });
            }
            else
            {
                Stderr.WriteLine($"{WatchingNotice} · run {runId}");
                var next = DateTimeOffset.MinValue;
                while (!state.Finished)
                {
                    if (DateTimeOffset.UtcNow >= next)
                    {
                        Stderr.WriteLine(Markup.Remove(RunView.Summary(state)));
                        next = DateTimeOffset.UtcNow + Heartbeat;
                    }
                    await Task.Delay(Poll, ct);
                    state = view.Read();
                }
                console.Write(RunView.Render(state, watching: true));
            }
        }
        catch (OperationCanceledException)
        {
            CliContext.Result = StatusResult(state, state.Stages, [], null);
            return CliContext.Cancelled($"Left the watch; run {runId} continues. Look again with: index watch --run {runId}");
        }
        CliContext.Result = StatusResult(state, state.Stages, [], null);
        return Exit.Ok;
    }

    public int Cancel(string runId)
    {
        var dir = Path.Combine(RepoPaths.Runs(root), runId);
        if (string.IsNullOrWhiteSpace(runId) || !Directory.Exists(dir)) return CliContext.Fail(Exit.BadInput, $"No run '{runId}' under runs/.");
        File.WriteAllText(Path.Combine(dir, CancelFile), DateTimeOffset.UtcNow.ToString("O") + "\n");
        CliContext.Result = new { runId, cancelRequested = true };
        console.MarkupLine($"Asked run [bold]{Markup.Escape(runId)}[/] to stop: `index run` checks for runs/{Markup.Escape(runId)}/{CancelFile} once a second, lets the model calls already sent finish, and says how to continue (its exit code is 130).");
        return Exit.Ok;
    }
}

// ── Spectre commands ───────────────────────────────────────────────────────────────────────────────────────────────────────────

public sealed class IndexRunCommand : AsyncCommand<IndexRunCommand.Settings>
{
    public sealed class Settings : StageSettings
    {
        [CommandOption("--book <ID>")][Description("Book id from data/books/books.json. At a terminal, asked when missing; with --from-run, the book of that run.")] public string? Book { get; init; }
        [CommandOption("--chapters <RANGE>")][Description("e.g. 1-3; default: every chapter.")] public string? Chapters { get; init; }
        [CommandOption("--stages <RANGE>")][Description("0-5 (default), 2..5, 3: 0 prepare, 1 extract, 2 resolve, 3 link, 4 time, 5 perceive. Ending at 5 also builds book.sqlite and runs the leak tests.")] public string? Stages { get; init; }
        [CommandOption("--from-run <RUN>")][Description("Copy this run's earlier stage outputs into the new run (with --stages starting at 2 or later): how a failed run continues.")] public string? FromRun { get; init; }
        [CommandOption("--llm <NAME>")][Description("A named LLM profile (Llm:Profiles:<name>); default: Indexing:Llm.")] public string? Llm { get; init; }
        [CommandOption("--budget-usd <N>")][Description("Refuse (exit 3) when the estimated cost is higher. Without a configured price for the profile the cost is not known and the run is refused too (exit 3, price-unknown): use --budget-tokens then.")] public decimal? BudgetUsd { get; init; }
        [CommandOption("--budget-tokens <N>")][Description("Refuse (exit 3) when the estimated tokens, in plus out, are more. Needs no price. Both budgets may be given; either one refuses.")] public long? BudgetTokens { get; init; }
        [CommandOption("--yes")][Description("Don't ask before the first model call. With no price configured it says on stderr that the cost is not known.")] public bool Yes { get; init; }
        [CommandOption("--promote")][Description("After the leak tests pass, promote the graph to data/books/<id>.sqlite (exit 4 and nothing promoted when they fail).")] public bool Promote { get; init; }
        [CommandOption("--dry-run")][Description("The pre-flight and the estimate only: no run folder, no model call. The budgets still refuse (exit 3).")] public bool DryRun { get; init; }
    }

    public override Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct) =>
        IndexRunner.ForConsole().RunAsync(new IndexRunOptions(s.Book, s.Chapters, s.Stages, s.FromRun, s.Llm, s.BudgetUsd, s.Yes, s.Promote, s.BudgetTokens, DryRun: s.DryRun, Overrides: s.Overrides()), ct);
}

public sealed class IndexRebuildCommand : AsyncCommand<IndexRebuildCommand.Settings>
{
    public sealed class Settings : StageSettings
    {
        [CommandOption("--book <ID>")][Description("A book to rebuild; repeat for several. Default: every book in data/books/books.json, in its order.")] public string[] Book { get; init; } = [];
        [CommandOption("--llm <NAME>")][Description("A named LLM profile (Llm:Profiles:<name>); default: Indexing:Llm.")] public string? Llm { get; init; }
        [CommandOption("--budget-tokens <N>")][Description("Per book: refuse (exit 3) when a book's estimated tokens, in plus out, are more. Needs no price.")] public long? BudgetTokens { get; init; }
        [CommandOption("--budget-usd <N>")][Description("Per book: refuse (exit 3) when a book's estimated cost is higher, or when no price is configured for the profile.")] public decimal? BudgetUsd { get; init; }
        [CommandOption("--dry-run")][Description("Pre-flight every book and print the plan with the summed estimate; run nothing.")] public bool DryRun { get; init; }
        [CommandOption("--yes")][Description("Don't ask before the first model call.")] public bool Yes { get; init; }
    }

    public override Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct) =>
        IndexRunner.ForConsole().RebuildAsync(new RebuildOptions(s.Book, s.Llm, s.BudgetUsd, s.BudgetTokens, s.DryRun, s.Yes, s.Overrides()), ct);
}

public sealed class IndexStatusCommand : Command<IndexStatusCommand.Settings>
{
    public sealed class Settings : GlobalSettings
    {
        [CommandOption("--book <ID>")][Description("The book's runs, and its newest run in detail.")] public string? Book { get; init; }
        [CommandOption("--run <RUN>")][Description("One run: stages, tokens, cost, leak tests and the files of its folder.")] public string? Run { get; init; }
    }

    public override int Execute(CommandContext context, Settings s, CancellationToken cancellationToken) => IndexRunner.ForConsole().Status(s.Book, s.Run);
}

public sealed class IndexWatchCommand : AsyncCommand<IndexWatchCommand.Settings>
{
    public sealed class Settings : GlobalSettings
    {
        [CommandOption("--run <RUN>")][Description("The run to watch.")] public string Run { get; init; } = "";
    }

    public override Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct) => IndexRunner.ForConsole().WatchAsync(s.Run, ct);
}

public sealed class IndexCancelCommand : Command<IndexCancelCommand.Settings>
{
    public sealed class Settings : GlobalSettings
    {
        [CommandOption("--run <RUN>")][Description("The run to stop.")] public string Run { get; init; } = "";
    }

    public override int Execute(CommandContext context, Settings s, CancellationToken cancellationToken) => IndexRunner.ForConsole().Cancel(s.Run);
}
