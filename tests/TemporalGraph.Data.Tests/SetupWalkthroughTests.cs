using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.UserSecrets;
using Spectre.Console.Testing;
using TemporalGraph.Cli.Setup;
using TemporalGraph.Llm;
using TemporalGraph.Testing;

namespace TemporalGraph.Data.Tests;

// The interactive `setup` driven by simulated keystrokes (Spectre's TestConsole). Back and Cancel from every step of every
// flow, the review's Edit a step and Edit the name, and the existing-setup paths. Most tests run on an in-memory store; the ones that
// save go through a temporary user-secrets store and the real `dotnet user-secrets` tool. Fake keys only, never in any output.
public sealed class SetupWalkthroughTests : IDisposable
{
    private const string Key = "sk-test-WALK-0123456789abcdef";
    private const string OtherKey = "sk-test-WALK-second-key-fedcba9876";
    private const string AzureEndpoint = "https://example-resource.openai.azure.com/openai/v1/";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TemporalGraph.Data.Tests", Guid.NewGuid().ToString("N"));
    private readonly string _storeId = "temporalgraph-setup-test-" + Guid.NewGuid().ToString("N");
    private readonly TestConsole _console = new TestConsole().Interactive();
    private readonly StringWriter _out = new();

    private static readonly IReadOnlyList<Prerequisite> AllThere = [new("dotnet", "10.0.100", "10.0.300", true)];

    public SetupWalkthroughTests()
    {
        Directory.CreateDirectory(_root);
        _console.Profile.Width = 160;
    }

    public void Dispose()
    {
        var folder = Path.GetDirectoryName(PathHelper.GetSecretsPathFromSecretsId(_storeId))!;
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        _console.Dispose();
    }

    private static IConfiguration ConfigOf(ISecretsStore store) =>
        new ConfigurationBuilder().AddInMemoryCollection(store.Read().Select(p => KeyValuePair.Create(p.Key, (string?)p.Value))).Build();

    private SetupRunner Runner(ISecretsStore store) =>
        new(store, () => store is MemoryStore ? ConfigOf(store) : LlmSettings.LoadConfiguration(_root, _storeId), () => AllThere, _console, _out,
            new StringReader(""), interactive: true, () => _root);

    private static MemoryStore Memory(params (string Name, string Provider, string Model)[] setupProfiles)
    {
        var store = new MemoryStore("test", new Dictionary<string, string>());
        var service = new SetupService(store);
        for (var i = 0; i < setupProfiles.Length; i++)
        {
            var (name, provider, model) = setupProfiles[i];
            service.Add(new AddRequest(name, provider, model, provider == "azure" ? AzureEndpoint : null, Key, Default: i == 0));
        }
        return store;
    }

    private async Task<int> Run(SetupRunner runner) => await runner.WalkthroughAsync(TestContext.Current.CancellationToken);

    private void AssertNoKey()
    {
        var all = _out + _console.Output;
        Assert.DoesNotContain(Key, all);
        Assert.DoesNotContain(OtherKey, all);
        Assert.DoesNotContain("0123456789abcdef", all);
    }

    private static Dictionary<string, string> Snapshot(ISecretsStore store) => store.Read().ToDictionary(p => p.Key, p => p.Value);

    // ── keystrokes ─────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private void Enter() => _console.Input.PushKey(ConsoleKey.Enter);
    private void Down(int times = 1) { for (var i = 0; i < times; i++) _console.Input.PushKey(ConsoleKey.DownArrow); }
    private void Pick(int index) { Down(index); Enter(); }
    private void Type(string text) => _console.Input.PushTextWithEnter(text);
    private void Esc() => _console.Input.PushKey(ConsoleKey.Escape);
    private void First() { _console.Input.PushKey(ConsoleKey.Home); Enter(); }

    /// <summary>A list's last choice is Cancel: with wrap-around, one Up from the first item reaches it.</summary>
    private void PickCancel() { _console.Input.PushKey(ConsoleKey.Home); _console.Input.PushKey(ConsoleKey.UpArrow); Enter(); }

    /// <summary>← Back, the second-to-last choice (Esc does the same; both are tested).</summary>
    private void PickBack() { _console.Input.PushKey(ConsoleKey.Home); _console.Input.PushKey(ConsoleKey.UpArrow); _console.Input.PushKey(ConsoleKey.UpArrow); Enter(); }

    private sealed record Stop(string Name, bool Text, Action Forward);

    /// <summary>The add flow's steps for a scenario, each with the keys that answer it and move on.</summary>
    private Stop[] AddPath(string scenario) => scenario switch
    {
        // Nothing configured: "Set up chat", OpenAI, the recommended model, the key, skip the test.
        "openai" =>
        [
            new("provider", false, () => Pick(0)),
            new("model", false, () => Pick(0)),
            new("key", true, () => Type(Key)),
            new("test", false, () => Pick(1)),
            new("review", false, () => { }),
        ],
        // Azure: the endpoint step appears, the model is a typed deployment name.
        "azure" =>
        [
            new("provider", false, () => Pick(2)),
            new("endpoint", true, () => Type(AzureEndpoint)),
            new("model", true, () => Type("gpt-4.1-mini")),
            new("key", true, () => Type(Key)),
            new("test", false, () => Pick(1)),
            new("review", false, () => { }),
        ],
        // A model is already set up: the role step appears (its current choice, "Just add it", is preselected).
        "existing" =>
        [
            new("provider", false, () => Pick(1)),
            new("model", false, () => Pick(0)),
            new("key", true, () => Type(OtherKey)),
            new("test", false, () => Pick(1)),
            new("role", false, () => Enter()),
            new("review", false, () => { }),
        ],
        _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
    };

    public static TheoryData<string, string> AddSteps() => new()
    {
        { "openai", "provider" }, { "openai", "model" }, { "openai", "key" }, { "openai", "test" }, { "openai", "review" },
        { "azure", "endpoint" }, { "azure", "model" }, { "azure", "key" }, { "azure", "test" }, { "azure", "review" },
        { "existing", "provider" }, { "existing", "role" }, { "existing", "review" },
    };

    // ── back and cancel from every step ────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(AddSteps))]
    public async Task Back_from_every_add_step_goes_to_the_one_before_and_writes_nothing(string scenario, string step)
    {
        var store = scenario == "existing" ? Memory(("openai", "openai", "gpt-4o-mini")) : Memory();
        var before = Snapshot(store);
        var path = AddPath(scenario);
        var at = Array.FindIndex(path, s => s.Name == step);

        Enter();                                            // home: the first item, "Set up chat" / "Add a model"
        foreach (var s in path[..at]) s.Forward();
        if (path[at].Text) Type("back"); else Esc();        // Back
        if (at > 0)
        {
            var previous = path[at - 1];
            if (previous.Text) Type("cancel"); else PickCancel();   // then cancel there
        }
        Esc();                                              // home: Esc exits

        var runner = Runner(store);
        Assert.Equal(0, await Run(runner));
        var expected = path[..(at + 1)].Select(s => s.Name).ToList();
        if (at > 0) expected.Add(path[at - 1].Name);
        Assert.Equal(expected, runner.LastTrail);
        Assert.Equal(before, Snapshot(store));
        Assert.Contains(at > 0 ? SetupRunner.CancelledNotice : "What would you like to do?", _console.Output);
        AssertNoKey();
    }

    [Theory]
    [MemberData(nameof(AddSteps))]
    public async Task Cancel_from_every_add_step_returns_home_and_writes_nothing(string scenario, string step)
    {
        var store = scenario == "existing" ? Memory(("openai", "openai", "gpt-4o-mini")) : Memory();
        var before = Snapshot(store);
        var path = AddPath(scenario);
        var at = Array.FindIndex(path, s => s.Name == step);

        Enter();
        foreach (var s in path[..at]) s.Forward();
        if (path[at].Text) Type("CANCEL"); else PickCancel();
        Esc();

        var runner = Runner(store);
        Assert.Equal(0, await Run(runner));
        Assert.Equal(path[..(at + 1)].Select(s => s.Name), runner.LastTrail);
        Assert.Equal(before, Snapshot(store));
        Assert.Contains(SetupRunner.CancelledNotice, _console.Output);
        Assert.Contains("Nothing is saved until you confirm · Ctrl+C quits safely", _console.Output);
        AssertNoKey();
    }

    [Fact]
    public async Task Back_as_a_choice_works_like_Esc_and_the_other_model_box_goes_back_to_the_list()
    {
        var store = Memory();
        Enter();                // Set up chat
        Pick(0);                // OpenAI
        Pick(2);                // model: Other…
        Type("back");           // the typed-id box: back to the list
        PickBack();             // the list's ← Back: back to the provider
        PickCancel();
        Esc();

        var runner = Runner(store);
        Assert.Equal(0, await Run(runner));
        Assert.Equal(["provider", "model", "provider"], runner.LastTrail);
        Assert.Empty(Snapshot(store));
    }

    /// <summary>The other menu actions, on two models setup made (a is the chat's default): each step's Back and Cancel.</summary>
    public static TheoryData<int, string, int, bool> MenuSteps() => new()
    {
        // menu index, flow, step index in the flow (0 = first; 1 = its review), true = Back / false = Cancel
        { 1, "default", 0, true }, { 1, "default", 0, false }, { 1, "default", 1, true }, { 1, "default", 1, false },
        { 2, "picker", 0, true }, { 2, "picker", 0, false }, { 2, "picker", 1, true }, { 2, "picker", 1, false },
        { 3, "indexing", 0, true }, { 3, "indexing", 0, false }, { 3, "indexing", 1, true }, { 3, "indexing", 1, false },
        { 5, "remove", 0, true }, { 5, "remove", 0, false }, { 5, "remove", 1, true }, { 5, "remove", 1, false },
        { 6, "undo", 0, true }, { 6, "undo", 0, false },
    };

    [Theory]
    [MemberData(nameof(MenuSteps))]
    public async Task Back_and_cancel_from_every_step_of_the_other_actions_write_nothing(int menu, string flow, int step, bool back)
    {
        var store = Memory(("a", "openai", "gpt-4o-mini"), ("b", "anthropic", "claude-haiku-5-5"));
        var before = Snapshot(store);

        Pick(menu);
        if (step == 1)
        {
            // Answer the first step: the default/indexing/remove lists keep the preselected item (remove: the first, b isn't the default).
            if (flow == "picker") Enter(); else if (flow == "remove") Pick(1); else Enter();
        }
        if (back)
        {
            Esc();
            if (step == 1)
            {
                if (flow == "picker") { _console.Input.PushKey(ConsoleKey.Home); _console.Input.PushKey(ConsoleKey.UpArrow); _console.Input.PushKey(ConsoleKey.Spacebar); Enter(); }
                else PickCancel();
            }
        }
        else if (flow == "picker" && step == 0) { _console.Input.PushKey(ConsoleKey.Home); _console.Input.PushKey(ConsoleKey.UpArrow); _console.Input.PushKey(ConsoleKey.Spacebar); Enter(); }
        else PickCancel();
        Esc();

        var runner = Runner(store);
        Assert.Equal(0, await Run(runner));
        var names = flow switch { "default" => new[] { "default", "review" }, "picker" => ["picker", "review"], "indexing" => ["indexing", "review"],
            "remove" => ["which", "review"], _ => ["review"] };
        var expected = names[..(step + 1)].ToList();
        if (back && step == 1) expected.Add(names[0]);
        Assert.Equal(expected, runner.LastTrail);
        Assert.Equal(before, Snapshot(store));
        if (!back || step == 1) Assert.Contains(SetupRunner.CancelledNotice, _console.Output);
        AssertNoKey();
    }

    // ── review, edit, save ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Review_edit_a_step_and_the_name_then_save_writes_exactly_what_it_showed()
    {
        var store = new UserSecretsStore(_storeId);
        Enter();                // Set up chat
        Pick(0);                // OpenAI
        Pick(0);                // gpt-4o-mini
        Type(Key);
        Pick(1);                // skip the test
        Pick(2);                // review: Save, Edit the name, Edit a step → Edit a step
        Pick(1);                // which step: Provider, Model, API key → Model
        Down(); Enter();        // gpt-4o-mini is preselected; one down: gpt-4.1-mini
        Type("");               // the key: Enter keeps the one typed
        Pick(1);                // skip the test
        Pick(1);                // review: Edit the name
        Type("my-openai");
        Enter();                // review: Save
        Esc();

        var runner = Runner(store);
        Assert.Equal(0, await Run(runner));
        Assert.Equal(["provider", "model", "key", "test", "review", "model", "key", "test", "review", "review"], runner.LastTrail);
        var saved = Snapshot(store);
        Assert.Equal("gpt-4.1-mini", saved["Llm:Profiles:my-openai:Model"]);
        Assert.Equal(Key, saved["Llm:Profiles:my-openai:ApiKey"]);
        Assert.Equal("my-openai", saved["Chat:Llm"]);
        Assert.DoesNotContain(saved.Keys, k => k.StartsWith("Llm:Profiles:openai:", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("New model my-openai: OpenAI · gpt-4.1-mini · key set (hidden)", _console.Output);
        Assert.Contains("Chat default (Chat:Llm): (fallback: the Llm section) → my-openai", _console.Output);
        Assert.Contains("Saved. Next: aspire run", _console.Output);
        Assert.Contains("chat default", _console.Output);    // the home screen after saving
        AssertNoKey();

        // The non-interactive reset still undoes it all.
        Assert.Equal(0, runner.Reset(null, yes: true, json: true));
        Assert.Empty(Snapshot(store));
    }

    [Fact]
    public async Task A_profile_name_is_derived_and_the_name_box_refuses_one_in_use()
    {
        var store = Memory(("openai", "openai", "gpt-4o-mini"));
        Enter();                // Add a model
        Pick(0);                // OpenAI again
        Pick(0);
        Type(OtherKey);
        Pick(1);                // skip the test
        First();                // role ("Just add it" is preselected): the first, make it the default
        Pick(1);                // review: Edit the name
        Type("openai");         // taken: refused
        Type("back");           // back to the review
        PickCancel();
        Esc();

        var runner = Runner(store);
        Assert.Equal(0, await Run(runner));
        Assert.Contains("New model openai-2: OpenAI · gpt-4o-mini", _console.Output);
        Assert.Contains("Chat default (Chat:Llm): openai → openai-2", _console.Output);
        Assert.Contains("There is already a model named 'openai'", _console.Output);
        Assert.DoesNotContain("Profile name", _console.Output);
        AssertNoKey();
    }

    // ── existing setups ────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_hand_made_profile_is_shown_read_only_and_adding_alongside_shows_old_to_new()
    {
        var store = new UserSecretsStore(_storeId);
        store.Set(new Dictionary<string, string>
        {
            ["Llm:Profiles:mine:Type"] = "OpenAI",
            ["Llm:Profiles:mine:Model"] = "my-model",
            ["Llm:Profiles:mine:ApiKey"] = OtherKey,
            ["Chat:Llm"] = "mine",
        });
        var before = Snapshot(store);

        Enter();                // Add a model (alongside)
        Pick(1);                // Anthropic
        Pick(0);                // claude-haiku-5-5
        Type(Key);
        Pick(1);                // skip the test
        First();                // role: make it the default
        Enter();                // Save
        Pick(5);                // menu: Add, default, picker, indexing, check, Remove a model
        Esc();                  // back from the list (only "anthropic" is in it)
        Esc();                  // exit

        var runner = Runner(store);
        Assert.Equal(0, await Run(runner));
        Assert.Contains("set up by hand", _console.Output);
        Assert.Contains("Chat default (Chat:Llm): mine → anthropic", _console.Output);
        Assert.Contains("1 model(s) set up by hand aren't listed", _console.Output);
        var saved = Snapshot(store);
        Assert.Equal("anthropic", saved["Chat:Llm"]);
        Assert.Equal(OtherKey, saved["Llm:Profiles:mine:ApiKey"]);
        Assert.Equal("16000", saved["Llm:Profiles:anthropic:MaxOutputTokens"]);

        // Undo from the menu gives the hand-made default back.
        _console.Input.PushKey(ConsoleKey.Home);
        Pick(6);                // Undo everything setup did
        Enter();                // Save
        Esc();
        Assert.Equal(0, await Run(Runner(store)));
        Assert.Equal(before, Snapshot(store));
        AssertNoKey();
    }

    [Fact]
    public async Task Removing_the_chat_default_asks_for_a_new_one_first()
    {
        var store = Memory(("a", "openai", "gpt-4o-mini"), ("b", "anthropic", "claude-haiku-5-5"));
        Pick(5);                // Remove a model
        Pick(0);                // a (the default)
        Enter();                // new default: b, the only other
        Enter();                // Save
        Esc();

        var runner = Runner(store);
        Assert.Equal(0, await Run(runner));
        Assert.Equal(["which", "new-default", "review"], runner.LastTrail);
        Assert.Contains("a is the chat's default. Which model should the chat use instead?", _console.Output);
        Assert.Contains("Remove model a (OpenAI · gpt-4o-mini) and its key", _console.Output);
        var saved = Snapshot(store);
        Assert.Equal("b", saved["Chat:Llm"]);
        Assert.DoesNotContain(saved.Keys, k => k.StartsWith("Llm:Profiles:a:", StringComparison.OrdinalIgnoreCase));
        AssertNoKey();
    }

    [Fact]
    public async Task The_default_picker_and_indexing_screens_preselect_the_current_values()
    {
        var store = Memory(("a", "openai", "gpt-4o-mini"), ("b", "anthropic", "claude-haiku-5-5"));
        new SetupService(store).SetShared(new Dictionary<string, string?> { ["Chat:Models"] = "b", ["Indexing:Llm"] = "b" });
        var before = Snapshot(store);

        Pick(1); Enter(); Enter();     // default: a preselected → nothing changes → Done
        Pick(2); Enter(); Enter();     // picker: b ticked → nothing changes
        Pick(3); Enter(); Enter();     // indexing: b preselected → nothing changes
        Pick(3); Down(); Enter();      // indexing again: one down from the preselected b is ← Back: home
        Esc();

        Assert.Equal(0, await Run(Runner(store)));
        Assert.Equal(before, Snapshot(store));
        Assert.Equal(3, CountOf(_console.Output, "the settings are already like this"));
        AssertNoKey();
    }

    [Fact]
    public async Task The_key_test_catches_a_bad_key_before_the_review()
    {
        using var server = new FakeLlmServer(errorFirst: 1, errorStatus: 401)
        {
            ErrorJson = $$$"""{"error":{"message":"Incorrect API key provided: {{{OtherKey}}}","type":"invalid_request_error","code":"invalid_api_key"}}""",
        };
        var store = Memory();
        Enter();                                  // Set up chat
        Pick(2);                                  // Azure OpenAI
        Type(server.BaseUrl + "/openai/v1/");     // the endpoint
        Type("gpt-4.1-mini");                     // the deployment
        Type(OtherKey);                           // a wrong key
        Enter();                                  // Test it now → 401
        Enter();                                  // Re-enter the key
        Type(Key);
        Enter();                                  // Test it now → ok
        PickCancel();                             // review: cancel
        Esc();

        var runner = Runner(store);
        Assert.Equal(0, await Run(runner));
        Assert.Equal(["provider", "endpoint", "model", "key", "test", "key", "test", "review"], runner.LastTrail);
        Assert.Contains("The test call failed:", _console.Output);
        Assert.Contains("[redacted]", _console.Output);
        Assert.Contains("The key works.", _console.Output);
        Assert.Equal("Bearer " + Key, server.Requests[^1].Authorization);
        Assert.Empty(Snapshot(store));
        AssertNoKey();
    }

    [Fact]
    public async Task Step_flow_skips_steps_that_do_not_apply_both_ways_and_counts_only_those()
    {
        var positions = new List<string>();
        var moves = new Queue<Move>([Move.Next, Move.Back, Move.Back]);
        Step<bool> S(string name, Func<bool, bool>? applies = null) =>
            new(name, name, (_, at) => { positions.Add($"{name} {at.Number}/{at.Count}"); return Task.FromResult(moves.Dequeue()); }, applies);
        var flow = new StepFlow<bool>([S("a"), S("b", d => d), S("c")]);
        Assert.Equal(FlowEnd.Back, await flow.RunAsync(false));
        Assert.Equal(["a 1/2", "c 2/2", "a 1/2"], positions);   // b skipped forwards and backwards; then Back from a leaves
        Assert.Equal(["a", "c", "a"], flow.Trail);
    }

    [Fact]
    public void Shared_settings_keep_the_users_value_for_undo_and_clearing_one_setup_wrote_removes_it()
    {
        var store = new MemoryStore("test", new Dictionary<string, string> { ["Chat:Models"] = "x" });
        var service = new SetupService(store);
        service.SetShared(new Dictionary<string, string?> { ["Chat:Models"] = "a", ["Indexing:Llm"] = "a" });
        Assert.Equal("x", store.Read()["Setup:Previous:Chat:Models"]);
        service.SetShared(new Dictionary<string, string?> { ["Chat:Models"] = "", ["Indexing:Llm"] = null });
        var values = store.Read();
        Assert.Equal("", values["Chat:Models"]);             // the user's value comes back on undo, not now
        Assert.False(values.ContainsKey("Indexing:Llm"));    // setup wrote it and there is nothing to give back
        service.Reset(null);
        Assert.Equal(new Dictionary<string, string> { ["Chat:Models"] = "x" }, Snapshot(store));
    }

    [Fact]
    public void Save_refuses_when_the_store_changed_after_the_review()
    {
        var store = Memory(("a", "openai", "gpt-4o-mini"));
        var plan = ChangePlan.Preview(store, s => s.SetShared(new Dictionary<string, string?> { ["Chat:Llm"] = "other" }));
        Assert.Equal([new StoreChange("Chat:Llm", "a", "other")], plan.Changes.Where(c => !c.Key.StartsWith("Setup:", StringComparison.Ordinal)));
        store.Set(new Dictionary<string, string> { ["Chat:Llm"] = "changed-elsewhere" });
        var before = Snapshot(store);
        Assert.Throws<SecretsStoreException>(() => plan.Apply(store));
        Assert.Equal(before, Snapshot(store));
    }

    private static int CountOf(string text, string part) => (text.Length - text.Replace(part, "", StringComparison.Ordinal).Length) / part.Length;
}
