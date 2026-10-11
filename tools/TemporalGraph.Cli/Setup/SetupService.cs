using Microsoft.Extensions.Configuration;
using TemporalGraph.Chat;
using TemporalGraph.Llm;

namespace TemporalGraph.Cli.Setup;

/// <param name="Name">What <c>--provider</c> takes.</param>
/// <param name="Type"><c>Llm:Profiles:&lt;name&gt;:Type</c>.</param>
/// <param name="Endpoint">The endpoint written when <c>--endpoint</c> is not given; null = none (the SDK's default).</param>
/// <param name="KeyVariable">The environment variable suggested for <c>--key-env</c>.</param>
/// <param name="MaxOutputTokens">Written for Anthropic, as the README's manual block does (a thinking model needs room).</param>
/// <param name="Needs">What the walkthrough says you need for this provider, after its name.</param>
/// <param name="Shortlist">The models the walkthrough offers, default first: ones that passed <c>chat vet</c> in this repo.</param>
public sealed record ProviderInfo(string Name, string Label, string Type, string? Endpoint, bool NeedsEndpoint, string? DefaultModel, string KeyVariable,
    int? MaxOutputTokens = null, string Needs = "", string[]? Shortlist = null);

public static class Providers
{
    public static readonly ProviderInfo[] All =
    [
        // Shortlists: gpt-4.1-mini passed chat vet as the Azure deployment; the OpenRouter three and Claude Haiku 5.5 passed it
        // through OpenRouter; gpt-4o-mini there too.
        new("openai", "OpenAI", LlmSettings.OpenAI, null, false, "gpt-4o-mini", "OPENAI_API_KEY",
            Needs: "an API key from platform.openai.com", Shortlist: ["gpt-4o-mini", "gpt-4.1-mini"]),
        new("anthropic", "Anthropic", LlmSettings.Anthropic, null, false, "claude-haiku-5-5", "ANTHROPIC_API_KEY", 16000,
            Needs: "an API key from console.anthropic.com", Shortlist: ["claude-haiku-5-5"]),
        new("azure", "Azure OpenAI", LlmSettings.OpenAI, null, true, null, "AZURE_OPENAI_API_KEY",
            Needs: "your resource's endpoint, a deployment name and its key (Azure portal)"),
        new("openrouter", "OpenRouter", LlmSettings.OpenAI, "https://openrouter.ai/api/v1", false, "openai/gpt-4o-mini", "OPENROUTER_API_KEY",
            Needs: "an API key from openrouter.ai (one key, many providers' models)",
            Shortlist: ["openai/gpt-4o-mini", "anthropic/claude-haiku-5.5", "openai/gpt-6-luna"]),
    ];

    public static string Names => string.Join("|", All.Select(p => p.Name));

    public static ProviderInfo? Find(string? name) => All.FirstOrDefault(p => string.Equals(p.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Which provider a profile talks to, from its type and endpoint, without showing the endpoint.</summary>
    public static string? Classify(string? type, string? endpoint)
    {
        if (string.Equals(type, LlmSettings.Anthropic, StringComparison.OrdinalIgnoreCase)) return "anthropic";
        if (!string.Equals(type, LlmSettings.OpenAI, StringComparison.OrdinalIgnoreCase)) return null;
        if (endpoint is null) return "openai";
        var host = Uri.TryCreate(endpoint, UriKind.Absolute, out var u) ? u.Host.ToLowerInvariant() : "";
        return host == "openrouter.ai" ? "openrouter" : host.EndsWith(".azure.com", StringComparison.Ordinal) ? "azure" : "custom";
    }
}

/// <summary>Bad input to a setup command: exit code 2. The message never contains a key.</summary>
public sealed class SetupInputException(string message) : Exception(message);

public sealed record AddRequest(string Profile, string Provider, string Model, string? Endpoint, string Key, bool Default = false, bool Pickable = false,
    bool Indexing = false);

/// <param name="Written">The key names this add wrote (values are never reported).</param>
public sealed record AddResult(string Profile, string Provider, string Model, bool Default, bool Pickable, bool Indexing, IReadOnlyList<string> Written);

/// <param name="Removed">Key names removed from the store.</param>
/// <param name="Restored">Key names put back to the value they had before setup changed them.</param>
public sealed record ResetResult(IReadOnlyList<string> Removed, IReadOnlyList<string> Restored);

public sealed record ProfileStatus(string Name, string? Provider, string? Type, string Endpoint, string? Model, string Key, bool Complete, bool WrittenBySetup);

/// <param name="Profile">The profile <c>Chat:Llm</c> names; null = the <c>Llm</c> section.</param>
/// <param name="Problems">What is missing, as key names (from <see cref="LlmSettings.Validate"/>).</param>
/// <param name="Models"><c>Chat:Models</c>: the extra profiles the API's picker offers.</param>
public sealed record ChatStatus(string? Profile, string? Model, bool Ready, IReadOnlyList<string> Problems, IReadOnlyList<string> Models);

public sealed record IndexingStatus(string? Profile, string? Model, bool Ready);

public sealed record GatewayStatus(bool Enabled);

/// <summary><c>setup status --json</c>. Stable field names; never a key value or an endpoint.</summary>
public sealed record StatusReport(string Store, bool Ready, IReadOnlyList<string> Missing, IReadOnlyList<Prerequisite> Prerequisites,
    IReadOnlyList<ProfileStatus> Profiles, ChatStatus Chat, IndexingStatus Indexing, GatewayStatus Gateway, IReadOnlyList<string> WrittenBySetup);

/// <summary>
/// What <c>setup</c> does to the user-secrets store. It records every key it writes in <c>Setup:Written</c> (one value, keys
/// separated by <c>;</c>), and before it changes a shared key it didn't write (<c>Chat:Llm</c>, <c>Chat:Models</c>, <c>Indexing:Llm</c>)
/// it saves the old value under <c>Setup:Previous:&lt;key&gt;</c>. <c>reset</c> removes exactly those keys and puts the old values back,
/// so add then reset leaves the store as it was. A profile it didn't create is never overwritten.
/// </summary>
public sealed class SetupService(ISecretsStore store)
{
    public const string WrittenKey = "Setup:Written";
    public const string PreviousPrefix = "Setup:Previous:";
    public const string ChatLlm = "Chat:Llm";
    public const string ChatModels = "Chat:Models";
    public const string IndexingLlm = "Indexing:Llm";

    private static readonly StringComparer Keys = StringComparer.OrdinalIgnoreCase;

    public ISecretsStore Store => store;

    public static string ProfilePrefix(string profile) => $"{LlmSettings.ProfilesSection}:{profile}:";

    public static List<string> WrittenKeys(IReadOnlyDictionary<string, string> values) =>
        values.TryGetValue(WrittenKey, out var v) ? v.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(Keys).ToList() : [];

    /// <summary>The profiles setup created, in the order of <c>Setup:Written</c>.</summary>
    public static List<string> WrittenProfiles(IReadOnlyDictionary<string, string> values) =>
        WrittenKeys(values).Where(k => k.StartsWith(LlmSettings.ProfilesSection + ":", StringComparison.OrdinalIgnoreCase))
            .Select(k => k.Split(':')[2]).Distinct(Keys).ToList();

    public static List<string> ParseList(string? value) =>
        (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(v => !v.StartsWith('<')).ToList();

    /// <summary>Throws <see cref="SetupInputException"/> when the request can't be applied as given.</summary>
    public void Validate(AddRequest r)
    {
        if (!LlmSettings.IsValidProfileName(r.Profile))
            throw new SetupInputException($"'{r.Profile}' is not a valid profile name: use letters, digits, '-' and '_'.");
        var provider = Providers.Find(r.Provider) ?? throw new SetupInputException($"Unknown provider '{r.Provider}': use {Providers.Names}.");
        if (string.IsNullOrWhiteSpace(r.Model) || r.Model.Trim().StartsWith('<'))
            throw new SetupInputException(provider.Name == "azure" ? "--model is required: your Azure deployment's name." : "--model is required.");
        if (provider.NeedsEndpoint && r.Endpoint is null)
            throw new SetupInputException("--endpoint is required for Azure: https://<resource>.openai.azure.com/openai/v1/");
        if (r.Endpoint is not null && provider.Type == LlmSettings.Anthropic)
            throw new SetupInputException("--endpoint is not used with Anthropic.");
        if (r.Endpoint is not null && !(Uri.TryCreate(r.Endpoint.Trim(), UriKind.Absolute, out var u) && u.Scheme is "https" or "http"))
            throw new SetupInputException("--endpoint must be an absolute http(s) URL.");
        if (string.IsNullOrWhiteSpace(r.Key)) throw new SetupInputException("The API key is empty.");

        var values = store.Read();
        var written = WrittenKeys(values).ToHashSet(Keys);
        var prefix = ProfilePrefix(r.Profile);
        if (values.Keys.Any(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !written.Contains(k)))
            throw new SetupInputException($"A profile named '{r.Profile}' already exists and setup didn't create it. Pick another name (setup never changes settings it didn't write).");
        if (r.Pickable && values.Keys.Any(k => k.StartsWith(ChatModels + ":", StringComparison.OrdinalIgnoreCase) && !written.Contains(k)))
            throw new SetupInputException("Chat:Models is set as a list in your user-secrets; add the profile to it yourself, or remove the list and rerun with --pickable.");
    }

    public AddResult Add(AddRequest r)
    {
        Validate(r);
        var provider = Providers.Find(r.Provider)!;
        var values = store.Read();
        var written = WrittenKeys(values);
        var prefix = ProfilePrefix(r.Profile);
        var set = ProfileKeys(r);

        // A key setup didn't write: keep its value to give back on reset (the shared keys only; never a key or a profile).
        void Claim(string key, string value)
        {
            if (values.TryGetValue(key, out var old) && !written.Contains(key, Keys) && !values.ContainsKey(PreviousPrefix + key))
                set[PreviousPrefix + key] = old;
            set[key] = value;
        }
        if (r.Default) Claim(ChatLlm, r.Profile);
        if (r.Indexing) Claim(IndexingLlm, r.Profile);
        if (r.Pickable)
        {
            var list = ParseList(values.GetValueOrDefault(ChatModels));
            if (!list.Contains(r.Profile, Keys)) list.Add(r.Profile);
            Claim(ChatModels, string.Join(",", list));
        }

        // Keys an earlier setup of this profile wrote that this one doesn't (another provider's Endpoint, say).
        var stale = written.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !set.ContainsKey(k)).ToList();
        var mine = set.Keys.Where(k => !k.StartsWith(PreviousPrefix, StringComparison.OrdinalIgnoreCase)).ToList();
        set[WrittenKey] = string.Join(";", written.Except(stale, Keys).Concat(mine).Distinct(Keys));

        store.Set(set);
        if (stale.Count > 0) store.Remove(stale);
        return new AddResult(r.Profile, provider.Name, r.Model.Trim(), r.Default, r.Pickable, r.Indexing, mine.Order(Keys).ToList());
    }

    /// <summary>The profile's own keys for this request (<c>Llm:Profiles:&lt;name&gt;:*</c>), as <see cref="Add"/> writes them.</summary>
    public static Dictionary<string, string> ProfileKeys(AddRequest r)
    {
        var provider = Providers.Find(r.Provider) ?? throw new SetupInputException($"Unknown provider '{r.Provider}': use {Providers.Names}.");
        var prefix = ProfilePrefix(r.Profile);
        var set = new Dictionary<string, string>(Keys)
        {
            [prefix + "Type"] = provider.Type,
            [prefix + "Model"] = r.Model.Trim(),
            [prefix + "ApiKey"] = r.Key.Trim(),
        };
        if ((r.Endpoint?.Trim() ?? provider.Endpoint) is { } endpoint) set[prefix + "Endpoint"] = endpoint;
        if (provider.MaxOutputTokens is { } max) set[prefix + "MaxOutputTokens"] = max.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return set;
    }

    /// <summary>The settings shared with the user (<c>Chat:Llm</c>, <c>Chat:Models</c>, <c>Indexing:Llm</c>), which setup may change.</summary>
    public static readonly string[] SharedKeys = [ChatLlm, ChatModels, IndexingLlm];

    /// <summary>
    /// Sets shared keys (null or empty = clear) with the same bookkeeping as <see cref="Add"/>: a value setup didn't write is kept
    /// under <c>Setup:Previous:&lt;key&gt;</c> before the first change, so <see cref="Reset"/> gives it back. Clearing a key setup wrote
    /// removes it when there is nothing to give back, else sets it empty (reset still restores the user's value).
    /// </summary>
    public void SetShared(IReadOnlyDictionary<string, string?> changes)
    {
        var values = store.Read();
        var written = WrittenKeys(values);
        var set = new Dictionary<string, string>(Keys);
        var remove = new List<string>();
        foreach (var (key, raw) in changes)
        {
            if (!SharedKeys.Contains(key, Keys)) throw new ArgumentException($"{key} is not a shared setting.", nameof(changes));
            if (key == ChatModels && values.Keys.Any(k => k.StartsWith(ChatModels + ":", StringComparison.OrdinalIgnoreCase) && !written.Contains(k, Keys)))
                throw new SetupInputException("Chat:Models is set as a list in your user-secrets; change it yourself, or remove the list first.");
            var value = raw?.Trim() ?? "";
            var current = values.GetValueOrDefault(key);
            if (string.Equals(current ?? "", value, StringComparison.Ordinal)) continue;
            var mine = written.Contains(key, Keys);
            if (value.Length == 0 && mine && !values.ContainsKey(PreviousPrefix + key))
            {
                remove.Add(key);
                written.RemoveAll(k => Keys.Equals(k, key));
                continue;
            }
            if (!mine)
            {
                if (current is not null && !values.ContainsKey(PreviousPrefix + key)) set[PreviousPrefix + key] = current;
                written.Add(key);
            }
            set[key] = value;
        }
        if (set.Count == 0 && remove.Count == 0) return;
        if (written.Count > 0) set[WrittenKey] = string.Join(";", written);
        else if (values.ContainsKey(WrittenKey)) remove.Add(WrittenKey);
        if (set.Count > 0) store.Set(set);
        if (remove.Count > 0) store.Remove(remove);
    }

    /// <summary>What <see cref="Reset"/> would do, without doing it.</summary>
    public ResetResult PlanReset(string? profile) => Reset(profile, apply: false);

    public ResetResult Reset(string? profile) => Reset(profile, apply: true);

    private ResetResult Reset(string? profile, bool apply)
    {
        var values = store.Read();
        var written = WrittenKeys(values);
        var remove = new List<string>();
        var set = new Dictionary<string, string>(Keys);
        var restored = new List<string>();

        // Give a key back: its old value if setup saved one, else remove it.
        void Release(string key)
        {
            if (values.TryGetValue(PreviousPrefix + key, out var old))
            {
                set[key] = old;
                remove.Add(PreviousPrefix + key);
                restored.Add(key);
            }
            else remove.Add(key);
        }

        if (profile is null)
        {
            foreach (var key in written) Release(key);
            remove.AddRange(values.Keys.Where(k => k.StartsWith(PreviousPrefix, StringComparison.OrdinalIgnoreCase)));
            remove.Add(WrittenKey);
        }
        else
        {
            var prefix = ProfilePrefix(profile);
            var mine = written.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
            if (mine.Count == 0) throw new SetupInputException($"Setup didn't create a profile named '{profile}'; nothing to reset.");
            remove.AddRange(mine);
            var keep = written.Except(mine, Keys).ToList();
            foreach (var key in new[] { ChatLlm, IndexingLlm })
                if (keep.Contains(key, Keys) && string.Equals(values.GetValueOrDefault(key), profile, StringComparison.OrdinalIgnoreCase))
                {
                    Release(key);
                    keep.RemoveAll(k => Keys.Equals(k, key));
                }
            if (keep.Contains(ChatModels, Keys))
            {
                var list = ParseList(values.GetValueOrDefault(ChatModels));
                if (list.RemoveAll(m => Keys.Equals(m, profile)) > 0)
                {
                    var before = values.TryGetValue(PreviousPrefix + ChatModels, out var old) ? ParseList(old) : null;
                    if (list.Count == 0 || (before is not null && list.SequenceEqual(before, Keys)))
                    {
                        Release(ChatModels);
                        keep.RemoveAll(k => Keys.Equals(k, ChatModels));
                    }
                    else set[ChatModels] = string.Join(",", list);
                }
            }
            if (keep.Count == 0) remove.Add(WrittenKey);
            else set[WrittenKey] = string.Join(";", keep);
        }

        var removed = remove.Where(values.ContainsKey).Distinct(Keys).Where(k => !set.ContainsKey(k)).ToList();
        if (apply)
        {
            if (set.Count > 0) store.Set(set);
            if (removed.Count > 0) store.Remove(removed);
        }
        return new ResetResult(removed.Where(k => !Keys.Equals(k, WrittenKey) && !k.StartsWith(PreviousPrefix, StringComparison.OrdinalIgnoreCase)).ToList(), restored);
    }

    /// <summary>Replaces every secret value in the store (and <paramref name="extra"/>) found in <paramref name="text"/> with [redacted].</summary>
    public static string Redact(string text, IReadOnlyDictionary<string, string> values, params string?[] extra)
    {
        var secrets = values.Where(p => IsSecretKey(p.Key)).Select(p => p.Value).Concat(extra.OfType<string>())
            .Where(v => v.Trim().Length >= 6).Select(v => v.Trim()).Distinct().OrderByDescending(v => v.Length);
        foreach (var s in secrets) text = text.Replace(s, "[redacted]", StringComparison.Ordinal);
        return text;
    }

    public static bool IsSecretKey(string key) =>
        key.EndsWith("ApiKey", StringComparison.OrdinalIgnoreCase) || key.EndsWith("SigningKey", StringComparison.OrdinalIgnoreCase)
        || key.Contains("Secret", StringComparison.OrdinalIgnoreCase) || key.Contains("Password", StringComparison.OrdinalIgnoreCase);

    /// <summary>The status as the CLI sees it (appsettings, user-secrets, environment), with the prerequisites given.</summary>
    public StatusReport Status(IConfiguration config, IReadOnlyList<Prerequisite> prerequisites)
    {
        var values = store.Read();
        var written = WrittenKeys(values);
        var profiles = new List<ProfileStatus>();
        foreach (var section in config.GetSection(LlmSettings.ProfilesSection).GetChildren())
        {
            var llm = LlmSettings.From(config, section.Key);
            if (llm.Type is null && llm.Model is null && llm.Endpoint is null && llm.ApiKey is null) continue;   // a template placeholder only
            profiles.Add(new ProfileStatus(section.Key, Providers.Classify(llm.Type, llm.Endpoint), llm.Type, llm.Endpoint is null ? "default" : "custom",
                llm.Model, llm.ApiKey is null ? "missing" : "set", llm.Validate().Count == 0,
                written.Any(k => k.StartsWith(ProfilePrefix(section.Key), StringComparison.OrdinalIgnoreCase))));
        }

        var chatProfile = ChatSettings.LlmProfile(config);
        var chatLlm = LlmSettings.From(config, chatProfile);
        var chatProblems = chatLlm.Validate();
        var models = config.GetSection(ChatModels).GetChildren().Select(c => c.Value).OfType<string>().ToList() is { Count: > 0 } listed
            ? listed : ParseList(config[ChatModels]);
        var chat = new ChatStatus(chatProfile, chatLlm.Model, chatProblems.Count == 0, chatProblems, models);

        var indexingProfile = config[IndexingLlm]?.Trim() is { Length: > 0 } ip && !ip.StartsWith('<') ? ip : null;
        var indexingLlm = LlmSettings.From(config, indexingProfile);
        var indexing = new IndexingStatus(indexingProfile, indexingLlm.Model, indexingLlm.Validate().Count == 0);
        var gateway = new GatewayStatus(config["Auth:Gateway:SigningKey"]?.Trim() is { Length: > 0 } g && !g.StartsWith('<'));

        var missing = new List<string>();
        foreach (var p in prerequisites.Where(p => !p.Ok))
            missing.Add(p.Found is null ? $"{p.Name} {p.Needed} or later is not installed (not on the PATH)." : $"{p.Name} {p.Needed} or later is needed; found {p.Found}.");
        if (!chat.Ready)
        {
            missing.Add(chatProfile is null
                ? "No chat model: Chat:Llm is empty and the Llm section is not set up. Run `setup`, or `setup add <profile> --provider <provider> --model <id> --key-env <VAR> --default`."
                : $"The chat's profile '{chatProfile}' (Chat:Llm) is incomplete: {string.Join("; ", chatProblems)}.");
        }
        return new StatusReport(store.Id, chat.Ready && prerequisites.All(p => p.Ok), missing, prerequisites, profiles, chat, indexing, gateway, written);
    }
}
