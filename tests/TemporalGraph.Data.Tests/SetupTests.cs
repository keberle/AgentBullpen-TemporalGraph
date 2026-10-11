using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration.UserSecrets;
using Spectre.Console;
using Spectre.Console.Testing;
using TemporalGraph.Cli.Setup;
using TemporalGraph.Llm;

namespace TemporalGraph.Data.Tests;

// The `setup` commands against a temporary user-secrets store (never temporalgraph-cli), through the real `dotnet user-secrets`
// tool. Fake keys only; every test asserts that none of them reaches stdout, the console or the JSON.
public sealed class SetupTests : IDisposable
{
    private const string Key = "sk-test-SETUP-0123456789abcdef";
    private const string ForeignKey = "foreign-key-NOT-from-setup-987654";
    private readonly string _storeId = "temporalgraph-setup-test-" + Guid.NewGuid().ToString("N");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TemporalGraph.Data.Tests", Guid.NewGuid().ToString("N"));
    private readonly string _keyVariable = "TG47_TEST_KEY_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
    private readonly UserSecretsStore _store;
    private readonly TestConsole _console = new();
    private readonly StringWriter _out = new();

    private static readonly IReadOnlyList<Prerequisite> AllThere = [new("dotnet", "10.0.100", "10.0.300", true)];

    public SetupTests()
    {
        _store = new UserSecretsStore(_storeId);
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable(_keyVariable, Key);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(_keyVariable, null);
        var folder = Path.GetDirectoryName(PathHelper.GetSecretsPathFromSecretsId(_storeId))!;
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        _console.Dispose();
    }

    private SetupRunner Runner(bool interactive = false, TextReader? stdin = null, TestConsole? console = null) =>
        new(_store, () => LlmSettings.LoadConfiguration(_root, _storeId), () => AllThere, console ?? _console, _out, stdin ?? new NoInput(),
            interactive, () => _root);

    /// <summary>Everything the commands printed, for the "no key in any output" checks.</summary>
    private string AllOutput => _out + _console.Output;

    private void AssertNoKey()
    {
        Assert.DoesNotContain(Key, AllOutput);
        Assert.DoesNotContain(ForeignKey, AllOutput);
        Assert.DoesNotContain("0123456789abcdef", AllOutput);   // nor a fragment
    }

    private JsonNode LastJson()
    {
        var text = _out.ToString();
        var start = text.LastIndexOf("\n{", StringComparison.Ordinal) is var i and >= 0 ? i + 1 : text.IndexOf('{');
        return JsonNode.Parse(text[start..])!;
    }

    private Dictionary<string, string> Snapshot() => _store.Read().ToDictionary(p => p.Key, p => p.Value);

    [Fact]
    public void Add_status_reset_leaves_the_store_as_it_was_and_never_touches_keys_it_did_not_write()
    {
        // The user's own settings: a profile with its key, the chat default and a picker list.
        _store.Set(new Dictionary<string, string>
        {
            ["Llm:Profiles:mine:Type"] = "OpenAI",
            ["Llm:Profiles:mine:Model"] = "my-model",
            ["Llm:Profiles:mine:ApiKey"] = ForeignKey,
            ["Chat:Llm"] = "mine",
            ["Chat:Models"] = "mine",
            ["Something:Else"] = "kept",
        });
        var before = Snapshot();
        var runner = Runner();

        Assert.Equal(0, runner.Add(new AddOptions("or", "openrouter", "openai/gpt-4o-mini", KeyEnv: _keyVariable, Default: true, Pickable: true, Indexing: true), json: true));
        var added = LastJson();
        Assert.Equal("or", added["profile"]!.GetValue<string>());
        Assert.Contains("Llm:Profiles:or:ApiKey", added["written"]!.AsArray().Select(k => k!.GetValue<string>()));

        var store = Snapshot();
        Assert.Equal(Key, store["Llm:Profiles:or:ApiKey"]);
        Assert.Equal("https://openrouter.ai/api/v1", store["Llm:Profiles:or:Endpoint"]);
        Assert.Equal("or", store["Chat:Llm"]);
        Assert.Equal("mine,or", store["Chat:Models"]);
        Assert.Equal(ForeignKey, store["Llm:Profiles:mine:ApiKey"]);   // untouched

        Assert.Equal(0, runner.Status(json: true));
        var status = LastJson();
        Assert.True(status["ready"]!.GetValue<bool>());
        Assert.Equal("or", status["chat"]!["profile"]!.GetValue<string>());
        var or = status["profiles"]!.AsArray().Single(p => p!["name"]!.GetValue<string>() == "or")!;
        Assert.Equal("set", or["key"]!.GetValue<string>());
        Assert.Equal("openrouter", or["provider"]!.GetValue<string>());
        Assert.True(or["writtenBySetup"]!.GetValue<bool>());
        Assert.False(status["profiles"]!.AsArray().Single(p => p!["name"]!.GetValue<string>() == "mine")!["writtenBySetup"]!.GetValue<bool>());

        Assert.Equal(0, runner.Reset(null, yes: true, json: true));
        var reset = LastJson();
        Assert.Contains("Chat:Llm", reset["restored"]!.AsArray().Select(k => k!.GetValue<string>()));
        Assert.Equal(before, Snapshot());
        AssertNoKey();
    }

    [Fact]
    public void Reset_of_one_profile_keeps_the_others_and_gives_back_the_shared_keys()
    {
        var runner = Runner();
        Assert.Equal(0, runner.Add(new AddOptions("a", "openai", "gpt-4o-mini", KeyEnv: _keyVariable, Default: true, Pickable: true), json: true));
        Assert.Equal(0, runner.Add(new AddOptions("b", "anthropic", "claude-haiku-5-5", KeyEnv: _keyVariable, Pickable: true), json: true));
        Assert.Equal("16000", Snapshot()["Llm:Profiles:b:MaxOutputTokens"]);

        Assert.Equal(0, runner.Reset("a", yes: true, json: true));
        var store = Snapshot();
        Assert.DoesNotContain(store.Keys, k => k.StartsWith("Llm:Profiles:a:", StringComparison.Ordinal));
        Assert.False(store.ContainsKey("Chat:Llm"));          // it pointed at a; setup wrote it; nothing to give back
        Assert.Equal("b", store["Chat:Models"]);
        Assert.Equal(Key, store["Llm:Profiles:b:ApiKey"]);

        Assert.Equal(2, runner.Reset("nosuch", yes: true, json: true));
        Assert.Equal(0, runner.Reset(null, yes: true, json: true));
        Assert.Empty(Snapshot());
        AssertNoKey();
    }

    [Fact]
    public void Add_refuses_a_profile_it_did_not_create_and_bad_input_exits_2()
    {
        _store.Set(new Dictionary<string, string> { ["Llm:Profiles:mine:ApiKey"] = ForeignKey });
        var before = Snapshot();
        var runner = Runner();

        Assert.Equal(2, runner.Add(new AddOptions("mine", "openai", "gpt-4o-mini", KeyEnv: _keyVariable), json: true));
        Assert.Contains("already exists", LastJson()["error"]!.GetValue<string>());
        Assert.Equal(2, runner.Add(new AddOptions("x", "nosuch", "m", KeyEnv: _keyVariable), json: true));
        Assert.Equal(2, runner.Add(new AddOptions("x", "azure", "deployment", KeyEnv: _keyVariable), json: true));                    // no endpoint
        Assert.Equal(2, runner.Add(new AddOptions("x", "anthropic", "m", Endpoint: "https://example.test/", KeyEnv: _keyVariable), json: true));
        Assert.Equal(2, runner.Add(new AddOptions("bad name", "openai", "m", KeyEnv: _keyVariable), json: true));
        Assert.Equal(2, runner.Add(new AddOptions("x", "openai", "m", KeyEnv: "TG47_NOT_SET_" + Guid.NewGuid().ToString("N")), json: true));
        Assert.Equal(2, runner.Add(new AddOptions("x", "openai", "m", KeyEnv: _keyVariable, KeyStdin: true), json: true));

        Assert.Equal(before, Snapshot());
        AssertNoKey();
    }

    [Fact]
    public void The_key_can_come_from_standard_input()
    {
        var runner = Runner(stdin: new StringReader(Key + "\n"));
        Assert.Equal(0, runner.Add(new AddOptions("a", "openai", "gpt-4o-mini", KeyStdin: true, Default: true), json: false));
        Assert.Equal(Key, Snapshot()["Llm:Profiles:a:ApiKey"]);
        Assert.Contains("Saved profile", _console.Output);
        AssertNoKey();
    }

    [Fact]
    public async Task Without_a_terminal_nothing_waits_for_input_and_the_exact_command_is_printed()
    {
        var runner = Runner(interactive: false);   // NoInput throws if anything reads standard input

        var walkthrough = runner.WalkthroughAsync(TestContext.Current.CancellationToken);
        Assert.True(walkthrough.IsCompleted);
        Assert.Equal(2, await walkthrough);
        Assert.Contains("setup add <profile> --provider openai|anthropic|azure|openrouter", _out.ToString());

        Assert.Equal(2, runner.Add(new AddOptions("a", "anthropic", "claude-haiku-5-5", Default: true), json: true));
        Assert.Contains("setup add a --provider anthropic --model claude-haiku-5-5 --key-env ANTHROPIC_API_KEY --default", LastJson()["error"]!.GetValue<string>());

        Assert.Equal(0, runner.Add(new AddOptions("a", "openai", "gpt-4o-mini", KeyEnv: _keyVariable), json: true));
        Assert.Equal(2, runner.Reset(null, yes: false, json: true));     // asks for --yes instead of a prompt
        Assert.Contains("--yes", LastJson()["error"]!.GetValue<string>());
        Assert.True(Snapshot().ContainsKey("Llm:Profiles:a:ApiKey"));    // nothing removed
        AssertNoKey();
    }

    [Fact]
    public void Status_says_what_is_missing_and_exits_3()
    {
        var runner = Runner();
        Assert.Equal(3, runner.Status(json: true));
        var status = LastJson();
        Assert.False(status["ready"]!.GetValue<bool>());
        Assert.False(status["chat"]!["ready"]!.GetValue<bool>());
        Assert.Contains("setup add", status["missing"]![0]!.GetValue<string>());

        Assert.Equal(3, runner.Status(json: false));
        Assert.Contains("Not ready", _console.Output);
    }

    [Fact]
    public async Task Check_calls_each_profile_and_never_shows_a_key_a_provider_echoes()
    {
        using var good = new FakeLlmServer();
        using var bad = new FakeLlmServer(errorFirst: 2, errorStatus: 401)
        {
            // A provider that repeats the key in its error message.
            ErrorJson = $$$"""{"error":{"message":"Incorrect API key provided: {{{Key}}}","type":"invalid_request_error","code":"invalid_api_key"}}""",
        };
        var runner = Runner();
        Assert.Equal(0, runner.Add(new AddOptions("good", "openai", "gpt-4o-mini", Endpoint: good.BaseUrl + "/openai/v1/", KeyEnv: _keyVariable, Default: true), json: true));
        Assert.Equal(0, await runner.CheckAsync("good", vet: false, json: true, TestContext.Current.CancellationToken));
        var ok = LastJson();
        Assert.True(ok["passed"]!.GetValue<bool>());
        Assert.Equal("good", ok["profiles"]![0]!["profile"]!.GetValue<string>());

        Assert.Equal(0, runner.Add(new AddOptions("bad", "openai", "gpt-4o-mini", Endpoint: bad.BaseUrl + "/openai/v1/", KeyEnv: _keyVariable), json: true));
        Assert.Equal(1, await runner.CheckAsync(null, vet: false, json: true, TestContext.Current.CancellationToken));
        var failed = LastJson();
        Assert.False(failed["passed"]!.GetValue<bool>());
        var error = failed["profiles"]!.AsArray().Single(p => p!["profile"]!.GetValue<string>() == "bad")!["error"]!.GetValue<string>();
        Assert.Contains("[redacted]", error);

        Assert.Equal(1, await runner.CheckAsync("bad", vet: false, json: false, TestContext.Current.CancellationToken));
        Assert.Equal(2, await runner.CheckAsync("nosuch", vet: false, json: true, TestContext.Current.CancellationToken));
        Assert.Equal("Bearer " + Key, bad.Requests[0].Authorization);   // the key did go to the provider
        AssertNoKey();
    }

    [Fact]
    public async Task Check_reports_the_same_reason_as_the_chat_for_an_account_out_of_credits()
    {
        // OpenRouter's 402 shape (tests/fixtures/provider-failures.json), classified as the API's standalone chat does.
        using var server = new FakeLlmServer(errorFirst: 2, errorStatus: 402)
        {
            ErrorJson = """{"error":{"code":402,"message":"Insufficient credits. Add more using https://openrouter.ai/settings/credits","metadata":{}}}""",
        };
        var runner = Runner();
        Assert.Equal(0, runner.Add(new AddOptions("broke", "openai", "gpt-4o-mini", Endpoint: server.BaseUrl + "/openai/v1/", KeyEnv: _keyVariable, Default: true), json: true));

        Assert.Equal(1, await runner.CheckAsync("broke", vet: false, json: true, TestContext.Current.CancellationToken));
        var entry = LastJson()["profiles"]![0]!;
        Assert.Equal("credits", entry["reason"]!.GetValue<string>());
        Assert.Equal("Your model provider account is out of credits — top it up, or run `setup` to switch the chat to another model.", entry["fixIt"]!.GetValue<string>());
        Assert.Single(server.Requests);   // not retried

        Assert.Equal(1, await runner.CheckAsync("broke", vet: false, json: false, TestContext.Current.CancellationToken));
        Assert.Contains("credits:", _console.Output);
        AssertNoKey();
    }

    [Fact]
    public void Redact_replaces_every_stored_secret_and_extra_values()
    {
        var values = new Dictionary<string, string> { ["Llm:Profiles:a:ApiKey"] = Key, ["Llm:Profiles:a:Model"] = "gpt-4o-mini" };
        Assert.Equal("key [redacted], env [redacted], model gpt-4o-mini",
            SetupService.Redact($"key {Key}, env other-secret-value, model gpt-4o-mini", values, "other-secret-value"));
    }

    /// <summary>Standard input that fails the test if anything reads it (a prompt in a non-interactive run would).</summary>
    private sealed class NoInput : TextReader
    {
        public override int Read() => throw new InvalidOperationException("standard input was read");
        public override string ReadToEnd() => throw new InvalidOperationException("standard input was read");
        public override string? ReadLine() => throw new InvalidOperationException("standard input was read");
    }

    [Fact]
    public async Task An_option_the_parser_rejects_on_a_setup_command_exits_2_like_any_bad_input()
    {
        // Spectre's parser keeps an unknown option aside (the command would run without it) and answers -1 for a missing
        // value; setup's documented code for bad input is 2 for both, and nothing runs (no store is read or written).
        Assert.Equal(SetupRunner.BadInput, await TemporalGraph.Cli.CliApp.RunAsync(["setup", "status", "--no-such-option"]));
        Assert.Equal(SetupRunner.BadInput, await TemporalGraph.Cli.CliApp.RunAsync(["setup", "reset", "--profile"]));   // a missing value
        Assert.Equal(SetupRunner.BadInput, await TemporalGraph.Cli.CliApp.RunAsync(["setup", "reset", "--yes", "--jsn"]));   // a misspelt option
        Assert.Equal(SetupRunner.BadInput, await TemporalGraph.Cli.CliApp.RunAsync(["runs", "diff"]));                       // Every command
        Assert.Equal(0, TemporalGraph.Cli.CliApp.ExitCode(0, ["setup"]));
        Assert.Equal(3, TemporalGraph.Cli.CliApp.ExitCode(3, ["setup", "status"]));
    }
}
