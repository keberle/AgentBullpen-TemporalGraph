using Microsoft.Extensions.Configuration;

namespace TemporalGraph.Llm;

/// <summary>
/// The <c>Llm</c> config section: user-secrets (dev) and environment variables (<c>Llm__Type</c>, …).
/// <list type="bullet">
/// <item><c>Llm:Type</c> — <c>OpenAI</c> (OpenAI, OpenRouter, Azure: OpenAI SDK) or <c>Anthropic</c> (Anthropic SDK).</item>
/// <item><c>Llm:Model</c> — model id, or the Azure deployment name.</item>
/// <item><c>Llm:Endpoint</c> — optional, OpenAI only. Empty: api.openai.com. OpenRouter: https://openrouter.ai/api/v1.
/// Azure: https://&lt;resource&gt;/openai/v1/.</item>
/// <item><c>Llm:ApiKey</c> — never log or print it.</item>
/// <item><c>Llm:PolicyId</c> — optional, Azure only: a custom content filter (RAI policy) name, sent as the
/// <c>x-policy-id</c> header on every request. Empty: no header. Ignored for Anthropic; not checked against the endpoint.</item>
/// <item><c>Llm:TimeoutSeconds</c> — optional: the network timeout of one model call, for every profile unless the profile
/// has its own <c>TimeoutSeconds</c>. Empty: each SDK's default (OpenAI 100 s, Anthropic 600 s).</item>
/// <item><c>Llm:Temperature</c>, <c>Llm:Seed</c>, <c>Llm:ReasoningEffort</c>, <c>Llm:MaxOutputTokens</c> — see <see cref="LlmSampling"/>.</item>
/// </list>
/// <para>Named profiles: <c>Llm:Profiles:&lt;name&gt;</c> sections with the same keys, chosen with <c>--llm &lt;name&gt;</c>.
/// Only the key falls back: <c>Llm:Profiles:&lt;name&gt;:ApiKey</c>, else <c>Llm:ApiKey</c> when the profile has the same Type and
/// Endpoint as <c>Llm</c> (the Azure deployments of one resource share it; another provider never gets the default key). Without <c>--llm</c> the <c>Llm</c> section itself is used, as before.</para>
/// </summary>
/// <param name="Profile">The profile name (<c>--llm</c>), or null for the <c>Llm</c> section.</param>
public sealed record LlmSettings(string? Type, string? Model, string? Endpoint, string? ApiKey, string? PolicyId = null, string? Profile = null)
{
    public const string Section = "Llm";

    /// <summary>The network timeout of one model call (<c>TimeoutSeconds</c> in the profile's section, else <c>Llm:TimeoutSeconds</c>);
    /// null: the SDK's default.</summary>
    public int? TimeoutSeconds { get; init; }

    /// <summary>The SDKs' own network timeouts, used when <see cref="TimeoutSeconds"/> is not set.</summary>
    public const int DefaultOpenAITimeoutSeconds = 100, DefaultAnthropicTimeoutSeconds = 600;

    /// <summary>The user-secrets store of the CLI and the API (both csproj files name it): one <c>Llm:ApiKey</c> serves both.</summary>
    public const string UserSecretsId = "temporalgraph-cli";
    public const string ProfilesSection = "Llm:Profiles";

    /// <summary>The config section this profile reads: <c>Llm</c> or <c>Llm:Profiles:&lt;name&gt;</c>.</summary>
    public string SectionPath => SectionFor(Profile);

    public static string SectionFor(string? profile) => profile is null ? Section : $"{ProfilesSection}:{profile}";

    public const string OpenAI = "OpenAI";
    public const string Anthropic = "Anthropic";

    /// <param name="profile">A profile name (<c>--llm</c>); null or empty: the <c>Llm</c> section.</param>
    public static LlmSettings From(IConfiguration config, string? profile = null)
    {
        profile = string.IsNullOrWhiteSpace(profile) ? null : profile.Trim();
        var s = config.GetSection(SectionFor(profile));
        var key = Value(s["ApiKey"]) ?? (profile is not null && SharesDefaultKey(config, s) ? Value(config[$"{Section}:ApiKey"]) : null);
        static int? Seconds(string? v) => int.TryParse(Value(v), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var n) && n > 0 ? n : null;
        return new LlmSettings(Value(s["Type"]), Value(s["Model"]), Value(s["Endpoint"]), key, Value(s["PolicyId"]), profile)
        {
            TimeoutSeconds = Seconds(s["TimeoutSeconds"]) ?? Seconds(config[$"{Section}:TimeoutSeconds"]),
        };
    }

    /// <summary>
    /// A profile without its own key may use <c>Llm:ApiKey</c> only when it talks to the same provider and endpoint as the
    /// <c>Llm</c> section (the Azure deployments of one resource). Otherwise the default key would be sent to another
    /// provider (an Anthropic profile without a key must not receive the Azure key).
    /// </summary>
    private static bool SharesDefaultKey(IConfiguration config, IConfigurationSection profile)
    {
        var d = config.GetSection(Section);
        static string Norm(string? v) => (Value(v) ?? "").TrimEnd('/').ToLowerInvariant();
        return Norm(profile["Type"]).Length > 0 && Norm(profile["Type"]) == Norm(d["Type"]) && Norm(profile["Endpoint"]) == Norm(d["Endpoint"]);
    }

    /// <summary>A profile name may use letters, digits, '-' and '_'.</summary>
    public static bool IsValidProfileName(string name) => name.Length > 0 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    /// <summary>
    /// A template placeholder (any value starting with <c>&lt;</c>, as in the committed appsettings.json) counts as not set.
    /// </summary>
    private static string? Value(string? raw)
    {
        var v = raw?.Trim();
        return string.IsNullOrEmpty(v) || v.StartsWith('<') ? null : v;
    }

    /// <summary>
    /// Later sources win: appsettings.json (committed template) → appsettings.Development.json (gitignored, loaded
    /// whenever present, without needing DOTNET_ENVIRONMENT) → user-secrets (the key) → environment variables.
    /// Both json files are read from <paramref name="baseDirectory"/> (default: <see cref="AppContext.BaseDirectory"/>,
    /// where the build copies them), so the CLI works from any folder. Read only when a command needs the LLM.
    /// </summary>
    /// <param name="userSecretsId">The user-secrets store; tests of <c>setup</c> pass a temporary one.</param>
    public static IConfiguration LoadConfiguration(string? baseDirectory = null, string userSecretsId = UserSecretsId) =>
        new ConfigurationBuilder()
            .SetBasePath(baseDirectory ?? AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .AddUserSecrets(userSecretsId)
            .AddEnvironmentVariables()
            .Build();

    public bool IsOpenAI => string.Equals(Type, OpenAI, StringComparison.OrdinalIgnoreCase);
    public bool IsAnthropic => string.Equals(Type, Anthropic, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Problems that stop a chat client from being built: missing keys (by name only), an unsupported
    /// <c>Llm:Type</c>, an endpoint with <c>Type=Anthropic</c>, or an endpoint that isn't an absolute http(s) URL.
    /// Empty when the settings are usable. Messages never include setting values except <c>Llm:Type</c>.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var section = SectionPath;
        if (Profile is not null && !IsValidProfileName(Profile))
            return [$"--llm '{Profile}' is not a valid profile name (letters, digits, '-' and '_')."];
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(Type)) missing.Add($"{section}:Type");
        if (string.IsNullOrWhiteSpace(Model)) missing.Add($"{section}:Model");
        if (string.IsNullOrWhiteSpace(ApiKey)) missing.Add(Profile is null ? $"{Section}:ApiKey" : $"{section}:ApiKey (Llm:ApiKey is shared only with profiles of the same Type and Endpoint)");

        var problems = missing.Select(k => $"{k} is not set.").ToList();

        if (!string.IsNullOrWhiteSpace(Type) && !IsOpenAI && !IsAnthropic)
            problems.Add($"{section}:Type '{Type}' is not supported. Supported: {OpenAI}, {Anthropic}.");

        if (!string.IsNullOrWhiteSpace(Endpoint))
        {
            if (IsAnthropic)
                problems.Add($"{section}:Endpoint is only used with {section}:Type {OpenAI}. Remove it for {Anthropic}.");
            else if (!(Uri.TryCreate(Endpoint, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)))
                problems.Add($"{section}:Endpoint is not an absolute http(s) URL.");
        }

        return problems;
    }
}
