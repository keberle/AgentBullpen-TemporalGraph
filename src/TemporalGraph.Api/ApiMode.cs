using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.AI;
using Microsoft.IdentityModel.Tokens;
using TemporalGraph.Chat;
using TemporalGraph.Llm;

namespace TemporalGraph.Api;

// Two modes, one chat code path. Standalone (a clone, `aspire run`): anonymous, the chat's model and key from the cloner's
// config, no budget. Gateway (behind the AgentBullpen.AI site): the gateway's JWT names the member, model calls go through the
// site's LLM proxy with that JWT as the key, and the allowance comes from the gateway's headers. The difference lives in four small
// adapters, chosen once at startup (AddApiMode): ICurrentUser, IModelCatalog, IChatClientFactory, IBudget.

/// <summary>
/// <c>Auth:Gateway:SigningKey</c> (HS256, the site's <c>gateway-signing-key</c>; set = gateway mode), <c>Auth:Gateway:Issuer</c>
/// (default <c>https://api.agentbullpen.ai</c>), <c>Auth:Gateway:Audience</c> (default <c>temporal</c>, the site's cluster id) and
/// <c>Gateway:LlmBaseUrl</c> (default <c>https://api.agentbullpen.ai/llm</c>, the site's LLM proxy). All four unset: standalone
/// mode. A key that is still a <c>&lt;placeholder&gt;</c>, or no key beside one of the other three: the API refuses to start
/// (<see cref="GatewayConfigurationException"/>), so a deployment meant for the gateway never runs open.
/// </summary>
public sealed record GatewayOptions(string? SigningKey, string Issuer, string Audience, string LlmBaseUrl)
{
    public const string DefaultIssuer = "https://api.agentbullpen.ai";
    public const string DefaultAudience = "temporal";
    public const string DefaultLlmBaseUrl = "https://api.agentbullpen.ai/llm";

    public bool Enabled => SigningKey is { Length: > 0 };

    private const string KeySetting = "Auth:Gateway:SigningKey";

    /// <summary>The other three settings: one of them set means the gateway was meant.</summary>
    private static readonly string[] GatewayOnly = ["Auth:Gateway:Issuer", "Auth:Gateway:Audience", "Gateway:LlmBaseUrl"];

    public static GatewayOptions From(IConfiguration config)
    {
        // Unset is empty, or the template's <placeholder> text for the three settings that have a default.
        static string? Value(string? raw) => raw?.Trim() is { Length: > 0 } v && !v.StartsWith('<') ? v : null;

        var key = config[KeySetting]?.Trim() is { Length: > 0 } k ? k : null;
        if (key is not null && key.StartsWith('<'))
            throw new GatewayConfigurationException(
                $"{KeySetting} is still a <placeholder>. Set it to the site's signing key to run behind the gateway, or leave it empty to run standalone.");
        if (key is null && GatewayOnly.FirstOrDefault(name => Value(config[name]) is not null) is { } set)
            throw new GatewayConfigurationException(
                $"{set} is set but {KeySetting} is empty, so the API would run without authentication. Set {KeySetting} to run behind the gateway, or clear {set} to run standalone.");

        return new(key, Value(config["Auth:Gateway:Issuer"]) ?? DefaultIssuer,
            Value(config["Auth:Gateway:Audience"]) ?? DefaultAudience, (Value(config["Gateway:LlmBaseUrl"]) ?? DefaultLlmBaseUrl).TrimEnd('/'));
    }

    /// <summary>The checks the API makes on every gateway token: issuer, audience, HS256 only, lifetime with 30 s of skew.</summary>
    public TokenValidationParameters Validation() => new()
    {
        ValidIssuer = Issuer,
        ValidAudience = Audience,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey ?? "")),
        ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
        ClockSkew = TimeSpan.FromSeconds(30),
        NameClaimType = "name",
        RoleClaimType = "role",
    };
}

/// <summary>The gateway settings are half-filled: the API does not start, rather than start without authentication.</summary>
public sealed class GatewayConfigurationException(string message) : Exception(message);

public static class ApiMode
{
    /// <summary>Registers the four adapters for the mode, and in gateway mode JWT bearer auth with "authenticated" as the fallback policy.</summary>
    public static WebApplicationBuilder AddApiMode(this WebApplicationBuilder builder)
    {
        var gateway = GatewayOptions.From(builder.Configuration);
        var services = builder.Services;
        services.AddSingleton(gateway);
        if (!gateway.Enabled)
        {
            // Standalone: no authentication registered, exactly as it is without the gateway.
            services.AddSingleton<ICurrentUser, LocalUser>();
            services.AddSingleton<IModelCatalog, LocalModelCatalog>();
            services.AddSingleton<IChatClientFactory, LocalChatClients>();
            services.AddSingleton<IBudget, LocalBudget>();
            return builder;
        }

        services.AddHttpContextAccessor();
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
        {
            o.MapInboundClaims = false;   // keep "sub", "email", "name", "role" as the gateway wrote them
            o.TokenValidationParameters = gateway.Validation();
        });
        services.AddAuthorization(o => o.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
        services.AddHttpClient(GatewayModelCatalog.HttpClientName);
        services.AddSingleton<ICurrentUser, GatewayUser>();
        services.AddSingleton<IModelCatalog, GatewayModelCatalog>();
        services.AddSingleton<IChatClientFactory, GatewayChatClients>();
        services.AddSingleton<IBudget, GatewayBudget>();
        return builder;
    }

    /// <summary>Gateway mode: authentication and authorization middleware. Standalone: nothing.</summary>
    public static WebApplication UseApiMode(this WebApplication app)
    {
        var gateway = app.Services.GetRequiredService<GatewayOptions>();
        if (!gateway.Enabled) return app;
        app.UseAuthentication();
        app.UseAuthorization();
        app.Logger.LogInformation("Gateway mode: JWT issuer {Issuer}, audience {Audience}; model calls through {LlmBaseUrl}", gateway.Issuer, gateway.Audience, gateway.LlmBaseUrl);
        return app;
    }
}

// ── Who is asking ────────────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>The member behind the request: the gateway JWT's <c>sub</c> and the bearer token itself; null for the local user.</summary>
public interface ICurrentUser
{
    string? Id { get; }

    /// <summary>The incoming request's bearer token (gateway mode). Never logged.</summary>
    string? BearerToken { get; }
}

public sealed class LocalUser : ICurrentUser
{
    public string? Id => null;
    public string? BearerToken => null;
}

public sealed class GatewayUser(IHttpContextAccessor http) : ICurrentUser
{
    public string? Id => http.HttpContext?.User.FindFirst("sub")?.Value;

    public string? BearerToken =>
        http.HttpContext?.Request.Headers.Authorization.ToString() is { } h && h.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? h[7..].Trim() : null;
}

// ── Which models ─────────────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>A model a reader may pick for the chat: <see cref="Id"/> is what the <c>chat</c> mutation's <c>model</c> takes.</summary>
/// <param name="Provider">openai, anthropic, azure, openrouter (gateway: the site's provider name).</param>
[GraphQLName("ChatModel")]
public sealed record ChatModel(string Id, string DisplayName, string Provider, bool IsDefault)
{
    /// <summary>The provider's model id (or Azure deployment name).</summary>
    [GraphQLIgnore] public string Model { get; init; } = "";

    /// <summary>Standalone: the <c>Llm:Profiles</c> name, or null for the <c>Llm</c> section.</summary>
    [GraphQLIgnore] public string? Profile { get; init; }

    /// <summary>Gateway: the provider speaks the Anthropic protocol (else OpenAI's).</summary>
    [GraphQLIgnore] public bool Anthropic { get; init; }

    /// <summary>False while the site has tripped this model's provider (its <c>/llm/models</c> <c>available</c>); standalone always true.</summary>
    public bool Available { get; init; } = true;

    /// <summary>When an unavailable model is expected back (ISO 8601 UTC), when the site says.</summary>
    public string? UnavailableUntilUtc { get; init; }
}

/// <param name="Problems">Why chat isn't available (setting names only, never values); empty when it is.</param>
public sealed record ModelCatalog(IReadOnlyList<ChatModel> Models, IReadOnlyList<string> Problems)
{
    public ChatModel? Default => Models.FirstOrDefault(m => m.IsDefault) ?? Models.FirstOrDefault();

    public ChatModel? Find(string? id) => string.IsNullOrWhiteSpace(id) ? Default : Models.FirstOrDefault(m => m.Id == id.Trim());
}

public interface IModelCatalog
{
    Task<ModelCatalog> GetAsync(CancellationToken ct);
}

/// <summary>
/// Standalone: the default model (<c>Chat:Llm</c>, else the <c>Llm</c> section, as it is without the gateway) first, then each profile named in
/// <c>Chat:Models</c> (a list, or one comma-separated string) whose settings are complete. Id = the profile name (<c>default</c> for
/// the <c>Llm</c> section); display name = the profile's <c>DisplayName</c>, else its <c>Model</c>.
/// </summary>
public sealed class LocalModelCatalog(IConfiguration config, ILogger<LocalModelCatalog> log) : IModelCatalog
{
    public const string DefaultId = "default";

    public Task<ModelCatalog> GetAsync(CancellationToken ct) => Task.FromResult(Get());

    public ModelCatalog Get()
    {
        var defaultProfile = ChatSettings.LlmProfile(config);
        var llm = LlmSettings.From(config, defaultProfile);
        var problems = llm.Validate();
        if (problems.Count > 0) return new([], problems);

        var models = new List<ChatModel> { Entry(llm, isDefault: true) };
        foreach (var name in Allowed(config).Where(n => n != defaultProfile).Distinct())
        {
            var profile = LlmSettings.From(config, name);
            if (profile.Validate() is { Count: > 0 } p)
                log.LogWarning("Chat:Models: profile '{Profile}' is left out: {Problems}", name, string.Join(" ", p));
            else models.Add(Entry(profile, isDefault: false));
        }
        return new(models, []);
    }

    /// <summary><c>Chat:Models</c>: profile names, as a list (<c>Chat:Models:0</c>, …) or one comma-separated value.</summary>
    public static IReadOnlyList<string> Allowed(IConfiguration config)
    {
        var section = config.GetSection("Chat:Models");
        // The template's empty list is the value "" (the JSON provider's way), not children.
        var values = section.Value is { Length: > 0 } single ? single.Split(',') : section.GetChildren().Select(c => c.Value ?? "");
        return values.Select(v => v.Trim()).Where(v => v.Length > 0 && !v.StartsWith('<')).ToList();
    }

    private ChatModel Entry(LlmSettings llm, bool isDefault) =>
        new(llm.Profile ?? DefaultId, config[$"{llm.SectionPath}:DisplayName"]?.Trim() is { Length: > 0 } d && !d.StartsWith('<') ? d : llm.Model!, Provider(llm), isDefault)
        {
            Model = llm.Model!,
            Profile = llm.Profile,
            Anthropic = llm.IsAnthropic,
        };

    /// <summary>anthropic, or from the OpenAI-type endpoint's host: openrouter, azure, openai (no endpoint), else the host.</summary>
    public static string Provider(LlmSettings llm)
    {
        if (llm.IsAnthropic) return "anthropic";
        if (string.IsNullOrWhiteSpace(llm.Endpoint) || !Uri.TryCreate(llm.Endpoint, UriKind.Absolute, out var uri)) return "openai";
        var host = uri.Host.ToLowerInvariant();
        return host.Contains("openrouter") ? "openrouter" : host.Contains("azure") ? "azure" : host.EndsWith("openai.com") ? "openai" : host;
    }
}

/// <summary>
/// Gateway: the chat models the site allows (<c>GET {Gateway:LlmBaseUrl}/models</c> with the member's token; LLM-PROXY.md), cached for
/// a minute (the list is the site's, not the member's). Id = <c>provider:model</c>. Default: the model whose id equals the default
/// profile's <c>Model</c> (<c>Chat:Llm</c>, else <c>Llm:Model</c>), else the first one listed.
/// </summary>
public sealed class GatewayModelCatalog(GatewayOptions options, ICurrentUser user, IHttpClientFactory http, IConfiguration config, TimeProvider time,
    ILogger<GatewayModelCatalog> log) : IModelCatalog
{
    public const string HttpClientName = "gateway-llm";
    public static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(1);

    private sealed record Cached(ModelCatalog Catalog, DateTimeOffset At);

    // A reference, swapped whole: concurrent requests read either the old list or the new one.
    private volatile Cached? _cache;

    public async Task<ModelCatalog> GetAsync(CancellationToken ct)
    {
        if (_cache is { } c && time.GetUtcNow() - c.At < CacheFor) return c.Catalog;
        if (user.BearerToken is not { } token) throw new GatewayTokenMissingException();

        using var request = new HttpRequestMessage(HttpMethod.Get, options.LlmBaseUrl + "/models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.CreateClient(HttpClientName).SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new GatewayRefusedException(GatewayRefusedException.Unauthorized, 401, "The site did not accept this request's sign-in; sign in again.");
        if (!response.IsSuccessStatusCode)
        {
            // The site has the demo closed: the app-unavailable problem, so the panel sends the reader to the under-construction page.
            var (type, _, extensions) = GatewayRefusalChatClient.Problem(await response.Content.ReadAsStringAsync(ct));
            if (type == "app-unavailable")
                throw new GatewayRefusedException(GatewayRefusedException.AppUnavailable, (int)response.StatusCode, "This demo isn't open right now.", extensions);
            log.LogWarning("The site's model list answered HTTP {Status}", (int)response.StatusCode);
            return new([], [$"The site's model list is not available (HTTP {(int)response.StatusCode})."]);
        }

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
        var preferred = LlmSettings.From(config, ChatSettings.LlmProfile(config)).Model;
        var models = Parse(doc.RootElement);
        // The default is an available model: the preferred one if it is, else the first available one.
        var defaultIndex = models.FindIndex(m => m.Model == preferred && m.Available) is >= 0 and var i ? i : Math.Max(0, models.FindIndex(m => m.Available));
        var catalog = new ModelCatalog(models.Select((m, i) => m with { IsDefault = i == defaultIndex }).ToList(),
            models.Count == 0 ? ["The site allows no chat model."] : []);
        _cache = new Cached(catalog, time.GetUtcNow());
        return catalog;
    }

    /// <summary><c>{ providers: [ { name, protocol, models: [ { id, kind, displayName? } ] } ] }</c> → the chat models, in the site's order.
    /// The label is the model's <c>displayName</c> when the site sends one (the list has none yet), else its id.</summary>
    public static List<ChatModel> Parse(JsonElement root)
    {
        var models = new List<ChatModel>();
        if (!root.TryGetProperty("providers", out var providers) || providers.ValueKind != JsonValueKind.Array) return models;
        foreach (var p in providers.EnumerateArray())
        {
            var name = p.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (string.IsNullOrEmpty(name) || !p.TryGetProperty("models", out var list) || list.ValueKind != JsonValueKind.Array) continue;
            var anthropic = p.TryGetProperty("protocol", out var proto) && string.Equals(proto.GetString(), "anthropic", StringComparison.OrdinalIgnoreCase);
            foreach (var m in list.EnumerateArray())
            {
                var id = m.TryGetProperty("id", out var i) ? i.GetString() : null;
                var kind = m.TryGetProperty("kind", out var k) ? k.GetString() : "chat";
                if (string.IsNullOrEmpty(id) || !string.Equals(kind, "chat", StringComparison.OrdinalIgnoreCase)) continue;
                var label = m.TryGetProperty("displayName", out var d) && d.ValueKind == JsonValueKind.String && d.GetString() is { Length: > 0 } dn ? dn.Trim() : id;
                var available = !(m.TryGetProperty("available", out var a) && a.ValueKind == JsonValueKind.False);
                var until = m.TryGetProperty("unavailableUntilUtc", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
                models.Add(new ChatModel($"{name}:{id}", label, name, false)
                {
                    Model = id, Anthropic = anthropic, Available = available, UnavailableUntilUtc = available ? null : until,
                });
            }
        }
        return models;
    }
}

// ── How a model is called ────────────────────────────────────────────────────────────────────────────────────────────────────

public interface IChatClientFactory
{
    /// <param name="conversationId">The chat session id (gateway: sent as <c>X-Conversation-Id</c> for the site's ledger).</param>
    IChatClient Create(ChatModel model, string conversationId, LlmCallContext context);
}

/// <summary>
/// Standalone: the profile's own endpoint and key, the profile's sampling (as it is without the gateway). An OpenAI-type profile's calls go
/// through <see cref="ProviderCostHandler"/>, which reads the provider's own cost when it sends one (OpenRouter); Anthropic sends none.
/// </summary>
public sealed class LocalChatClients(IConfiguration config) : IChatClientFactory
{
    // One connection pool; the per-session handler only reads the answer's cost. No client timeout: the SDK's own applies, as before.
    private static readonly SocketsHttpHandler Network = new() { PooledConnectionLifetime = TimeSpan.FromMinutes(5) };

    public IChatClient Create(ChatModel model, string conversationId, LlmCallContext context)
    {
        var llm = LlmSettings.From(config, model.Profile);
        var pipeline = new LlmPipelineOptions(CallContext: context, Sampling: LlmSampling.From(config, llm));
        if (llm.IsAnthropic) return ChatClientFactory.Create(llm, maxRetries: 0, pipeline);
        var http = new HttpClient(new ProviderCostHandler(context.AddCallCost) { InnerHandler = Network }, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        return ChatClientFactory.Create(llm, maxRetries: 0, pipeline, new LlmTransport(http, Gateway: false));
    }
}

/// <summary>
/// Gateway: the provider's URL with its host replaced by <c>{Gateway:LlmBaseUrl}/{provider}</c> (LLM-PROXY.md "What a demo sets":
/// <c>…/openai/v1/</c>, <c>…/openrouter/v1/</c>, <c>…/azure/openai/v1/</c>, Anthropic <c>…/anthropic</c> + the SDK's
/// <c>/v1/messages</c>), and the incoming request's bearer token as the key on every HTTP request (<see cref="GatewayCredentialHandler"/>).
/// The client lives with the chat session; the token is read per request, so a session outlives any one token. Sampling: the default
/// profile's section (<c>Temperature</c>, <c>Seed</c>, <c>ReasoningEffort</c>, <c>MaxOutputTokens</c>), the deployment's setting.
/// </summary>
public sealed class GatewayChatClients(GatewayOptions options, ICurrentUser user, IConfiguration config) : IChatClientFactory
{
    // One connection pool for every session's handler; the per-session handler only sets headers.
    private static readonly SocketsHttpHandler Network = new() { PooledConnectionLifetime = TimeSpan.FromMinutes(5) };

    public IChatClient Create(ChatModel model, string conversationId, LlmCallContext context)
    {
        var defaults = LlmSettings.From(config, ChatSettings.LlmProfile(config));
        var (endpoint, anthropicBase) = Endpoints(options.LlmBaseUrl, model.Provider, model.Anthropic);
        var llm = new LlmSettings(model.Anthropic ? LlmSettings.Anthropic : LlmSettings.OpenAI, model.Model, endpoint, GatewayCredentialHandler.PlaceholderKey,
            Profile: defaults.Profile);
        var handler = new GatewayCredentialHandler(() => user.BearerToken, model.Anthropic, conversationId, onFallback: f => context.ModelFallback = f,
            onCost: context.AddCallCost)
        {
            InnerHandler = Network,
        };
        var http = new HttpClient(handler, disposeHandler: false);
        return ChatClientFactory.Create(llm, maxRetries: 0, new LlmPipelineOptions(CallContext: context, Sampling: LlmSampling.From(config, llm)),
            new LlmTransport(http, anthropicBase));
    }

    /// <summary>The OpenAI-SDK endpoint, or for Anthropic the SDK base URL.</summary>
    public static (string? Endpoint, string? AnthropicBase) Endpoints(string llmBaseUrl, string provider, bool anthropic)
    {
        var root = $"{llmBaseUrl.TrimEnd('/')}/{provider}";
        if (anthropic) return (null, root);
        return (root + (string.Equals(provider, "azure", StringComparison.OrdinalIgnoreCase) ? "/openai/v1/" : "/v1/"), null);
    }
}

// ── Budget ───────────────────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>
/// The member's daily allowance in US dollars (gateway), or <see cref="Local"/> (standalone: no limit).
/// </summary>
/// <param name="Level">X-Budget-Level: off, light, standard, generous, custom or none; null standalone.</param>
/// <param name="DailyUsd">X-Budget-Daily-Usd: the allowance per UTC day; null for <c>none</c> and <c>off</c>.</param>
/// <param name="RemainingUsd">X-Budget-Remaining-Usd: what was left when the request arrived; on a chat result, less that turn's cost.
/// Null for <c>none</c>; 0 for <c>off</c>.</param>
/// <param name="Unlimited">No limit: standalone, or the level <c>none</c>.</param>
[GraphQLName("Budget")]
public sealed record Budget(string? Level, double? DailyUsd, double? RemainingUsd, bool Unlimited)
{
    public const string Off = "off";
    public const string None = "none";

    public static readonly Budget Local = new(null, null, null, true);

    /// <summary>After a turn that cost <paramref name="usd"/>: the remaining balance less it, never below 0. An unknown cost changes nothing.</summary>
    public Budget After(decimal? usd) =>
        RemainingUsd is { } r && usd is { } cost ? this with { RemainingUsd = (double)Math.Max(0m, Math.Round((decimal)r - cost, 6)) } : this;

    /// <summary>When the allowance starts again (ISO 8601 UTC): X-Budget-Resets-At, the next UTC midnight; null standalone and for <c>off</c>
    /// (nothing resets it).</summary>
    public string? ResetsAtUtc { get; init; }
}

public interface IBudget
{
    /// <summary>Null in gateway mode when the request carries no budget headers.</summary>
    Budget? Current { get; }
}

public sealed class LocalBudget : IBudget
{
    public Budget? Current => Budget.Local;
}

/// <summary>
/// The gateway's allowance headers on each forwarded request (the site's "Budget headers"):
/// <c>X-Budget-Level</c>, <c>X-Budget-Daily-Usd</c>, <c>X-Budget-Remaining-Usd</c>, <c>X-Budget-Resets-At</c>.
/// </summary>
public sealed class GatewayBudget(IHttpContextAccessor http, TimeProvider time) : IBudget
{
    public const string LevelHeader = "X-Budget-Level";
    public const string DailyHeader = "X-Budget-Daily-Usd";
    public const string RemainingHeader = "X-Budget-Remaining-Usd";
    public const string ResetsAtHeader = "X-Budget-Resets-At";

    public Budget? Current
    {
        get
        {
            var headers = http.HttpContext?.Request.Headers;
            if (headers is null) return null;
            double? Usd(string name) =>
                decimal.TryParse(headers[name].ToString(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var v) && v >= 0 ? (double)v : null;
            var level = headers[LevelHeader].ToString().Trim().ToLowerInvariant() is { Length: > 0 } l ? l : null;
            var (daily, remaining) = (Usd(DailyHeader), Usd(RemainingHeader));
            if (level is null && daily is null && remaining is null) return null;
            if (level == Budget.Off) return new Budget(level, null, 0, false);
            if (level == Budget.None) return new Budget(level, null, null, true);
            var resets = DateTimeOffset.TryParse(headers[ResetsAtHeader].ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
                ? at.ToUniversalTime().ToString("O") : Problems.NextUtcMidnight(time.GetUtcNow());
            return new Budget(level, daily, remaining, false) { ResetsAtUtc = resets };
        }
    }
}
