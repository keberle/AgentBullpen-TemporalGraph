// GraphQL API: the book manifest, and per book its chapters, characters, sample queries and openCypher queries
// over its embedded graph (TemporalGraph.Query, in memory); the chat mutation (TemporalGraph.Chat).
using System.Diagnostics;
using OpenTelemetry.Trace;
using TemporalGraph.Api;
using TemporalGraph.Chat;
using TemporalGraph.Data;
using TemporalGraph.Llm;
using TemporalGraph.Query;

var builder = WebApplication.CreateBuilder(args);

// OpenTelemetry (logs, metrics, traces to the Aspire dashboard) + health checks.
builder.AddServiceDefaults();
// The chat span (TemporalGraph.Api), chat turns (TemporalGraph.Chat) and the GenAI chat spans and metrics (TemporalGraph.Llm).
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing.AddHotChocolateInstrumentation().AddSource(BookCatalog.SourceName, ChatTelemetry.SourceName, LlmTelemetry.SourceName))
    .WithMetrics(metrics => metrics.AddMeter(LlmTelemetry.SourceName, ChatTelemetry.SourceName, Problems.MeterName));
// Every REST error is RFC 9457 problem JSON with a traceId (GraphQL errors carry the same problem in extensions.problem).
builder.Services.AddProblems();

builder.Services.AddSingleton(_ => BookResources.ReadManifest());
builder.Services.AddSingleton<BookCatalog>();
builder.Services.AddSingleton(sp => QueryLimits.From(sp.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ChatSessions>();
builder.Services.AddSingleton<ChatService>();
// Standalone (anonymous, the cloner's models) or gateway (Auth:Gateway:SigningKey set: the site's JWT, models and budget).
// Half-filled gateway settings stop the API here, with the setting named, so it never starts without the authentication it was meant to have.
try { builder.AddApiMode(); }
catch (GatewayConfigurationException ex)
{
    Console.Error.WriteLine($"The API did not start: {ex.Message}");
    return 1;
}

builder.Services
    .AddGraphQLServer()
    .AddInstrumentation()
    .AddQueryType<Query>()
    .AddMutationType<Mutation>()
    .AddErrorFilter<ProblemErrorFilter>()
    // A chat turn may take up to Chat:TimeoutSeconds (60 s) and answers TIMEOUT itself; HotChocolate's own limit is 30 s.
    .ModifyRequestOptions(o => o.ExecutionTimeout = TimeSpan.FromSeconds(120));

var app = builder.Build();

// Load every book now, so a chat roster name that matches no character is logged at startup.
app.Services.GetRequiredService<BookCatalog>().LoadAll();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseApiMode();
app.MapDefaultEndpoints();
app.MapGraphQL();
// A built web app under Web:Root (or wwwroot), with the client routes falling back to index.html.
app.MapWebApp();

app.Run();
return 0;

/// <param name="Hero">The 3:2 hero image path relative to the web app's public folder (books/&lt;id&gt;.webp), or null.</param>
public record Book(string Id, string Title, string Author, string? Hero);

[GraphQLName("Chapter")]
public record BookChapter(int Number, string? Title);

/// <param name="Group">"sample" (queries/&lt;book&gt;/samples, about the book, literal names) or "tool" (the chat tools' queries).</param>
public record SampleQuery(string Name, string Question, string Text, string Group = SampleQueries.Tool);

/// <param name="Rows">Each value rendered as the CLI's table does (lists joined with ", ", nodes as (:Label {name})); null stays null.</param>
/// <param name="Truncated">More rows than <c>Query:MaxRows</c>; only the first ones are returned.</param>
public record CypherResult(IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<string?>> Rows, bool Truncated, int ElapsedMs);

/// <summary><c>Query:MaxRows</c> (default 500) and <c>Query:TimeoutSeconds</c> (default 5).</summary>
public sealed record QueryLimits(int MaxRows, int TimeoutSeconds)
{
    public static QueryLimits From(IConfiguration config) =>
        new(config.GetValue("Query:MaxRows", 500), config.GetValue("Query:TimeoutSeconds", 5));
}

public class Query
{
    public IReadOnlyList<Book> GetBooks([Service] IReadOnlyList<BookEntry> manifest) =>
        manifest.Select(b => new Book(b.Id, b.Title, b.Author, b.Hero)).ToList();

    public IReadOnlyList<BookChapter> GetChapters(string bookId, [Service] BookCatalog catalog) => catalog.Get(bookId).Chapters;

    /// <summary>Canonical names from the characters table, in order of first mention.</summary>
    public IReadOnlyList<string> GetCharacters(string bookId, [Service] BookCatalog catalog) => catalog.Get(bookId).Characters;

    public IReadOnlyList<SampleQuery> GetSampleQueries(string bookId, [Service] BookCatalog catalog)
    {
        catalog.Get(bookId); // unknown id → error
        return SampleQueries.For(bookId);
    }

    /// <summary>
    /// Runs an openCypher query (the supported subset) on the book's graph, with <c>$pos</c> = the end of
    /// <paramref name="chapter"/>, <c>$chapter</c>, <c>$character</c> and <c>$spoilers</c> set. Syntax errors and
    /// unsupported features come back as GraphQL errors with the position (extensions line, column, offset).
    /// </summary>
    public CypherResult Cypher(string bookId, string query, int chapter, string? character, bool spoilers,
        [Service] BookCatalog catalog, [Service] QueryLimits limits)
    {
        var book = catalog.Get(bookId);
        if (!book.Graph.Chapters.TryGetValue(chapter, out var range))
            throw new GraphQLException(ErrorBuilder.New().SetMessage($"Book '{bookId}' has no chapter {chapter}.").SetCode("UNKNOWN_CHAPTER").Build());
        using var span = BookCatalog.Source.StartActivity("cypher");
        span?.SetTag("book.id", bookId);
        var parameters = new Dictionary<string, object?>
        {
            ["pos"] = (long)range.CharEnd,
            ["chapter"] = (long)chapter,
            ["character"] = character,
            ["spoilers"] = spoilers,
        };
        var watch = Stopwatch.StartNew();
        QueryResult result;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(limits.TimeoutSeconds));
        try { result = CypherEngine.Execute(book.Graph, query, parameters, timeout.Token); }
        catch (CypherException ex)
        {
            span?.SetStatus(ActivityStatusCode.Error, "query error");
            throw new GraphQLException(ErrorBuilder.New()
                .SetMessage(ex.Message)
                .SetCode(ex.Reason.StartsWith("not supported", StringComparison.Ordinal) ? "NOT_SUPPORTED" : ex.Reason.StartsWith("syntax error", StringComparison.Ordinal) ? "SYNTAX_ERROR" : "QUERY_ERROR")
                .SetExtension("reason", ex.Reason)
                .SetExtension("line", ex.Line)
                .SetExtension("column", ex.Column + 1)
                .SetExtension("offset", ex.Offset)
                .Build());
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            span?.SetStatus(ActivityStatusCode.Error, "timeout");
            throw new GraphQLException(ErrorBuilder.New().SetMessage($"The query ran longer than {limits.TimeoutSeconds} s and was stopped.").SetCode("TIMEOUT").Build());
        }
        var elapsed = (int)Math.Round(watch.Elapsed.TotalMilliseconds);
        var rows = result.Rows.Take(limits.MaxRows).Select(r => (IReadOnlyList<string?>)r.Select(v => v is null ? null : CypherValues.Display(v)).ToList()).ToList();
        span?.SetTag("query.rows", result.Rows.Count);
        span?.SetTag("query.elapsed_ms", elapsed);
        return new CypherResult(result.Columns, rows, result.Rows.Count > limits.MaxRows, elapsed);
    }

    /// <summary>
    /// The book's whole promoted graph (nodes with aliasFirstPos, edges with revealedPos, chapter ends) for the 2D/3D tabs,
    /// which gate it in the browser like the chat tools. Cached per book.
    /// </summary>
    public BookGraphPayload Graph(string bookId, [Service] BookCatalog catalog) => catalog.Get(bookId).Payload;

    /// <summary>False when the <c>Llm</c> section is not usable (placeholders, no key): the chat panel shows a note instead.
    /// Gateway mode: false when the site lists no chat model.</summary>
    public Task<bool> ChatAvailable([Service] ChatService chat, CancellationToken cancellationToken) => chat.AvailableAsync(cancellationToken);

    /// <summary>The models the chat may use (standalone: the default and <c>Chat:Models</c>; gateway: the site's list).</summary>
    public Task<IReadOnlyList<ChatModel>> ChatModels([Service] ChatService chat, CancellationToken cancellationToken) => chat.ModelsAsync(cancellationToken);

    /// <summary>The member's daily allowance in dollars, from the gateway's headers; standalone: unlimited; null in gateway mode without the headers.</summary>
    public Budget? Budget([Service] IBudget budget) => budget.Current;

    /// <summary>The chat roster at <paramref name="chapter"/> (books.json <c>chat</c>, in order; spoilers off: those mentioned by then, by their display name).</summary>
    public IReadOnlyList<ChatCharacterView> ChatCharacters(string bookId, int chapter, bool spoilers, [Service] BookCatalog catalog) =>
        ChatService.Characters(catalog.Get(bookId), chapter, spoilers);
}

public class Mutation
{
    /// <summary>
    /// One message to a character. A session holds the conversation; a message whose book, chapter, character or
    /// spoilers differ from its session's starts a new session (new id); so does another model. Errors: LLM_NOT_CONFIGURED,
    /// UNKNOWN_BOOK, UNKNOWN_CHAPTER, UNKNOWN_CHARACTER, UNKNOWN_MODEL, EMPTY_MESSAGE, LLM_ERROR, TIMEOUT, PROVIDER_UNAVAILABLE,
    /// RATE_LIMITED; gateway mode also BUDGET_EXHAUSTED, UNAUTHORIZED. Each with extensions.problem (docs/design/errors.md).
    /// </summary>
    public Task<ChatResult> Chat(ChatInput input, [Service] ChatService chat, CancellationToken cancellationToken) =>
        chat.ChatAsync(input, cancellationToken);
}

/// <summary>For WebApplicationFactory in the tests.</summary>
public partial class Program;
