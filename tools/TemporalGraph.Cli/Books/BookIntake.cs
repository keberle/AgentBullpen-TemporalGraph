using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TemporalGraph.Cli.Indexing;
using TemporalGraph.Cli.Runs;
using TemporalGraph.Data;

namespace TemporalGraph.Cli.Books;

/// <summary>What <c>books add &lt;ref&gt;</c> was given, in the order it is recognised: a Gutenberg number, a gutenberg.org URL
/// that holds the number, a direct <c>.txt</c> URL on any host, or a local <c>.txt</c> file.</summary>
public sealed record BookRef(int? Gutenberg, string? Url, string? LocalPath)
{
    public string Describe => Gutenberg is { } n ? $"Project Gutenberg #{n}" : Url ?? LocalPath!;
}

public static partial class BookRefs
{
    [GeneratedRegex(@"^(?:https?://)?(?:www\.|aleph\.)?gutenberg\.org/(?:.*/)?(?:ebooks/|epub/|files/|pg)(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex GutenbergUrl();

    /// <summary>Null when <paramref name="text"/> is none of the four kinds.</summary>
    public static BookRef? Parse(string? text)
    {
        var s = (text ?? "").Trim();
        if (s.Length == 0) return null;
        if (s.All(char.IsAsciiDigit)) return int.TryParse(s, out var n) && n > 0 ? new BookRef(n, null, null) : null;
        if (GutenbergUrl().Match(s) is { Success: true } m) return new BookRef(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), null, null);
        if (Uri.TryCreate(s, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            return uri!.AbsolutePath.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) || uri.AbsolutePath.EndsWith(".txt.utf-8", StringComparison.OrdinalIgnoreCase)
                ? new BookRef(null, uri.ToString(), null) : null;
        return s.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ? new BookRef(null, null, s) : null;
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NotSlug();

    /// <summary>
    /// The book id made from a title: lower case, folded to ASCII, a leading article dropped, everything else "-",
    /// at most 60 characters. "The Hound of the Baskervilles" → "hound-of-the-baskervilles".
    /// </summary>
    public static string Slug(string title)
    {
        var folded = new StringBuilder();
        foreach (var c in title.Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) folded.Append(c);
        var slug = NotSlug().Replace(folded.ToString().ToLowerInvariant(), "-").Trim('-');
        foreach (var article in new[] { "the-", "a-", "an-" })
            if (slug.StartsWith(article, StringComparison.Ordinal) && slug.Length > article.Length) { slug = slug[article.Length..]; break; }
        return slug.Length <= 60 ? slug : slug[..60].TrimEnd('-');
    }

    /// <summary>A valid id: what <see cref="Slug"/> produces.</summary>
    public static bool IsId(string id) => id.Length is > 0 and <= 60 && id == NotSlug().Replace(id, "-").Trim('-');

    [GeneratedRegex(@"^(Title|Author):\s*(\S.*?)\s*$", RegexOptions.Multiline)]
    private static partial Regex HeaderLine();

    /// <summary>The "Title:" and "Author:" lines of a Gutenberg text's header (the part before the START line).</summary>
    public static (string? Title, string? Author) Header(string text)
    {
        var start = text.IndexOf("*** START OF", StringComparison.Ordinal);
        var header = start > 0 ? text[..start] : text[..Math.Min(text.Length, 4000)];
        string? title = null, author = null;
        foreach (Match m in HeaderLine().Matches(header.Replace("\r\n", "\n")))
        {
            if (m.Groups[1].Value == "Title") title ??= m.Groups[2].Value;
            else author ??= m.Groups[2].Value;
        }
        return (title, author);
    }
}

public sealed record BookAddOptions(string Ref, string? Id = null, string? Title = null, string? Author = null, int? ExpectChapters = null, bool Force = false);

/// <summary>What <c>books add --dry-run</c> would do: where the text would come from and the id it would get. Nothing is written
/// and the text is not downloaded (for a Gutenberg number the catalogue is asked for the title, which makes the id).</summary>
/// <param name="Id">Null when only the text's own Title: line could say (a .txt URL without <c>--id</c> or <c>--title</c>).</param>
/// <param name="Exists">The id is already in the manifest.</param>
public sealed record AddPlan(string Ref, int? Gutenberg, IReadOnlyList<string> Urls, string? LocalPath, string? Id, string? Title, string? Author, bool Exists, string Note);

public sealed record ChapterRow(int Number, string Title, string FirstLine, int Chars);

/// <param name="ProfileSource">"override" (data/books/profiles/&lt;id&gt;.json) or "detected".</param>
/// <param name="Checks">Stage 0's checks; empty when Stage 0 could not run.</param>
/// <param name="RunId">The <c>index prepare</c> run.</param>
public sealed record AddOutcome(string Id, string Title, string Author, int? Gutenberg, string SourceUrl, string Sha256, int Bytes, string SourceText,
    BookProfile? Profile, string? ProfileSource, IReadOnlyList<ChapterRow> Chapters, IReadOnlyList<CheckResult> Checks, string? RunId, bool Replaced)
{
    /// <summary>The book was already there with exactly this text (the same SHA-256): nothing was written.</summary>
    public bool Unchanged { get; init; }

    public bool ChecksPassed => Profile is not null && Checks.Count > 0 && Checks.All(c => c.Passed);
}

public sealed record RefetchOutcome(string Id, string SourceUrl, string Sha256Committed, string Sha256Fetched, int Bytes)
{
    public bool Changed => Sha256Committed != Sha256Fetched;
}

/// <summary>Bad input for the <c>books</c> commands (exit 2).</summary>
public sealed class BookInputException(string message) : Exception(message);

/// <summary>A download that failed (exit 1).</summary>
public sealed class BookDownloadException(string message) : Exception(message);

/// <summary>
/// A book from Project Gutenberg into the repo: the text byte for byte into <c>data/books/source/&lt;id&gt;.txt</c>, a manifest
/// entry, a detected Stage 0 profile and the book database with its chapters. No prompts and no console: the commands and the
/// interactive path both call this.
/// </summary>
/// <param name="pause">The wait between two requests (2 s: Project Gutenberg asks automated access to stay light); tests pass none.</param>
public sealed class BookIntake(HttpClient http, string root, Func<TimeSpan, CancellationToken, Task>? pause = null)
{
    public const string UserAgent = "TemporalGraph-CLI/1.0 (AgentBullpen Temporal Graph demo; +https://agentbullpen.ai)";
    public static readonly TimeSpan Pause = TimeSpan.FromSeconds(2);

    /// <summary>The timeout of one request to Gutendex or Project Gutenberg when <c>Books:DownloadTimeoutSeconds</c> is not set.</summary>
    public const int DefaultDownloadTimeoutSeconds = 60;

    private readonly Func<TimeSpan, CancellationToken, Task> _pause = pause ?? Task.Delay;

    /// <summary>The lines of this intake (each request and what came back, what was written), for the run folder's run.log.</summary>
    public RunLog Log { get; } = new();

    /// <param name="config">Reads <c>Books:DownloadTimeoutSeconds</c>; null or not set: 60 s.</param>
    public static HttpClient CreateClient(Microsoft.Extensions.Configuration.IConfiguration? config = null)
    {
        var seconds = int.TryParse(config?["Books:DownloadTimeoutSeconds"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : DefaultDownloadTimeoutSeconds;
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(seconds) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return client;
    }

    private string ManifestPath => Path.Combine(RepoPaths.Books(root), BookManifest.FileName);

    public IReadOnlyList<BookEntry> Manifest() => File.Exists(ManifestPath) ? BookManifest.Read(ManifestPath) : [];

    public sealed record Metadata(string? Title, string? Author, IReadOnlyList<string> TextUrls);

    /// <summary>Gutendex's record of a Gutenberg number; null when it cannot be reached or has no such book.</summary>
    public async Task<Metadata?> MetadataAsync(int number, CancellationToken ct)
    {
        try
        {
            Log.Write($"GET https://gutendex.com/books/{number}");
            using var response = await http.GetAsync($"https://gutendex.com/books/{number}", ct);
            Log.Write($"  HTTP {(int)response.StatusCode}");
            if (!response.IsSuccessStatusCode) return null;
            var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
            var formats = (json?["formats"] as JsonObject)?.Where(kv => kv.Key.StartsWith("text/plain", StringComparison.OrdinalIgnoreCase) && kv.Value is not null)
                .OrderByDescending(kv => kv.Key.Contains("utf-8", StringComparison.OrdinalIgnoreCase))
                .Select(kv => kv.Value!.GetValue<string>()).ToList() ?? [];
            return new Metadata(json?["title"]?.GetValue<string>(), (json?["authors"] as JsonArray)?.FirstOrDefault()?["name"]?.GetValue<string>(), formats);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException)
        {
            if (ct.IsCancellationRequested) throw;
            Log.Write($"  the catalogue did not answer ({ex.GetType().Name}: {ex.Message}); going on without it");
            return null;
        }
    }

    /// <summary>Gutendex gives "Doyle, Arthur Conan"; the manifest has "Arthur Conan Doyle".</summary>
    public static string? AuthorName(string? catalogued)
    {
        if (string.IsNullOrWhiteSpace(catalogued)) return null;
        var parts = catalogued.Split(',', 2, StringSplitOptions.TrimEntries);
        return parts.Length == 2 && parts[1].Length > 0 ? $"{parts[1]} {parts[0]}" : catalogued.Trim();
    }

    /// <summary>The places to try for a number's plain text, in order: the catalogue's, then Project Gutenberg's two usual paths.</summary>
    public static IReadOnlyList<string> TextUrls(int number, Metadata? metadata) =>
        (metadata?.TextUrls ?? []).Concat([$"https://www.gutenberg.org/ebooks/{number}.txt.utf-8", $"https://www.gutenberg.org/cache/epub/{number}/pg{number}.txt"])
        .Distinct().ToList();

    /// <summary>One request per URL, a pause between two; the bytes exactly as sent.</summary>
    public async Task<(byte[] Bytes, string Url)> DownloadAsync(IReadOnlyList<string> urls, CancellationToken ct)
    {
        var failures = new List<string>();
        for (var i = 0; i < urls.Count; i++)
        {
            if (i > 0) await _pause(Pause, ct);
            try
            {
                Log.Write($"GET {urls[i]}");
                using var response = await http.GetAsync(urls[i], ct);
                if (response.IsSuccessStatusCode)
                {
                    var bytes = await response.Content.ReadAsByteArrayAsync(ct);
                    Log.Write($"  HTTP {(int)response.StatusCode}, {bytes.Length:N0} bytes");
                    if (bytes.Length > 0) return (bytes, urls[i]);
                    failures.Add($"{urls[i]}: empty");
                }
                else
                {
                    Log.Write($"  HTTP {(int)response.StatusCode}");
                    failures.Add($"{urls[i]}: HTTP {(int)response.StatusCode}");
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                if (ct.IsCancellationRequested) throw;
                Log.Write($"  failed: {ex.Message}");
                failures.Add($"{urls[i]}: {ex.Message}");
            }
        }
        throw new BookDownloadException("The text could not be downloaded. " + string.Join("; ", failures));
    }

    /// <summary>The text and where it came from, for a ref; the catalogue's title and author when the ref is a Gutenberg number.</summary>
    private async Task<(byte[] Bytes, string Url, int? Number, Metadata? Metadata)> FetchAsync(BookRef reference, CancellationToken ct)
    {
        if (reference.LocalPath is { } path)
        {
            var full = Path.GetFullPath(path);
            if (!File.Exists(full)) throw new BookInputException($"No such file: {path}");
            return (await File.ReadAllBytesAsync(full, ct), full, null, null);
        }
        if (reference.Url is { } url)
        {
            var (direct, from) = await DownloadAsync([url], ct);
            return (direct, from, null, null);
        }
        var number = reference.Gutenberg!.Value;
        var metadata = await MetadataAsync(number, ct);
        if (metadata is not null) await _pause(Pause, ct);
        var (bytes, source) = await DownloadAsync(TextUrls(number, metadata), ct);
        return (bytes, source, number, metadata);
    }

    /// <summary><c>books add --dry-run</c>: what would be fetched and the id the book would get. Writes nothing.</summary>
    public async Task<AddPlan> PlanAsync(BookAddOptions options, CancellationToken ct)
    {
        var reference = BookRefs.Parse(options.Ref)
            ?? throw new BookInputException($"'{options.Ref}' is not a Project Gutenberg number, a gutenberg.org URL, a .txt URL or a .txt file.");
        if (options.Id is { } given && !BookRefs.IsId(given))
            throw new BookInputException($"--id '{given}' must be lower-case letters, digits and '-' (at most 60 characters).");
        string? title = options.Title, author = options.Author;
        IReadOnlyList<string> urls = [];
        var note = "";
        if (reference.LocalPath is { } path)
        {
            var full = Path.GetFullPath(path);
            if (!File.Exists(full)) throw new BookInputException($"No such file: {path}");
            var header = BookRefs.Header(Encoding.UTF8.GetString(await File.ReadAllBytesAsync(full, ct)));
            (title, author) = (title ?? header.Title, author ?? header.Author);
            note = "The file would be copied as it is.";
        }
        else if (reference.Url is { } url)
        {
            urls = [url];
            note = "The title and the author come from the text's own header, which a dry run does not download.";
        }
        else
        {
            var metadata = await MetadataAsync(reference.Gutenberg!.Value, ct);
            (title, author) = (title ?? metadata?.Title, author ?? AuthorName(metadata?.Author));
            urls = TextUrls(reference.Gutenberg.Value, metadata);
            note = metadata is null ? "The catalogue did not answer: the title and the author would come from the text's own header."
                : "The first of these that answers is used; the title and the author are the catalogue's.";
        }
        var id = options.Id ?? (title is null ? null : BookRefs.Slug(title));
        return new AddPlan(options.Ref, reference.Gutenberg, urls, reference.LocalPath is null ? null : Path.GetFullPath(reference.LocalPath), id, title, author,
            id is not null && Manifest().Any(b => b.Id == id), note);
    }

    public async Task<AddOutcome> AddAsync(BookAddOptions options, CancellationToken ct)
    {
        Log.Write($"books add {options.Ref}{(options.Id is { } asId ? $" --id {asId}" : "")}{(options.Force ? " --force" : "")}");
        var reference = BookRefs.Parse(options.Ref)
            ?? throw new BookInputException($"'{options.Ref}' is not a Project Gutenberg number, a gutenberg.org URL, a .txt URL or a .txt file.");
        if (options.Id is { } given && !BookRefs.IsId(given))
            throw new BookInputException($"--id '{given}' must be lower-case letters, digits and '-' (at most 60 characters).");
        if (options.ExpectChapters is <= 0) throw new BookInputException("--expect-chapters must be 1 or more.");

        var (bytes, url, number, metadata) = await FetchAsync(reference, ct);
        var text = Encoding.UTF8.GetString(bytes);
        var header = BookRefs.Header(text);
        var title = options.Title ?? metadata?.Title ?? header.Title;
        var author = options.Author ?? AuthorName(metadata?.Author) ?? header.Author;
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(author))
            throw new BookInputException("The title and the author could not be read from the catalogue or from the text's header. Give --title \"<title>\" and --author \"<author>\".");

        var id = options.Id ?? BookRefs.Slug(title);
        if (id.Length == 0) throw new BookInputException("No book id can be made from the title. Give --id <slug>.");
        var manifest = Manifest();
        var existing = manifest.FirstOrDefault(b => b.Id == id);
        var sha = Stage0.Sha256(bytes);
        var textPath = SourceTexts.PathOf(root, id);
        if (existing is not null && !options.Force)
        {
            // Adding a book that is there with exactly this text is not an error: nothing changes.
            if (File.Exists(textPath) && SourceTexts.Sha256(root, id) == sha)
            {
                Log.Write($"{id}: already present with this text (sha256 {sha}); unchanged");
                return new AddOutcome(id, existing.Title, existing.Author, existing.Gutenberg ?? number, existing.SourceUrl ?? url, sha, bytes.Length,
                    Path.GetRelativePath(root, textPath).Replace('\\', '/'), null, null, [], [], null, false) { Unchanged = true };
            }
            throw new BookInputException($"'{id}' is already in data/books/books.json with another text. Use --force to fetch it again, or --id <slug> for another id.");
        }
        // A book with a promoted graph keeps its text: every offset in the graph points into it.
        if (existing is not null && File.Exists(IndexPrepareCommand.ProvenancePath(root, id)) && File.Exists(SourceTexts.PathOf(root, id))
            && SourceTexts.Sha256(root, id) != sha)
            throw new BookInputException($"'{id}' has a promoted graph and the downloaded text differs from the committed one (sha256 {sha}). The committed text is kept; use --id <slug> to add the download as another book.");

        // A stop while the text is being written leaves no partial .txt and the manifest as it was.
        Directory.CreateDirectory(Path.GetDirectoryName(textPath)!);
        var partial = textPath + ".part";
        try
        {
            await File.WriteAllBytesAsync(partial, bytes, ct);
            ct.ThrowIfCancellationRequested();
            File.Move(partial, textPath, overwrite: true);
        }
        catch (OperationCanceledException)
        {
            if (File.Exists(partial)) File.Delete(partial);
            throw;
        }
        Log.Write($"wrote {Path.GetRelativePath(root, textPath).Replace('\\', '/')} ({bytes.Length:N0} bytes, sha256 {sha})");

        // New: no hero, no avatars, an empty roster. Replaced: what the entry already has is kept (BookManifest.AddOrUpdate).
        var entry = new BookEntry(id, title.Trim(), author.Trim(), existing is null ? [] : null, Gutenberg: number, SourceUrl: url, Sha256: sha,
            FetchedAt: DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
        BookManifest.Write(ManifestPath, BookManifest.AddOrUpdate(manifest, entry));

        Log.Write($"data/books/books.json: {id} {(existing is null ? "added" : "replaced")}");

        var prepared = Prepare(id, options.ExpectChapters, text);
        return new AddOutcome(id, entry.Title, entry.Author, number, url, sha, bytes.Length, Path.GetRelativePath(root, SourceTexts.PathOf(root, id)).Replace('\\', '/'),
            prepared.Profile, prepared.Source, prepared.Chapters, prepared.Checks, prepared.RunId, existing is not null);
    }

    /// <summary>
    /// Detects the profile (or reads the override; <paramref name="expectChapters"/> writes one), runs <c>index prepare</c> and makes
    /// sure the book has a database: Stage 0 writes it with the chapters when every check passes; else an empty one is created, so
    /// the manifest never names a book without a file.
    /// </summary>
    public (BookProfile? Profile, string? Source, IReadOnlyList<ChapterRow> Chapters, IReadOnlyList<CheckResult> Checks, string? RunId) Prepare(string id, int? expectChapters, string? text = null)
    {
        text ??= Encoding.UTF8.GetString(SourceTexts.Read(root, id));
        var dbPath = Path.Combine(RepoPaths.Books(root), id + ".sqlite");
        var profile = BookProfiles.ReadOverride(root, id);
        var source = profile is null ? "detected" : "override";
        profile ??= BookProfile.Detect(text, id);
        if (profile is not null && expectChapters is { } n && n != profile.ExpectedChapters)
        {
            profile = profile with { ExpectedChapters = n };
            BookProfiles.WriteOverride(root, profile);
            source = "override";
        }
        if (profile is null)
        {
            if (!File.Exists(dbPath)) BookDatabase.Create(dbPath);
            // No chapters, so Stage 0 made no run folder: the intake makes one, for its log.
            var noStage0 = RunFolder.Create(RepoPaths.Runs(root));
            noStage0.Write(RunFolder.RunJson, new { runId = noStage0.RunId, command = $"books add ({id})", startedAt = DateTimeOffset.UtcNow, finishedAt = DateTimeOffset.UtcNow, book = id, passed = false });
            Log.Write($"stage 0: no chapter headings of a known style in {id}");
            Log.AttachTo(noStage0.File(RunLog.FileName));
            return (null, null, [], [new CheckResult("chapters", id, false, "no chapter headings of a known style between the Gutenberg START and END lines")], noStage0.RunId);
        }

        var outcome = IndexPrepareCommand.Run(root, [id], $"index prepare --book {id}", writeDatabase: true);
        if (!File.Exists(dbPath)) BookDatabase.Create(dbPath);
        var result = outcome.Results[0];
        var chapters = result.Chapters.Select(c => new ChapterRow(c.Number, c.Title, FirstLine(text, c), c.CharEnd - c.CharStart)).ToList();
        Log.Write($"stage 0: {chapters.Count} chapters ({source} profile); checks {(result.Checks.All(c => c.Passed) ? "pass" : "FAIL: " + string.Join("; ", result.Checks.Where(c => !c.Passed).Select(c => $"{c.Name}: {c.Details}")))}");
        Log.AttachTo(outcome.Run.File(RunLog.FileName));
        return (profile, source, chapters, result.Checks, outcome.Run.RunId);
    }

    private static string FirstLine(string text, ChapterInfo chapter)
    {
        if (chapter.Paragraphs.Count == 0) return "";
        var p = chapter.Paragraphs[0];
        var line = text[p.CharStart..p.CharEnd].Replace("\r\n", " ").Replace('\n', ' ');
        return line.Length <= 60 ? line : line[..60].TrimEnd() + "…";
    }

    /// <summary>Removes the manifest entry, the source text, the database, the profile override and the provenance file; <c>runs/</c> stay.</summary>
    public IReadOnlyList<string> Remove(string id)
    {
        var manifest = Manifest();
        if (manifest.All(b => b.Id != id)) throw new BookInputException($"'{id}' is not in data/books/books.json.");
        BookManifest.Write(ManifestPath, manifest.Where(b => b.Id != id));
        var removed = new List<string>();
        foreach (var path in new[] { SourceTexts.PathOf(root, id), Path.Combine(RepoPaths.Books(root), id + ".sqlite"), BookProfiles.OverridePath(root, id), IndexPrepareCommand.ProvenancePath(root, id) })
        {
            if (!File.Exists(path)) continue;
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(path);
            removed.Add(Path.GetRelativePath(root, path).Replace('\\', '/'));
        }
        return removed;
    }

    /// <summary>Downloads the book's text again and compares its SHA-256 with the committed file's. Nothing is written.</summary>
    public async Task<RefetchOutcome> RefetchAsync(string id, CancellationToken ct)
    {
        var entry = Manifest().FirstOrDefault(b => b.Id == id) ?? throw new BookInputException($"'{id}' is not in data/books/books.json.");
        if (!File.Exists(SourceTexts.PathOf(root, id))) throw new BookInputException($"'{id}' has no source text (data/books/source/{id}.txt).");
        IReadOnlyList<string> urls = entry.SourceUrl is { } u && Uri.TryCreate(u, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? [u]
            : entry.Gutenberg is { } n ? TextUrls(n, null)
            : throw new BookInputException($"'{id}' has no sourceUrl and no gutenberg number in data/books/books.json to fetch from.");
        var (bytes, url) = await DownloadAsync(urls, ct);
        return new RefetchOutcome(id, url, SourceTexts.Sha256(root, id), Stage0.Sha256(bytes), bytes.Length);
    }
}
