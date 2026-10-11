using Microsoft.Extensions.Configuration;
using Spectre.Console;
using TemporalGraph.Llm;

namespace TemporalGraph.Cli.Setup;

/// <summary>
/// The interactive <c>setup</c>. A home screen (what is configured, and a menu) and, behind each menu item, a short
/// <see cref="StepFlow{TDraft}"/> whose last step is a review: it lists exactly what will change in the store, and only its Save
/// writes. Every list has ← Back and Cancel as its last two choices (Esc = Back); every text question takes the words "back" and
/// "cancel" (the hidden key prompt too). Leaving any other way, Ctrl+C included, writes nothing.
/// </summary>
public sealed partial class SetupRunner
{
    public const string BackWord = "back";
    public const string CancelWord = "cancel";
    public const string SavedNotice = "Saved. Next: [bold]aspire run[/], then open the URL the dashboard shows for [bold]web[/].";
    public const string CancelledNotice = "Cancelled. Nothing was saved.";
    private const string Promise = "Nothing is saved until you confirm · Ctrl+C quits safely";

    /// <summary>What follows a setup that saved something.</summary>
    public static readonly string NextAfterSetup = $"{CliName.Wrapper}   (menu)   ·   books add <number>";

    /// <summary>The steps each flow ran, for the tests (the last action's flow).</summary>
    public IReadOnlyList<string> LastTrail { get; private set; } = [];

    // ── home screen ────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private sealed record MenuItem(string Label, Func<StatusReport, CancellationToken, Task<string?>>? Run);

    public async Task<int> WalkthroughAsync(CancellationToken ct)
    {
        if (!interactive)
        {
            output.WriteLine("`setup` asks questions, and there is no terminal here. Run the steps directly instead:");
            output.WriteLine($"  {Cli} setup status --json");
            output.WriteLine($"  {Cli} setup add <profile> --provider {Providers.Names} --model <id> [--endpoint <url>] --key-env <VAR> --default");
            output.WriteLine($"  {Cli} setup check");
            output.WriteLine("The key goes in an environment variable the user sets themselves (for example OPENAI_API_KEY); it is never a command-line argument.");
            return BadInput;
        }

        var tools = prerequisites();
        string? notice = null;
        _ct = ct;
        _savedAny = false;
        try
        {
        while (true)
        {
            StatusReport report;
            try
            {
                report = _service.Status(loadConfig(), tools);
            }
            catch (SecretsStoreException e)
            {
                console.MarkupLine($"[red]{Markup.Escape(e.Message)}[/]");
                return Failed;
            }
            RenderHome(report);
            if (notice is not null) console.MarkupLine(notice);

            var exit = new MenuItem("Exit", null);
            var menu = Menu(report);
            var picked = await console.AskAsync(new SelectionPrompt<MenuItem>()
                .Title($"[bold]What would you like to do?[/]\n[grey]↑/↓ move · Enter chooses[/] {Prompts.EscExit}")
                .UseConverter(m => Markup.Escape(m.Label)).WrapAround().PageSize(12)
                .AddChoices(menu).AddChoices(exit).AddCancelResult(exit), ct);
            if (picked.Run is null)
            {
                console.MarkupLine(report.Chat.Ready
                    ? "Bye. The chat is ready: [bold]aspire run[/]. Run setup again any time to change models."
                    : "Bye. Nothing is configured for the chat yet; run setup again any time.");
                if (_savedAny) CliContext.Next(NextAfterSetup);
                return Ok;
            }
            try
            {
                notice = await picked.Run(report, ct);
            }
            catch (SecretsStoreException e)
            {
                notice = $"[red]{Markup.Escape(e.Message)}[/]";
            }
        }
        }
        catch (OperationCanceledException)
        {
            // Ctrl+C leaves the flow as its Cancel does: only a review's Save writes, so what was not saved is not there.
            console.MarkupLine(CancelledNotice);
            CliContext.Problem(Exit.Cancelled, "Cancelled.", "cancelled");
            return Exit.Cancelled;
        }
    }

    private List<MenuItem> Menu(StatusReport r)
    {
        var complete = r.Profiles.Where(p => p.Complete).ToList();
        var nothing = r.Profiles.Count == 0 && !r.Chat.Ready;
        var items = new List<MenuItem> { new(nothing ? "Set up chat" : "Add a model", AddModelAsync) };
        if (complete.Count > 0) items.Add(new("Change the chat's default model", ChangeDefaultAsync));
        if (complete.Count > 1) items.Add(new("Choose which models readers can pick", ChoosePickerAsync));
        if (complete.Count > 0) items.Add(new("Choose the model for indexing new books", ChooseIndexingAsync));
        if (complete.Count > 0 || r.Chat.Ready) items.Add(new("Check my models (one short call each)", CheckModelsAsync));
        if (r.Profiles.Any(p => p.WrittenBySetup)) items.Add(new("Remove a model", RemoveModelAsync));
        if (r.WrittenBySetup.Count > 0) items.Add(new("Undo everything setup did", UndoAsync));
        return items;
    }

    private void RenderHome(StatusReport r)
    {
        console.WriteLine();
        console.Write(new Rule("[bold]Temporal Graph setup[/]").LeftJustified());
        console.MarkupLine($"[grey]Settings live in this machine's user-secrets store ({Markup.Escape(store.Id)}), never in the repo. Setup changes only what it wrote; Undo puts the rest back.[/]");

        if (r.Profiles.Count == 0) console.MarkupLine("Models: [yellow]none yet[/]");
        else
        {
            var table = new Table().Border(TableBorder.Rounded).AddColumns("Model", "Provider", "Model id", "Key", "Used for");
            foreach (var p in r.Profiles)
            {
                var uses = new List<string>();
                if (string.Equals(r.Chat.Profile, p.Name, StringComparison.OrdinalIgnoreCase)) uses.Add("chat default");
                if (r.Chat.Models.Contains(p.Name, StringComparer.OrdinalIgnoreCase)) uses.Add("picker");
                if (string.Equals(r.Indexing.Profile, p.Name, StringComparison.OrdinalIgnoreCase)) uses.Add("indexing");
                var name = Markup.Escape(p.Name) + (p.WrittenBySetup ? "" : "\n[grey]set up by hand[/]");
                table.AddRow(name, Markup.Escape(ProviderLabel(p.Provider)), Markup.Escape(p.Model ?? "-"),
                    p.Key == "set" ? "set" : "[red]missing[/]", uses.Count == 0 ? "[grey]-[/]" : string.Join(", ", uses));
            }
            console.Write(table);
        }

        console.MarkupLine(r.Chat.Profile is null && !r.Chat.Ready
            ? "Chat default: [yellow]none yet[/] — the chat needs one model; choose [bold]Set up chat[/]"
            : $"Chat default: {Markup.Escape(r.Chat.Profile ?? "the Llm section")}{(r.Chat.Model is { } m ? $" ({Markup.Escape(m)})" : "")} — "
                + (r.Chat.Ready ? "[green]ready[/]" : "[yellow]not ready[/]"));
        var pickable = r.Chat.Models.Where(n => !string.Equals(n, r.Chat.Profile, StringComparison.OrdinalIgnoreCase)).ToList();
        console.MarkupLine($"Readers can pick: {(pickable.Count == 0 ? "only the default" : Markup.Escape(string.Join(", ", pickable)) + " (and the default)")}");
        console.MarkupLine($"Indexing new books: {Markup.Escape(r.Indexing.Profile ?? "the Llm section")} — "
            + (r.Indexing.Ready ? "ready" : "[grey]not set up (only needed to index a new book)[/]"));
        console.MarkupLine("Prerequisites: " + string.Join(" · ", r.Prerequisites.Select(p =>
            $"{p.Name} {(p.Found is { } f ? Markup.Escape(f) : "not found")} {(p.Ok ? "[green]ok[/]" : $"[red]needs {Markup.Escape(p.Needed)}+[/]")}")));
    }

    private static string ProviderLabel(string? provider) => Providers.Find(provider)?.Label ?? provider ?? "?";

    private static string Describe(ProfileStatus p) =>
        $"{p.Name} — {ProviderLabel(p.Provider)} · {p.Model ?? "?"}{(p.WrittenBySetup ? "" : " (set up by hand)")}";

    // ── add a model ────────────────────────────────────────────────────────────────────────────────────────────────────────────

    public enum AddRole { Default, Picker, Later }

    private sealed class AddDraft
    {
        public ProviderInfo? Provider;
        public string? Endpoint;
        public string? Model;
        public string? Key;
        public string Name = "";
        public bool NameEdited;
        public AddRole Role = AddRole.Default;
        public AddRequest Request => new(Name, Provider!.Name, Model ?? "", Endpoint, Key ?? "", Role == AddRole.Default, Role == AddRole.Picker);
    }

    private async Task<string?> AddModelAsync(StatusReport report, CancellationToken ct)
    {
        var current = report.Chat.Ready ? report.Chat.Profile ?? "the Llm section" : null;
        var pickerByHand = PickerSetByHand();
        var title = current is null ? "Set up chat" : "Add a model";
        var draft = new AddDraft { Role = current is null ? AddRole.Default : AddRole.Later };

        var flow = new StepFlow<AddDraft>(
        [
            new("provider", "Provider", async (d, at) =>
            {
                Header(title, at);
                var (kind, provider) = await PickAsync("Which model provider will you use?",
                    Providers.All.Select(p => (Markup.Escape($"{p.Label} — {p.Needs}"), p)), d.Provider);
                if (kind != MoveKind.Next) return Leave(kind);
                if (d.Provider != provider)
                {
                    d.Provider = provider;
                    d.Endpoint = d.Model = d.Key = null;
                    if (!d.NameEdited) d.Name = FreeName(provider!, report);
                }
                return Move.Next;
            }),
            new("endpoint", "Azure endpoint", async (d, at) =>
            {
                Header(title, at);
                var (kind, endpoint) = await QuestionAsync("Your Azure OpenAI endpoint",
                    "Azure portal → your resource → Endpoint, ending in /openai/v1/, e.g. https://<resource>.openai.azure.com/openai/v1/", d.Endpoint,
                    e => Uri.TryCreate(e, UriKind.Absolute, out var u) && u.Scheme is "https" or "http" ? null : "That isn't a URL; it starts with https://.");
                if (kind != MoveKind.Next) return Leave(kind);
                d.Endpoint = endpoint;
                return Move.Next;
            }, d => d.Provider?.NeedsEndpoint == true),
            new("model", "Model", (d, at) => ModelStepAsync(title, d, at)),
            new("key", "API key", async (d, at) =>
            {
                Header(title, at);
                var p = d.Provider!;
                var (kind, key) = await QuestionAsync($"Your {p.Label} API key (hidden as you type)",
                    $"Where to get it: {p.Needs}. It is stored in user-secrets on this machine only, never in the repo or a log.",
                    null, secret: true, keep: d.Key is not null);
                if (kind != MoveKind.Next) return Leave(kind);
                if (key.Length > 0) d.Key = key;
                return Move.Next;
            }),
            new("test", "Test the key", (d, at) => TestKeyStepAsync(title, d, at, ct)),
            new("role", "How the chat uses it", async (d, at) =>
            {
                Header(title, at);
                var choices = new List<(string, AddRole)> { ($"Make it the chat's default (now: {Markup.Escape(current!)})", AddRole.Default) };
                if (!pickerByHand) choices.Add(($"Let readers pick it in the chat; keep {Markup.Escape(current!)} as the default", AddRole.Picker));
                choices.Add(("Just add it (choose how to use it later from the menu)", AddRole.Later));
                var (kind, role) = await PickAsync($"How should the chat use {Markup.Escape(d.Name)}?", choices, d.Role);
                if (kind != MoveKind.Next) return Leave(kind);
                d.Role = role;
                return Move.Next;
            }, _ => current is not null),
            new("review", "Review", (d, at) => ReviewAsync(title, at, () => ChangePlan.Preview(store, s => s.Add(d.Request)), d.Request.Key,
                [($"Edit the name ({Markup.Escape(d.Name)})", async () =>
                {
                    var (kind, name) = await QuestionAsync("Name for this model", "Used in the settings (Llm:Profiles:<name>) and the chat's model picker. Letters, digits, '-' and '_'.",
                        d.Name, n => NameProblem(n, report));
                    if (kind == MoveKind.Next)
                    {
                        d.Name = name;
                        d.NameEdited = true;
                    }
                    return kind == MoveKind.Cancel ? Move.Cancel : Move.To("review");
                })])),
        ]);
        return await RunFlowAsync(flow, draft);
    }

    private async Task<Move> ModelStepAsync(string title, AddDraft d, StepPosition at)
    {
        var p = d.Provider!;
        while (true)
        {
            Header(title, at);
            if (p.Shortlist is not { Length: > 0 } list)
            {
                var (k, deployment) = await QuestionAsync("Your deployment's name",
                    "Azure calls a model by the name you gave its deployment (Azure portal → your resource → Deployments), e.g. gpt-4.1-mini.", d.Model,
                    m => m.StartsWith('<') ? "The deployment's name, without < >." : null);
                if (k != MoveKind.Next) return Leave(k);
                d.Model = deployment;
                return Move.Next;
            }

            const string other = "\0other";
            var choices = list.Select((m, i) => (Markup.Escape(m) + (i == 0 ? " [grey](recommended)[/]" : ""), m))
                .Append(("Other… (type a model id)", other));
            var current = d.Model is null ? null : list.Contains(d.Model) ? d.Model : other;
            var (kind, model) = await PickAsync($"Which {Markup.Escape(p.Label)} model?", choices, current);
            if (kind != MoveKind.Next) return Leave(kind);
            if (model != other)
            {
                d.Model = model;
                return Move.Next;
            }
            var (k2, typed) = await QuestionAsync("Model id", $"As {p.Label} names it. The listed ones passed this repo's chat checks; others may work.",
                d.Model is { } m2 && !list.Contains(m2) ? m2 : null, m => m.StartsWith('<') ? "The model id, without < >." : null);
            if (k2 == MoveKind.Cancel) return Move.Cancel;
            if (k2 == MoveKind.Back) continue;   // back to the list
            d.Model = typed;
            return Move.Next;
        }
    }

    private async Task<Move> TestKeyStepAsync(string title, AddDraft d, StepPosition at, CancellationToken ct)
    {
        Header(title, at);
        var (kind, test) = await PickAsync($"Test this key now? (one short call to {Markup.Escape(d.Provider!.Label)}, so a typo shows up before you save)",
            [("Test it now", true), ("Skip the test", false)], true);
        if (kind != MoveKind.Next) return Leave(kind);
        if (!test) return Move.Next;

        var request = d.Request;
        console.MarkupLine("Calling the model…");
        LlmCheckCommand.Result result;
        try
        {
            var config = new ConfigurationBuilder().AddConfiguration(loadConfig())
                .AddInMemoryCollection(SetupService.ProfileKeys(request).Select(p => KeyValuePair.Create(p.Key, (string?)p.Value))).Build();
            result = await LlmCheckCommand.CheckAsync(config, request.Profile, captureContent: false, repoRoot(), showStatus: false, command: "setup (key test)", ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            result = new LlmCheckCommand.Result(request.Profile, true, [], false, Error: e.Message);
        }
        if (result.Passed)
        {
            console.MarkupLine($"[green]The key works.[/] {Markup.Escape(result.Model ?? request.Model)} answered.");
            return Move.Next;
        }

        var why = result.Configured ? result.Error ?? "the call failed" : string.Join("; ", result.Problems);
        console.MarkupLine($"[red]The test call failed:[/] {Markup.Escape(SetupService.Redact(why, SafeRead(), d.Key))}");
        var (k2, next) = await PickAsync("What now?", [("Re-enter the key", "key"), ("Change the model", "model"), ("Continue anyway", "")], "key");
        if (k2 != MoveKind.Next) return Leave(k2);
        return string.IsNullOrEmpty(next) ? Move.Next : Move.To(next);
    }

    // ── the chat's default, the picker, indexing ───────────────────────────────────────────────────────────────────────────────

    private sealed class Choice
    {
        public string? Profile;
        public List<string> Profiles = [];
    }

    private Task<string?> ChangeDefaultAsync(StatusReport report, CancellationToken ct)
    {
        const string title = "Change the chat's default model";
        var complete = report.Profiles.Where(p => p.Complete).ToList();
        var flow = new StepFlow<Choice>(
        [
            new("default", "Default model", async (d, at) =>
            {
                Header(title, at);
                var (kind, name) = await PickAsync("Which model should the chat use by default?", complete.Select(p => (Markup.Escape(Describe(p)), p.Name)),
                    d.Profile ?? report.Chat.Profile);
                if (kind != MoveKind.Next) return Leave(kind);
                d.Profile = name;
                return Move.Next;
            }),
            new("review", "Review", (d, at) => ReviewAsync(title, at,
                () => ChangePlan.Preview(store, s => s.SetShared(new Dictionary<string, string?> { [SetupService.ChatLlm] = d.Profile })), null, [])),
        ]);
        return RunFlowAsync(flow, new Choice());
    }

    private Task<string?> ChoosePickerAsync(StatusReport report, CancellationToken ct)
    {
        const string title = "Choose which models readers can pick";
        if (PickerSetByHand())
            return Task.FromResult<string?>("[yellow]Chat:Models is set by hand as a list in your user-secrets; setup leaves it alone. Change it there, or remove it and come back.[/]");
        var options = report.Profiles.Where(p => p.Complete && !string.Equals(p.Name, report.Chat.Profile, StringComparison.OrdinalIgnoreCase)).ToList();
        // Names in Chat:Models that aren't offered here (an incomplete profile, say) are kept as they are.
        var kept = report.Chat.Models.Where(n => !options.Any(o => string.Equals(o.Name, n, StringComparison.OrdinalIgnoreCase))).ToList();
        var flow = new StepFlow<Choice>(
        [
            new("picker", "Models readers can pick", async (d, at) =>
            {
                Header(title, at);
                var chosen = d.Profile is null ? report.Chat.Models.ToList() : d.Profiles;
                var (kind, names) = await PickManyAsync($"Which models can readers pick in the chat? (the default, {Markup.Escape(report.Chat.Profile ?? "the Llm section")}, is always offered)",
                    options.Select(p => (Markup.Escape(Describe(p)), p.Name)), chosen);
                if (kind != MoveKind.Next) return Leave(kind);
                d.Profile = "";   // answered
                d.Profiles = names;
                return Move.Next;
            }),
            new("review", "Review", (d, at) => ReviewAsync(title, at, () => ChangePlan.Preview(store, s => s.SetShared(new Dictionary<string, string?>
                {
                    [SetupService.ChatModels] = string.Join(",", kept.Concat(d.Profiles)),
                })), null, [])),
        ]);
        return RunFlowAsync(flow, new Choice());
    }

    private Task<string?> ChooseIndexingAsync(StatusReport report, CancellationToken ct)
    {
        const string title = "Choose the model for indexing new books";
        var complete = report.Profiles.Where(p => p.Complete).ToList();
        var flow = new StepFlow<Choice>(
        [
            new("indexing", "Indexing model", async (d, at) =>
            {
                Header(title, at);
                console.MarkupLine("[grey]Only `index …` uses it, to add a new book. Reading and chatting don't need it.[/]");
                var (kind, name) = await PickAsync("Which model should index new books?", complete.Select(p => (Markup.Escape(Describe(p)), p.Name)),
                    d.Profile ?? report.Indexing.Profile);
                if (kind != MoveKind.Next) return Leave(kind);
                d.Profile = name;
                return Move.Next;
            }),
            new("review", "Review", (d, at) => ReviewAsync(title, at,
                () => ChangePlan.Preview(store, s => s.SetShared(new Dictionary<string, string?> { [SetupService.IndexingLlm] = d.Profile })), null, [])),
        ]);
        return RunFlowAsync(flow, new Choice());
    }

    // ── check, remove, undo ────────────────────────────────────────────────────────────────────────────────────────────────────

    private async Task<string?> CheckModelsAsync(StatusReport report, CancellationToken ct)
    {
        const string all = "\0all";
        Header("Check my models", null);
        var (kind, which) = await PickAsync("Which models? (one short call each; nothing is changed)",
            new[] { ("All of them", all) }.Concat(report.Profiles.Where(p => p.Complete).Select(p => (Markup.Escape(Describe(p)), p.Name))), all);
        if (kind == MoveKind.Cancel) return CancelledNotice;
        if (kind != MoveKind.Next) return null;
        var exit = await CheckAsync(which == all ? null : which, vet: false, json: false, ct);
        return exit == Ok ? "[green]Every check passed.[/]" : "[red]A check failed (above).[/] Remove the model and add it again with the right key or model id.";
    }

    private Task<string?> RemoveModelAsync(StatusReport report, CancellationToken ct)
    {
        const string title = "Remove a model";
        var mine = report.Profiles.Where(p => p.WrittenBySetup).ToList();
        var hand = report.Profiles.Count - mine.Count;
        bool IsDefault(string? name) => name is not null && string.Equals(name, report.Chat.Profile, StringComparison.OrdinalIgnoreCase);
        List<ProfileStatus> Others(Choice d) => report.Profiles.Where(p => p.Complete && !string.Equals(p.Name, d.Profile, StringComparison.OrdinalIgnoreCase)).ToList();

        var flow = new StepFlow<Choice>(
        [
            new("which", "Model to remove", async (d, at) =>
            {
                Header(title, at);
                if (hand > 0) console.MarkupLine($"[grey]{hand} model(s) set up by hand aren't listed: setup never changes those.[/]");
                var (kind, name) = await PickAsync("Which model should be removed?", mine.Select(p => (Markup.Escape(Describe(p)), p.Name)), d.Profile);
                if (kind != MoveKind.Next) return Leave(kind);
                if (!string.Equals(d.Profile, name, StringComparison.OrdinalIgnoreCase)) d.Profiles = [];
                d.Profile = name;
                return Move.Next;
            }),
            new("new-default", "New default", async (d, at) =>
            {
                Header(title, at);
                var (kind, name) = await PickAsync($"{Markup.Escape(d.Profile!)} is the chat's default. Which model should the chat use instead?",
                    Others(d).Select(p => (Markup.Escape(Describe(p)), p.Name)), d.Profiles.FirstOrDefault());
                if (kind != MoveKind.Next) return Leave(kind);
                d.Profiles = [name!];
                return Move.Next;
            }, d => IsDefault(d.Profile) && Others(d).Count > 0),
            new("review", "Review", (d, at) => ReviewAsync(title, at, () => ChangePlan.Preview(store, s =>
                {
                    s.Reset(d.Profile);
                    if (IsDefault(d.Profile) && d.Profiles.FirstOrDefault() is { } next)
                        s.SetShared(new Dictionary<string, string?> { [SetupService.ChatLlm] = next });
                }), null, [])),
        ]);
        return RunFlowAsync(flow, new Choice());
    }

    private Task<string?> UndoAsync(StatusReport report, CancellationToken ct)
    {
        var flow = new StepFlow<Choice>(
        [
            new("review", "Review", (d, at) => ReviewAsync("Undo everything setup did", at,
                () => ChangePlan.Preview(store, s => s.Reset(null)), null, [])),
        ]);
        return RunFlowAsync(flow, new Choice());
    }

    // ── the review ─────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The last step of every flow: what will change, then Save (writes exactly that), any extra choices, Edit a step, Back and
    /// Cancel. The plan is worked out on a copy of the store each time the screen is shown.
    /// </summary>
    private async Task<Move> ReviewAsync(string title, StepPosition at, Func<ChangePlan> plan, string? key, IReadOnlyList<(string Label, Func<Task<Move>> Then)> extra)
    {
        Header(title, at);
        ChangePlan changes;
        try
        {
            changes = plan();
        }
        catch (SetupInputException e)
        {
            console.MarkupLine($"[red]{Markup.Escape(SetupService.Redact(e.Message, SafeRead(), key))}[/]");
            return Move.Back;
        }
        RenderPlan(changes);

        var choices = new List<(string Label, Func<Task<Move>> Then)>
        {
            (changes.IsEmpty ? "Done (nothing to save)" : "Save", () =>
            {
                changes.Apply(store);
                _saved = !changes.IsEmpty;
                _savedAny |= _saved;
                return Task.FromResult(Move.Done);
            }),
        };
        choices.AddRange(extra);
        var editable = _editable();
        if (editable.Count > 1) choices.Add(("Edit a step", async () =>
        {
            var (kind, step) = await PickAsync("Which step?", editable.Select(s => (Markup.Escape(s.Title), s.Name)), null);
            return kind switch { MoveKind.Next => Move.To(step!), MoveKind.Cancel => Move.Cancel, _ => Move.To("review") };
        }));
        var (k, then) = await PickAsync(changes.IsEmpty ? "Nothing would change." : "Save these changes?", choices, choices[0].Then, cancelLabel: "Cancel (nothing saved)");
        return k == MoveKind.Next ? await then!() : Leave(k);
    }

    private void RenderPlan(ChangePlan plan)
    {
        if (plan.IsEmpty)
        {
            console.MarkupLine("Nothing would change: the settings are already like this.");
            return;
        }
        console.MarkupLine($"What will change in the user-secrets store ({Markup.Escape(store.Id)}):");
        var profiles = plan.Changes.Where(c => c.Key.StartsWith(LlmSettings.ProfilesSection + ":", StringComparison.OrdinalIgnoreCase))
            .GroupBy(c => c.Key.Split(':')[2], StringComparer.OrdinalIgnoreCase);
        foreach (var g in profiles)
        {
            string? Field(string f, bool old) => g.FirstOrDefault(c => c.Key.EndsWith(":" + f, StringComparison.OrdinalIgnoreCase)) is { } c ? old ? c.Old : c.New : null;
            string What(bool old) => $"{ProviderLabel(Providers.Classify(Field("Type", old), Field("Endpoint", old)))} · {Field("Model", old) ?? "?"}";
            if (g.All(c => c.Old is null))
                console.MarkupLine($"  [green]+[/] New model [bold]{Markup.Escape(g.Key)}[/]: {Markup.Escape(What(false))} · key set (hidden)");
            else if (g.All(c => c.New is null))
                console.MarkupLine($"  [red]−[/] Remove model [bold]{Markup.Escape(g.Key)}[/] ({Markup.Escape(What(true))}) and its key");
            else
                foreach (var c in g)
                    console.MarkupLine($"  [yellow]~[/] {Markup.Escape(g.Key)} {Markup.Escape(c.Key.Split(':')[^1])}: "
                        + (SetupService.IsSecretKey(c.Key) ? "key replaced (hidden)" : $"{Markup.Escape(c.Old ?? "(none)")} → {Markup.Escape(c.New ?? "(none)")}"));
        }
        foreach (var c in plan.Changes.Where(c => SetupService.SharedKeys.Contains(c.Key, StringComparer.OrdinalIgnoreCase)))
            console.MarkupLine($"  [yellow]~[/] {SharedLabel(c.Key)} ({Markup.Escape(c.Key)}): {Markup.Escape(Shown(c.Key, c.Old))} → {Markup.Escape(Shown(c.Key, c.New))}");
        var other = plan.Changes.Where(c => !c.Key.StartsWith(LlmSettings.ProfilesSection + ":", StringComparison.OrdinalIgnoreCase)
            && !c.Key.StartsWith("Setup:", StringComparison.OrdinalIgnoreCase) && !SetupService.SharedKeys.Contains(c.Key, StringComparer.OrdinalIgnoreCase));
        foreach (var c in other)
            console.MarkupLine($"  [yellow]~[/] {Markup.Escape(c.Key)}: {(c.New is null ? "removed" : "restored")}");
        if (plan.Changes.Any(c => c.Key.StartsWith("Setup:", StringComparison.OrdinalIgnoreCase)))
            console.MarkupLine("  [grey]and setup's own note of what it wrote (Setup:*), so Undo can put things back[/]");
        console.MarkupLine("[grey]Nothing else changes.[/]");
    }

    private static string SharedLabel(string key) => key switch
    {
        SetupService.ChatLlm => "Chat default",
        SetupService.ChatModels => "Readers can pick",
        _ => "Indexing model",
    };

    /// <summary>A shared setting's value for the review; null = not in the store (the chat then falls back to the Llm section).</summary>
    private static string Shown(string key, string? value)
    {
        if (value is null) return key == SetupService.ChatLlm ? "(fallback: the Llm section)" : "(not set)";
        if (key == SetupService.ChatModels) return SetupService.ParseList(value) is { Count: > 0 } list ? string.Join(", ", list) : "(none)";
        return value.Length == 0 ? "(none)" : value;
    }

    // ── prompts ────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private bool _saved;
    private bool _savedAny;
    private CancellationToken _ct;
    private Func<List<(string Title, string Name)>> _editable = () => [];

    /// <summary>Runs a flow and turns its end into the home screen's notice.</summary>
    private async Task<string?> RunFlowAsync<TDraft>(StepFlow<TDraft> flow, TDraft draft)
    {
        _saved = false;
        _editable = () => flow.Applicable(draft).Where(s => s.Name is not ("review" or "test")).Select(s => (s.Title, s.Name)).ToList();
        var end = await flow.RunAsync(draft);
        LastTrail = flow.Trail;
        return end switch
        {
            FlowEnd.Cancelled => CancelledNotice,
            FlowEnd.Done when _saved => SavedNotice,
            FlowEnd.Done => "Nothing changed.",
            _ => null,
        };
    }

    private void Header(string title, StepPosition? at)
    {
        console.WriteLine();
        console.Write(new Rule($"[bold]{Markup.Escape(title)}[/]").LeftJustified());
        console.MarkupLine(at is { } s ? $"[grey]Step {s.Number} of {s.Count} · {Promise}[/]" : $"[grey]{Promise}[/]");
    }

    private static Move Leave(MoveKind kind) => kind == MoveKind.Cancel ? Move.Cancel : Move.Back;

    private sealed record Option<T>(string Label, T? Value, MoveKind Kind);

    /// <summary>A list with ← Back and Cancel as its last two choices; Esc = Back. The labels are markup.</summary>
    private async Task<(MoveKind Kind, T? Value)> PickAsync<T>(string title, IEnumerable<(string Label, T Value)> items, T? current, string cancelLabel = "Cancel")
    {
        var options = items.Select(i => new Option<T>(i.Label, i.Value, MoveKind.Next)).ToList();
        var back = new Option<T>("← Back", default, MoveKind.Back);
        var prompt = new SelectionPrompt<Option<T>>()
            .Title($"[bold]{title}[/]\n[grey]↑/↓ move · Enter chooses[/] {Prompts.EscBack}")
            .UseConverter(o => o.Label).WrapAround().PageSize(12)
            .AddChoices(options).AddChoices(back, new Option<T>(Markup.Escape(cancelLabel), default, MoveKind.Cancel)).AddCancelResult(back);
        if (current is not null && options.FirstOrDefault(o => EqualityComparer<T>.Default.Equals(o.Value, current)) is { } selected) prompt.DefaultValue(selected);
        var picked = await console.AskAsync(prompt, _ct);
        return (picked.Kind, picked.Value);
    }

    /// <summary>A multi-select; ticking ← Back or Cancel (or Esc) leaves, as in <see cref="Select{T}"/>.</summary>
    private async Task<(MoveKind Kind, List<string> Values)> PickManyAsync(string title, IEnumerable<(string Label, string Value)> items, IReadOnlyCollection<string> current)
    {
        var options = items.Select(i => new Option<string>(i.Label, i.Value, MoveKind.Next)).ToList();
        var back = new Option<string>("← Back", null, MoveKind.Back);
        var cancel = new Option<string>("Cancel", null, MoveKind.Cancel);
        var prompt = new MultiSelectionPrompt<Option<string>>()
            .Title($"[bold]{title}[/]\n[grey]↑/↓ move · Space ticks · Enter confirms (or tick ← Back or Cancel)[/] {Prompts.EscBack}")
            .UseConverter(o => o.Label).WrapAround().PageSize(12).NotRequired()
            .AddChoices(options).AddChoices(back, cancel).AddCancelResult([back]);
        foreach (var o in options.Where(o => current.Contains(o.Value!, StringComparer.OrdinalIgnoreCase))) prompt.Select(o);
        var picked = await console.AskAsync(prompt, _ct);
        if (picked.Any(p => p.Kind == MoveKind.Cancel)) return (MoveKind.Cancel, []);
        if (picked.Any(p => p.Kind == MoveKind.Back)) return (MoveKind.Back, []);
        return (MoveKind.Next, picked.Select(p => p.Value!).ToList());
    }

    /// <summary>
    /// A text question with a hint line under it. "back" and "cancel" (any case) leave the step; for the hidden prompt they are
    /// compared, never shown. <paramref name="keep"/>: the hidden prompt already has a value, and Enter keeps it (returns "").
    /// </summary>
    private async Task<(MoveKind Kind, string Value)> QuestionAsync(string question, string explain, string? defaultValue, Func<string, string?>? problem = null,
        bool secret = false, bool keep = false)
    {
        console.MarkupLine($"[bold]{Markup.Escape(question)}[/]");
        console.MarkupLine($"[grey]{Markup.Escape(explain)}[/]");
        var enter = keep ? "Enter keeps the key you typed · " : defaultValue is not null ? "Enter accepts the default · " : "";
        console.MarkupLine($"[grey]{enter}type \"{BackWord}\" to go back · \"{CancelWord}\" to stop[/]");
        var prompt = new TextPrompt<string>(">").Validate(v =>
            IsWord(v) || (keep && v.Length == 0) ? ValidationResult.Success()
            : problem?.Invoke(v.Trim()) is { } p ? ValidationResult.Error($"[red]{Markup.Escape(p)}[/]") : ValidationResult.Success());
        if (secret) prompt.Secret(null);
        if (keep) prompt.AllowEmpty();
        else if (defaultValue is not null) prompt.DefaultValue(defaultValue);
        var answer = (await console.AskAsync(prompt, _ct)).Trim();
        if (string.Equals(answer, BackWord, StringComparison.OrdinalIgnoreCase)) return (MoveKind.Back, "");
        if (string.Equals(answer, CancelWord, StringComparison.OrdinalIgnoreCase)) return (MoveKind.Cancel, "");
        return (MoveKind.Next, answer);
    }

    private static bool IsWord(string v) =>
        string.Equals(v.Trim(), BackWord, StringComparison.OrdinalIgnoreCase) || string.Equals(v.Trim(), CancelWord, StringComparison.OrdinalIgnoreCase);

    // ── names ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private bool PickerSetByHand()
    {
        var values = SafeRead();
        var written = SetupService.WrittenKeys(values);
        return values.Keys.Any(k => k.StartsWith(SetupService.ChatModels + ":", StringComparison.OrdinalIgnoreCase) && !written.Contains(k, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Why a new model can't have this name: not a valid name, or one already in use (by setup or by hand).</summary>
    private string? NameProblem(string name, StatusReport report)
    {
        if (!LlmSettings.IsValidProfileName(name)) return "Use letters, digits, '-' and '_' only.";
        var prefix = SetupService.ProfilePrefix(name);
        if (report.Profiles.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
            || SafeRead().Keys.Any(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            return $"There is already a model named '{name}'; pick another name.";
        return null;
    }

    /// <summary>The provider's name, then <c>-2</c>, <c>-3</c>, … until it is free (the walkthrough no longer asks).</summary>
    private string FreeName(ProviderInfo provider, StatusReport report)
    {
        for (var i = 1; ; i++)
        {
            var name = i == 1 ? provider.Name : $"{provider.Name}-{i}";
            if (NameProblem(name, report) is null) return name;
        }
    }
}
