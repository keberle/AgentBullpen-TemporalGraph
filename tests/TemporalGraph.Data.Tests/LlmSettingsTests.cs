using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using TemporalGraph.Cli;
using TemporalGraph.Llm;

namespace TemporalGraph.Data.Tests;

// Config validation, plus client + agent construction against FakeLlmServer. No real network.
public class LlmSettingsTests
{
    private const string FakeKey = "not-a-real-key-123";

    private static LlmSettings FromValues(Dictionary<string, string?> values) =>
        LlmSettings.From(new ConfigurationBuilder().AddInMemoryCollection(values).Build());

    [Fact]
    public void Missing_keys_are_reported_by_name()
    {
        Assert.Equal(
            ["Llm:Type is not set.", "Llm:Model is not set.", "Llm:ApiKey is not set."],
            FromValues([]).Validate());
    }

    [Theory]
    [InlineData("OpenAI")]
    [InlineData("Anthropic")]
    public void Only_missing_keys_are_reported_and_no_values_are_echoed(string type)
    {
        var problems = FromValues(new() { ["Llm:Type"] = type, ["Llm:ApiKey"] = FakeKey }).Validate();

        Assert.Equal(["Llm:Model is not set."], problems);
        Assert.DoesNotContain(problems, p => p.Contains(FakeKey));
    }

    [Theory]
    [InlineData("OpenAI", null)]                              // api.openai.com
    [InlineData("OpenAI", "https://openrouter.ai/api/v1")]    // OpenRouter
    [InlineData("openai", "https://example.invalid/openai/v1/")] // Azure v1; type is case-insensitive
    [InlineData("Anthropic", null)]
    public void Complete_settings_are_valid(string type, string? endpoint)
    {
        var settings = FromValues(new() { ["Llm:Type"] = type, ["Llm:Model"] = "some-model", ["Llm:Endpoint"] = endpoint, ["Llm:ApiKey"] = FakeKey });

        Assert.Empty(settings.Validate());
    }

    [Fact]
    public void Placeholder_values_count_as_not_set()
    {
        var settings = FromValues(new()
        {
            ["Llm:Type"] = "<OpenAI | Anthropic>",
            ["Llm:Endpoint"] = "<optional for OpenAI>",
            ["Llm:Model"] = "  <model id or Azure deployment name>",
            ["Llm:ApiKey"] = "<key>",
        });

        Assert.Null(settings.Endpoint);
        Assert.Equal(["Llm:Type is not set.", "Llm:Model is not set.", "Llm:ApiKey is not set."], settings.Validate());
    }

    [Theory]
    [InlineData("cli.appsettings.json")]
    [InlineData("api.appsettings.json")]
    public void Committed_template_counts_as_not_configured(string template)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "templates", template);
        var config = new ConfigurationBuilder().AddJsonFile(path, optional: false).Build();

        // The template shows the shape but holds no key.
        Assert.NotNull(config["Llm:Type"]);
        Assert.NotNull(config["Llm:Endpoint"]);
        Assert.NotNull(config["Llm:Model"]);
        Assert.Null(config["Llm:ApiKey"]);

        var settings = LlmSettings.From(config);
        Assert.Equal(new LlmSettings(null, null, null, null), settings);
        Assert.Equal(["Llm:Type is not set.", "Llm:Model is not set.", "Llm:ApiKey is not set."], settings.Validate());
    }

    [Fact]
    public void Development_file_is_loaded_over_the_template_without_an_environment_setting()
    {
        var dir = Path.Combine(Path.GetTempPath(), "TemporalGraph.Data.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.Copy(Path.Combine(AppContext.BaseDirectory, "templates", "cli.appsettings.json"), Path.Combine(dir, "appsettings.json"));
            File.WriteAllText(Path.Combine(dir, "appsettings.Development.json"),
                """{ "Llm": { "Type": "Anthropic", "Model": "dev-model" } }""");

            var settings = LlmSettings.From(LlmSettings.LoadConfiguration(dir));

            Assert.Equal("Anthropic", settings.Type);
            Assert.Equal("dev-model", settings.Model);
            Assert.Null(settings.Endpoint); // template placeholder, not overridden
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Unsupported_type_is_rejected()
    {
        var settings = FromValues(new() { ["Llm:Type"] = "AzureOpenAI", ["Llm:Model"] = "some-model", ["Llm:ApiKey"] = FakeKey });

        var problem = Assert.Single(settings.Validate());
        Assert.Contains("'AzureOpenAI' is not supported", problem);
        Assert.Throws<InvalidOperationException>(() => ChatClientFactory.Create(settings));
    }

    [Fact]
    public void Endpoint_with_anthropic_is_rejected()
    {
        var settings = FromValues(new()
        {
            ["Llm:Type"] = "Anthropic", ["Llm:Model"] = "some-model", ["Llm:Endpoint"] = "https://example.invalid/", ["Llm:ApiKey"] = FakeKey,
        });

        var problem = Assert.Single(settings.Validate());
        Assert.Contains("Llm:Endpoint is only used with Llm:Type OpenAI", problem);
        Assert.DoesNotContain("example.invalid", problem);
        Assert.Throws<InvalidOperationException>(() => ChatClientFactory.Create(settings));
    }

    [Fact]
    public void Endpoint_must_be_an_absolute_url()
    {
        var settings = FromValues(new() { ["Llm:Type"] = "OpenAI", ["Llm:Model"] = "m", ["Llm:Endpoint"] = "openrouter.ai/api/v1", ["Llm:ApiKey"] = FakeKey });

        Assert.Equal(["Llm:Endpoint is not an absolute http(s) URL."], settings.Validate());
    }

    [Fact]
    public async Task OpenAI_type_builds_a_client_and_agent_that_run_against_the_fake_server()
    {
        using var server = new FakeLlmServer();
        var settings = FromValues(new()
        {
            ["Llm:Type"] = "OpenAI", ["Llm:Model"] = "my-deployment", ["Llm:Endpoint"] = server.BaseUrl + "/openai/v1/", ["Llm:ApiKey"] = FakeKey,
        });

        using var client = ChatClientFactory.Create(settings, maxRetries: 0);
        Assert.NotNull(client.GetService<OpenTelemetryChatClient>());
        var agent = client.AsAIAgent(name: "llm-check");

        var response = await agent.RunAsync(LlmCheckCommand.Prompt, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("ready", response.Text);
        Assert.Equal(FakeLlmServer.OpenAIModel, LlmCheckCommand.ModelId(response));
        Assert.Equal(11, response.Usage?.InputTokenCount);
        Assert.Equal(1, response.Usage?.OutputTokenCount);

        var request = Assert.Single(server.Requests);
        Assert.Equal("/openai/v1/chat/completions", request.Path); // Azure v1 shape: no deployments path, no api-version
        Assert.Equal($"Bearer {FakeKey}", request.Authorization);
        Assert.Contains("\"model\":\"my-deployment\"", request.Body);
        Assert.Contains(LlmCheckCommand.Prompt, request.Body);
    }

    [Fact]
    public async Task Anthropic_type_builds_a_client_and_agent_that_run_against_the_fake_server()
    {
        using var server = new FakeLlmServer();
        var settings = FromValues(new() { ["Llm:Type"] = "Anthropic", ["Llm:Model"] = "my-claude-model", ["Llm:ApiKey"] = FakeKey });

        using var client = ChatClientFactory.Create(settings, maxRetries: 0, anthropicBaseUrl: server.BaseUrl);
        Assert.NotNull(client.GetService<OpenTelemetryChatClient>());
        var agent = client.AsAIAgent(name: "llm-check");

        var response = await agent.RunAsync(LlmCheckCommand.Prompt, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("ready", response.Text);
        Assert.Equal(FakeLlmServer.AnthropicModel, LlmCheckCommand.ModelId(response));
        Assert.Equal(12, response.Usage?.InputTokenCount);
        Assert.Equal(1, response.Usage?.OutputTokenCount);

        var request = Assert.Single(server.Requests);
        Assert.Equal("/v1/messages", request.Path);
        Assert.Equal(FakeKey, request.XApiKey);
        Assert.Contains("\"model\":\"my-claude-model\"", request.Body);
    }

    // ---- Llm:PolicyId: Azure content filter selected per call with x-policy-id ----

    [Theory]
    [InlineData("temporalgraph-fiction", "temporalgraph-fiction")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public async Task Policy_id_is_sent_as_x_policy_id_only_when_configured(string? configured, string? expectedHeader)
    {
        using var server = new FakeLlmServer();
        var settings = FromValues(new()
        {
            ["Llm:Type"] = "OpenAI", ["Llm:Model"] = "my-deployment", ["Llm:ApiKey"] = FakeKey,
            ["Llm:Endpoint"] = server.BaseUrl + "/openai/v1/", ["Llm:PolicyId"] = configured,
        });

        Assert.Equal(expectedHeader, settings.PolicyId);
        Assert.Empty(settings.Validate());
        using var client = ChatClientFactory.Create(settings, maxRetries: 0);
        var response = await client.AsAIAgent(name: "llm-check").RunAsync(LlmCheckCommand.Prompt, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("ready", response.Text);
        var request = Assert.Single(server.Requests);
        Assert.Equal(expectedHeader, request.PolicyId);
        Assert.Equal($"Bearer {FakeKey}", request.Authorization);
    }

    [Fact]
    public void Policy_id_template_value_is_empty()
    {
        var template = new ConfigurationBuilder().AddJsonFile(Path.Combine(AppContext.BaseDirectory, "templates", "cli.appsettings.json")).Build();
        Assert.Equal("", template["Llm:PolicyId"]);
        Assert.Null(LlmSettings.From(template).PolicyId);
        var api = new ConfigurationBuilder().AddJsonFile(Path.Combine(AppContext.BaseDirectory, "templates", "api.appsettings.json")).Build();
        Assert.Equal("", api["Llm:PolicyId"]);
    }

    // ---- Named profiles, key fallback, reasoning_effort ----

    private static IConfiguration Values(Dictionary<string, string?> values) => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Fact]
    public void Profile_reads_its_own_section_and_falls_back_to_the_default_key_only()
    {
        var config = Values(new()
        {
            ["Llm:Type"] = "OpenAI", ["Llm:Model"] = "default-deployment", ["Llm:ApiKey"] = FakeKey, ["Llm:PolicyId"] = "default-policy",
            ["Llm:Profiles:luna:Type"] = "OpenAI", ["Llm:Profiles:luna:Model"] = "gpt-5.6-luna", ["Llm:Profiles:luna:PolicyId"] = "temporalgraph-fiction",
            ["Llm:Profiles:haiku:Type"] = "Anthropic", ["Llm:Profiles:haiku:Model"] = "claude-haiku-5-5", ["Llm:Profiles:haiku:ApiKey"] = "anthropic-key-456",
        });

        var dflt = LlmSettings.From(config);
        Assert.Equal((null, "Llm", "default-deployment", "default-policy"), (dflt.Profile, dflt.SectionPath, dflt.Model, dflt.PolicyId));

        var luna = LlmSettings.From(config, "luna");
        Assert.Equal(("luna", "Llm:Profiles:luna", "OpenAI", "gpt-5.6-luna", "temporalgraph-fiction"), (luna.Profile, luna.SectionPath, luna.Type, luna.Model, luna.PolicyId));
        Assert.Equal(FakeKey, luna.ApiKey);           // no profile key: Llm:ApiKey
        Assert.Null(luna.Endpoint);                   // nothing else is inherited
        Assert.Empty(luna.Validate());

        var haiku = LlmSettings.From(config, "haiku");
        Assert.Equal("anthropic-key-456", haiku.ApiKey); // the profile's own key wins
        Assert.True(haiku.IsAnthropic);
    }

    [Theory]
    [InlineData("Anthropic", null)]                                   // another provider: never the default (Azure) key
    [InlineData("OpenAI", "https://other.example/openai/v1/")]        // same provider, another endpoint
    public void Default_key_is_not_shared_with_another_provider_or_endpoint(string type, string? endpoint)
    {
        var config = Values(new()
        {
            ["Llm:Type"] = "OpenAI", ["Llm:Endpoint"] = "https://resource.example/openai/v1/", ["Llm:Model"] = "m", ["Llm:ApiKey"] = FakeKey,
            ["Llm:Profiles:p:Type"] = type, ["Llm:Profiles:p:Endpoint"] = endpoint, ["Llm:Profiles:p:Model"] = "x",
        });
        var p = LlmSettings.From(config, "p");
        Assert.Null(p.ApiKey);
        Assert.Contains(p.Validate(), m => m.StartsWith("Llm:Profiles:p:ApiKey"));
        Assert.Throws<InvalidOperationException>(() => ChatClientFactory.Create(p, maxRetries: 0));

        // Same type and endpoint (trailing slash and case ignored): shared.
        var same = Values(new()
        {
            ["Llm:Type"] = "OpenAI", ["Llm:Endpoint"] = "https://resource.example/openai/v1/", ["Llm:Model"] = "m", ["Llm:ApiKey"] = FakeKey,
            ["Llm:Profiles:q:Type"] = "openai", ["Llm:Profiles:q:Endpoint"] = "https://Resource.example/openai/v1", ["Llm:Profiles:q:Model"] = "gpt-5.4-mini",
        });
        Assert.Equal(FakeKey, LlmSettings.From(same, "q").ApiKey);
    }

    [Fact]
    public void Unconfigured_profile_names_its_missing_keys_and_no_values()
    {
        var config = Values(new() { ["Llm:Type"] = "OpenAI", ["Llm:Model"] = "m", ["Llm:ApiKey"] = FakeKey });

        Assert.Equal(["Llm:Profiles:gpt54mini:Type is not set.", "Llm:Profiles:gpt54mini:Model is not set.",
            "Llm:Profiles:gpt54mini:ApiKey (Llm:ApiKey is shared only with profiles of the same Type and Endpoint) is not set."], LlmSettings.From(config, "gpt54mini").Validate());
        Assert.Contains("Llm:Profiles:x:ApiKey (Llm:ApiKey is shared only with profiles of the same Type and Endpoint) is not set.", LlmSettings.From(Values(new() { ["Llm:Profiles:x:Type"] = "OpenAI", ["Llm:Profiles:x:Model"] = "m" }), "x").Validate());
        Assert.Contains("not a valid profile name", Assert.Single(LlmSettings.From(config, "a:b").Validate()));
    }

    [Theory]
    [InlineData("gpt41mini")]
    [InlineData("gpt54mini")]
    [InlineData("gpt56luna")]
    [InlineData("haiku55")]
    public void Committed_template_profiles_count_as_not_configured(string profile)
    {
        var template = new ConfigurationBuilder().AddJsonFile(Path.Combine(AppContext.BaseDirectory, "templates", "cli.appsettings.json")).Build();
        var problems = LlmSettings.From(template, profile).Validate();
        Assert.Equal([$"Llm:Profiles:{profile}:Type is not set.", $"Llm:Profiles:{profile}:Model is not set.", $"Llm:Profiles:{profile}:ApiKey (Llm:ApiKey is shared only with profiles of the same Type and Endpoint) is not set."], problems);
        Assert.Equal(profile == "gpt41mini" ? 0.0 : null, LlmSampling.From(template, LlmSettings.From(template, profile)).Temperature);
    }

    [Theory]
    [InlineData("none", "\"reasoning_effort\":\"none\"")]
    [InlineData("minimal", "\"reasoning_effort\":\"minimal\"")]
    [InlineData("low", "\"reasoning_effort\":\"low\"")]
    [InlineData(null, null)]
    public async Task Reasoning_effort_is_sent_as_reasoning_effort_and_an_empty_temperature_is_not_sent(string? effort, string? expected)
    {
        using var server = new FakeLlmServer();
        var config = Values(new()
        {
            ["Llm:Type"] = "OpenAI", ["Llm:Endpoint"] = server.BaseUrl + "/openai/v1/", ["Llm:ApiKey"] = FakeKey, // same resource: key shared
            ["Llm:Profiles:r:Type"] = "OpenAI", ["Llm:Profiles:r:Model"] = "gpt-5.4-mini", ["Llm:Profiles:r:Endpoint"] = server.BaseUrl + "/openai/v1/",
            ["Llm:Profiles:r:Temperature"] = "", ["Llm:Profiles:r:Seed"] = "20261007", ["Llm:Profiles:r:ReasoningEffort"] = effort,
        });
        var llm = LlmSettings.From(config, "r");
        var sampling = LlmSampling.From(config, llm);
        Assert.Equal((null, 20261007L, effort), (sampling.Temperature, sampling.Seed, sampling.ReasoningEffort));

        using var client = ChatClientFactory.Create(llm, maxRetries: 0, new LlmPipelineOptions(Retries: 0, Sampling: sampling));
        await client.AsAIAgent(name: "llm-check").RunAsync(LlmCheckCommand.Prompt, cancellationToken: TestContext.Current.CancellationToken);

        var body = Assert.Single(server.Requests).Body.Replace(" ", "");
        if (expected is null) Assert.DoesNotContain("reasoning_effort", body);
        else Assert.Contains(expected, body);
        Assert.DoesNotContain("\"temperature\"", body);
        Assert.Contains("\"seed\":20261007", body);
        Assert.Contains("\"model\":\"gpt-5.4-mini\"", body);
    }

    [Fact]
    public void Reasoning_effort_is_not_used_for_anthropic()
    {
        var config = Values(new() { ["Llm:Profiles:h:Type"] = "Anthropic", ["Llm:Profiles:h:Model"] = "m", ["Llm:Profiles:h:ApiKey"] = FakeKey, ["Llm:Profiles:h:ReasoningEffort"] = "low" });
        Assert.Null(LlmSampling.From(config, LlmSettings.From(config, "h")).ReasoningEffort);
    }

    [Theory]
    [InlineData("16000", 16000)]
    [InlineData("", null)]
    public async Task Max_output_tokens_is_sent_as_max_tokens_to_anthropic(string configured, int? expected)
    {
        // Without it the Anthropic SDK's chat client uses its own small default, which Haiku 5.5's thinking used up.
        using var server = new FakeLlmServer();
        var config = Values(new() { ["Llm:Profiles:h:Type"] = "Anthropic", ["Llm:Profiles:h:Model"] = "claude-haiku-5-5", ["Llm:Profiles:h:ApiKey"] = FakeKey, ["Llm:Profiles:h:MaxOutputTokens"] = configured });
        var llm = LlmSettings.From(config, "h");
        var sampling = LlmSampling.From(config, llm);
        Assert.Equal(expected, sampling.MaxOutputTokens);

        using var client = ChatClientFactory.Create(llm, maxRetries: 0, anthropicBaseUrl: server.BaseUrl, new LlmPipelineOptions(Retries: 0, Sampling: sampling));
        await client.AsAIAgent(name: "llm-check").RunAsync(LlmCheckCommand.Prompt, cancellationToken: TestContext.Current.CancellationToken);

        var body = Assert.Single(server.Requests).Body.Replace(" ", "");
        if (expected is { } n) Assert.Contains($"\"max_tokens\":{n}", body);
        else Assert.DoesNotContain("\"max_tokens\":16000", body);
    }

    [Fact]
    public async Task Max_output_tokens_is_sent_to_openai()
    {
        using var server = new FakeLlmServer();
        var config = Values(new()
        {
            ["Llm:Type"] = "OpenAI", ["Llm:Endpoint"] = server.BaseUrl + "/openai/v1/", ["Llm:ApiKey"] = FakeKey,
            ["Llm:Profiles:o:Type"] = "OpenAI", ["Llm:Profiles:o:Endpoint"] = server.BaseUrl + "/openai/v1/", ["Llm:Profiles:o:Model"] = "m", ["Llm:Profiles:o:MaxOutputTokens"] = "4000",
        });
        var llm = LlmSettings.From(config, "o");
        using var client = ChatClientFactory.Create(llm, maxRetries: 0, new LlmPipelineOptions(Retries: 0, Sampling: LlmSampling.From(config, llm)));
        await client.AsAIAgent(name: "llm-check").RunAsync(LlmCheckCommand.Prompt, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("\"max_completion_tokens\":4000", Assert.Single(server.Requests).Body.Replace(" ", ""));
    }
}
