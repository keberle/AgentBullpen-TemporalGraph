using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Spectre.Console.Testing;
using TemporalGraph.Chat;
using TemporalGraph.Cli;
using TemporalGraph.Cli.Books;
using TemporalGraph.Cli.Indexing;
using TemporalGraph.Cli.Repl;
using TemporalGraph.Cli.Runs;
using TemporalGraph.Query;
using TemporalGraph.Testing;

namespace TemporalGraph.Data.Tests;

/// <summary>The tests that run the CLI in-process swap <see cref="Console.Out"/> and Spectre's console, so they run one at a time.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CliConsoleCollection
{
    public const string Name = "cli-console";
}

/// <summary>
/// The CLI's two doors. The agent door: every command answers <c>--json</c> with one envelope, never prompts without a terminal
/// (exit 2), and uses the exit codes 0–4. The intake (ref parsing, slugs, detected profiles, a download from a fake Project Gutenberg),
/// <c>index run</c>'s pre-flight, and the query and chat consoles driven by scripted lines.
/// </summary>
[Collection(CliConsoleCollection.Name)]
public partial class CliDoorTests
{
    private const string Hound = "hound-of-the-baskervilles";

    private static async Task<(int Exit, string Stdout)> Run(params string[] args)
    {
        var stdout = Console.Out;
        var captured = new StringWriter();
        Console.SetOut(captured);
        try { return (await CliApp.RunAsync(args), captured.ToString()); }
        finally { Console.SetOut(stdout); }
    }

    private sealed class TempRoot : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "TemporalGraph.Data.Tests", Guid.NewGuid().ToString("N"));

        public TempRoot() => Directory.CreateDirectory(System.IO.Path.Combine(Path, "data", "books"));

        /// <summary>With the committed Hound: manifest entry, database and query files (what the chat and the query console read).</summary>
        public TempRoot WithHound()
        {
            var repo = RepoPaths.FindRoot();
            var entry = BookManifest.Read(System.IO.Path.Combine(RepoPaths.Books(repo), BookManifest.FileName)).Single(b => b.Id == Hound);
            BookManifest.Write(System.IO.Path.Combine(RepoPaths.Books(Path), BookManifest.FileName), [entry]);
            File.Copy(System.IO.Path.Combine(RepoPaths.Books(repo), Hound + ".sqlite"), System.IO.Path.Combine(RepoPaths.Books(Path), Hound + ".sqlite"));
            var queries = Directory.CreateDirectory(System.IO.Path.Combine(Path, "queries", Hound)).FullName;
            foreach (var f in Directory.GetFiles(System.IO.Path.Combine(repo, "queries", Hound))) File.Copy(f, System.IO.Path.Combine(queries, System.IO.Path.GetFileName(f)));
            return this;
        }

        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
            catch (IOException) { }
        }
    }

    // ── the envelope and the exit codes ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Every command and sub-command, with arguments that end at once: no model call, nothing written to the repo.</summary>
    public static TheoryData<string> EveryCommand => new(
        "", "books list", "books add", "books remove no-such-book --yes", "books refetch no-such-book",
        "create", "index run", "index run --book no-such-book --dry-run", "index rebuild --book no-such-book --dry-run", "index status", "index status --run no-such-run",
        "index watch --run no-such-run", "index cancel --run no-such-run", "index --stages 9", "books add --dry-run", "help", "help index run",
        "index prepare", "index extract --book no-such-book --chapters 1-3", "index resolve --run no-such-run", "index link --run no-such-run",
        "index dedupe --run no-such-run", "index time --run no-such-run", "index perceive --run no-such-run",
        "index build-db --run no-such-run", "index promote --run no-such-run --book no-such-book", "index score --run no-such-run --gold no-such.json",
        "index keep --baseline no-such-run --candidate no-such-run", "index voice --book no-such-book", "index crosscheck --run no-such-run",
        "gold review --run no-such-run", "gold facts-review --run no-such-run", "gold convert --book no-such-book --chapters 1-3",
        "gold perceptions-review --run no-such-run", "gold import --book no-such-book --file no-such.md", "runs diff",
        $"query --book {Hound}", $"query --book {Hound} --chapter 1 --text RETURN_1", $"query samples --book {Hound}", "query explain --text RETURN_1",
        $"chat --book {Hound}", "chat vet --llm no-such-profile", "llm-check --llm no-such-profile",
        "setup status", "setup add", "setup check --profile no-such-profile", "setup reset --profile no-such-profile");

    [Theory]
    [MemberData(nameof(EveryCommand))]
    public async Task Every_command_answers_json_with_one_envelope(string command)
    {
        var args = command.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(a => a.Replace("RETURN_1", "RETURN 1 AS one")).Append("--json").ToArray();

        var (exit, stdout) = await Run(args);

        var json = JsonNode.Parse(stdout)!.AsObject();   // stdout is exactly one JSON object
        Assert.Equal(["ok", "command", "exit", "result", "problems"], json.Select(kv => kv.Key));
        Assert.Equal(exit, json["exit"]!.GetValue<int>());
        Assert.InRange(exit, 0, 4);
        Assert.Equal(exit == 0, json["ok"]!.GetValue<bool>());
        Assert.Equal(CliApp.CommandName(CliApp.HelpCommand(args)), json["command"]!.GetValue<string>());   // "help index run" is "index run --help"
        var problems = json["problems"]!.AsArray();
        Assert.Equal(exit == 0, problems.Count == 0);
        Assert.All(problems, p => Assert.Equal(["type", "title", "detail"], p!.AsObject().Select(kv => kv.Key)));
        Assert.All(problems, p => Assert.False(string.IsNullOrWhiteSpace(p!["detail"]!.GetValue<string>())));
    }

    [Theory]
    [InlineData("", "home")]
    [InlineData("books add", "books add")]
    [InlineData("books remove some-book", "books remove")]
    [InlineData("index run", "index run")]
    [InlineData("query --book hound-of-the-baskervilles", "query")]
    [InlineData("chat --book hound-of-the-baskervilles --chapter 3 --character Holmes", "chat")]
    public async Task A_prompt_without_a_terminal_is_exit_2_with_the_command_to_run(string command, string name)
    {
        var (exit, stdout) = await Run([.. command.Split(' ', StringSplitOptions.RemoveEmptyEntries), "--json"]);

        var json = JsonNode.Parse(stdout)!;
        Assert.Equal(name, json["command"]!.GetValue<string>());
        // The chat checks its model first: with none configured on this machine it is "not ready" (3) before it would prompt.
        if (name == "chat" && exit == Exit.NotReady) return;
        Assert.Equal(Exit.BadInput, exit);
        Assert.Contains(json["problems"]!.AsArray(), p => p!["type"]!.GetValue<string>() == "no-terminal");
        Assert.Contains(TemporalGraph.Cli.Setup.SetupRunner.Cli, json["problems"]![0]!["detail"]!.GetValue<string>());
    }

    [Fact]
    public async Task Exit_codes_of_the_agent_door()
    {
        Assert.Equal(Exit.BadInput, (await Run("no-such-command")).Exit);                                  // a parser error, every command
        Assert.Equal(Exit.BadInput, (await Run("index", "run", "--book", "no-such-book")).Exit);
        Assert.Equal(Exit.BadInput, (await Run("index", "run", "--book", Hound, "--stages", "7")).Exit);
        Assert.Equal(Exit.NotReady, (await Run("index", "run", "--book", Hound, "--stages", "3-5")).Exit);     // no --from-run: the stage's input is missing
        Assert.Equal(Exit.NotReady, (await Run("index", "run", "--book", Hound, "--llm", "no-such-profile")).Exit);
        Assert.Equal(Exit.NotReady, (await Run("llm-check", "--llm", "no-such-profile")).Exit);
        Assert.Equal(Exit.BadInput, (await Run("query", "--book", Hound, "--text", "MATCH (n RETURN n")).Exit);
        Assert.Equal(Exit.NotReady, (await Run("query", "--db", "no-such.sqlite", "--text", "RETURN 1 AS one")).Exit);
        Assert.Equal(Exit.Ok, (await Run("query", "samples", "--book", Hound)).Exit);
        Assert.Equal(Exit.BadInput, (await Run("books", "list", "--repo", Path.Combine(Path.GetTempPath(), "no-such-folder-" + Guid.NewGuid().ToString("N")))).Exit);
    }

    [Fact]
    public async Task Json_results_keep_their_shape_inside_the_envelope_and_quiet_prints_only_problems()
    {
        var (exit, stdout) = await Run("query", "--book", Hound, "--chapter", "3", "--character", "Holmes", "--file", $"queries/{Hound}/who-met.cypher", "--json");
        Assert.Equal(Exit.Ok, exit);
        var result = JsonNode.Parse(stdout)!["result"]!.AsObject();
        Assert.Equal(["columns", "rows", "parameters", "elapsedMs", "loadMs"], result.Select(kv => kv.Key));   // as without the envelope
        Assert.True(result["rows"]!.AsArray().Count > 0);
        // --character and --chapter are $character, $pos and $chapter; $spoilers is false unless --spoilers.
        Assert.Equal("Holmes", result["parameters"]!["character"]!.GetValue<string>());
        Assert.False(result["parameters"]!["spoilers"]!.GetValue<bool>());

        var samples = JsonNode.Parse((await Run("query", "samples", "--book", Hound, "--json")).Stdout)!["result"]!["queries"]!.AsArray();
        Assert.Equal(26, samples.Count);   // 10 samples and 16 tool queries
        Assert.Equal("Whom has Dr. Watson met so far?", samples[0]!["question"]!.GetValue<string>());
        Assert.Equal(["character", "pos", "spoilers"], samples.Single(s => s!["name"]!.GetValue<string>() == "who-met")!["parameters"]!.AsArray().Select(p => p!.GetValue<string>()));

        var explain = JsonNode.Parse((await Run("query", "explain", "--file", $"queries/{Hound}/who-met.cypher", "--json")).Stdout)!["result"]!;
        Assert.Equal(["MatchClause", "WithClause", "ReturnClause"], explain["clauses"]!.AsArray().Select(c => c!.GetValue<string>()));
        Assert.Equal("QueryAst", explain["ast"]!["type"]!.GetValue<string>());

        var quiet = await Run("books", "list", "--quiet");
        Assert.Equal((Exit.Ok, ""), quiet);
    }

    [Fact]
    public void The_envelope_wraps_json_text_and_failures()
    {
        var ok = JsonNode.Parse(OutputCapture.Envelope("query", 0, "{ \"rows\": 3 }", "", null, []))!;
        Assert.Equal(3, ok["result"]!["rows"]!.GetValue<int>());
        Assert.True(ok["ok"]!.GetValue<bool>());
        Assert.Equal("Run abc", JsonNode.Parse(OutputCapture.Envelope("index prepare", 0, "Run abc\n", "", null, []))!["result"]!["text"]!.GetValue<string>());
        // A command that failed without recording a problem gets one from what it said.
        var failed = JsonNode.Parse(OutputCapture.Envelope("index facts", 1, "", "Stage 3 failed: boom\n", null, []))!;
        Assert.False(failed["ok"]!.GetValue<bool>());
        Assert.Equal(("failed", "Stage 3 failed: boom"), (failed["problems"]![0]!["type"]!.GetValue<string>(), failed["problems"]![0]!["detail"]!.GetValue<string>()));
        Assert.Null(failed["result"]);
        Assert.Equal(["ok", "failed", "bad-input", "not-ready", "check-failed"], Enumerable.Range(0, 5).Select(Exit.Slug));
        Assert.Equal("index run", CliApp.CommandName(["index", "run", "--book", "x"]));
        Assert.Equal("index", CliApp.CommandName(["index", "--stages", "2..5"]));
        Assert.Equal("create", CliApp.CommandName(["create", "my-book", "--title", "T"]));
        Assert.Equal("home", CliApp.CommandName(["--json"]));
    }

    // ── intake: refs, slugs, profiles ──────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("2852", 2852)]
    [InlineData(" 120 ", 120)]
    [InlineData("https://www.gutenberg.org/ebooks/2852", 2852)]
    [InlineData("https://www.gutenberg.org/cache/epub/2852/pg2852.txt", 2852)]
    [InlineData("https://www.gutenberg.org/files/2852/2852-0.txt", 2852)]
    [InlineData("gutenberg.org/ebooks/1257.txt.utf-8", 1257)]
    public void A_gutenberg_number_or_url_is_recognised(string text, int number)
    {
        Assert.Equal(new BookRef(number, null, null), BookRefs.Parse(text));
    }

    [Fact]
    public void A_txt_url_a_local_file_and_what_is_neither()
    {
        Assert.Equal(new BookRef(null, "https://example.org/texts/a-book.txt", null), BookRefs.Parse("https://example.org/texts/a-book.txt"));
        Assert.Equal(new BookRef(null, null, @"C:\texts\a book.txt"), BookRefs.Parse(@"C:\texts\a book.txt"));
        Assert.Equal(new BookRef(null, null, "texts/a-book.txt"), BookRefs.Parse("texts/a-book.txt"));
        Assert.Null(BookRefs.Parse("https://example.org/a-book.epub"));
        Assert.Null(BookRefs.Parse("the hound"));
        Assert.Null(BookRefs.Parse("0"));
        Assert.Null(BookRefs.Parse(""));
    }

    [Theory]
    [InlineData("The Hound of the Baskervilles", "hound-of-the-baskervilles")]
    [InlineData("The Three Musketeers", "three-musketeers")]
    [InlineData("Treasure Island", "treasure-island")]
    [InlineData("A Tale of Two Cities", "tale-of-two-cities")]
    [InlineData("Les Misérables: Tome I — Fantine!", "les-miserables-tome-i-fantine")]
    [InlineData("The", "the")]
    public void A_title_becomes_a_book_id(string title, string id)
    {
        Assert.Equal(id, BookRefs.Slug(title));
        Assert.True(BookRefs.IsId(id));
    }

    [Fact]
    public void A_long_title_is_cut_at_sixty_characters()
    {
        var slug = BookRefs.Slug("The Life and Strange Surprizing Adventures of Robinson Crusoe, of York, Mariner");
        Assert.True(slug.Length <= 60);
        Assert.StartsWith("life-and-strange-surprizing-adventures-of-robinson-crusoe", slug);
        Assert.False(BookRefs.IsId("Not An Id"));
    }

    /// <summary>The detected profile of each committed text is its hand-checked profile, field for field.</summary>
    [Theory]
    [InlineData("hound-of-the-baskervilles", 15, HeadingStyle.ChapterWord)]
    [InlineData("three-musketeers", 67, HeadingStyle.ChapterWord)]
    [InlineData("treasure-island", 34, HeadingStyle.BareRoman)]
    public void Detect_reproduces_the_three_profiles_exactly(string id, int chapters, HeadingStyle style)
    {
        var root = RepoPaths.FindRoot();
        var committed = BookProfiles.ReadOverride(root, id)!;
        Assert.Equal((chapters, style), (committed.ExpectedChapters, committed.Headings));

        var detected = BookProfile.Detect(Encoding.UTF8.GetString(SourceTexts.Read(root, id)), id)!;

        Assert.Equal(committed.BookId, detected.BookId);
        Assert.Equal(committed.ExpectedChapters, detected.ExpectedChapters);
        Assert.Equal(committed.Headings, detected.Headings);
        Assert.Equal(committed.SkipHeadings, detected.SkipHeadings);
        Assert.Equal(committed.NumberFixes?.OrderBy(kv => kv.Key), detected.NumberFixes?.OrderBy(kv => kv.Key));
        // And Stage 0 with the detected profile gives what it gives with the committed one.
        var a = Stage0.Prepare(committed, SourceTexts.Read(root, id), SourceTexts.Sha256(root, id));
        var b = Stage0.Prepare(detected, SourceTexts.Read(root, id), SourceTexts.Sha256(root, id));
        Assert.True(b.Passed);
        Assert.Equal(a.Chapters.Select(c => (c.Number, c.Title, c.CharStart, c.CharEnd)), b.Chapters.Select(c => (c.Number, c.Title, c.CharStart, c.CharEnd)));
    }

    private static string Book(string title, string author, string body) =>
        $"The Project Gutenberg eBook of {title}\r\n\r\nTitle: {title}\r\n\r\nAuthor: {author}\r\n\r\n*** START OF THE PROJECT GUTENBERG EBOOK {title.ToUpperInvariant()} ***\r\n\r\n"
        + body.Replace("\r\n", "\n").Replace("\n", "\r\n") + $"\r\n\r\n*** END OF THE PROJECT GUTENBERG EBOOK {title.ToUpperInvariant()} ***\r\n";

    private const string CapsBody = "A TINY TALE\n\nCONTENTS\n\nCHAPTER I. THE FIRST DAY\nCHAPTER II. THE SECOND DAY\nCHAPTER III. THE LAST DAY\n\n\n"
        + "CHAPTER I. THE FIRST DAY\n\nIt began on a Monday.\n\nNothing happened.\n\nCHAPTER II. THE SECOND DAY\n\nIt went on.\n\n"
        + "CHAPTER III. THE LAST DAY\n\nIt ended.\n\nTHE END";

    [Fact]
    public void Detect_reads_the_two_new_heading_styles()
    {
        // CHAPTER N in capitals, the title on the same line; the contents list uses the same words, so the numbering starts twice.
        var caps = Book("A Tiny Tale", "Jane Doe", CapsBody);
        var profile = BookProfile.Detect(caps, "tiny-tale")!;
        Assert.Equal((3, HeadingStyle.ChapterCaps), (profile.ExpectedChapters, profile.Headings));
        Assert.Equal(["THE END"], profile.SkipHeadings);
        var bytes = Encoding.UTF8.GetBytes(caps);
        var stage0 = Stage0.Prepare(profile, bytes, Stage0.Sha256(bytes));
        Assert.True(stage0.Passed, string.Join("; ", stage0.Checks.Where(c => !c.Passed).Select(c => $"{c.Name}: {c.Details}")));
        Assert.Equal(["THE FIRST DAY", "THE SECOND DAY", "THE LAST DAY"], stage0.Chapters.Select(c => c.Title));
        Assert.Equal([2, 1, 1], stage0.Chapters.Select(c => c.Paragraphs.Count));
        Assert.Equal(3, stage0.Contents.Count);

        // "N. Title", with word numbers elsewhere and no contents list: nothing to check the titles against, which is not a failure.
        var numbered = Book("Short Pieces", "John Roe", "1. Alpha\n\nFirst text.\n\n2. Beta\n\nSecond text.\n\n3. Gamma\n\nThird text.");
        var second = BookProfile.Detect(numbered, "short-pieces")!;
        Assert.Equal((3, HeadingStyle.Numbered), (second.ExpectedChapters, second.Headings));
        var result = Stage0.Prepare(second, Encoding.UTF8.GetBytes(numbered), Stage0.Sha256(Encoding.UTF8.GetBytes(numbered)));
        Assert.True(result.Passed);
        Assert.Contains("no contents list", result.Checks.Single(c => c.Name == "titles").Details);

        var words = Book("Worded", "Ann Poe", "CHAPTER ONE\n\nThe Start\n\nText.\n\nCHAPTER TWO\n\nThe Middle\n\nText.\n\nCHAPTER TWENTY-ONE\n\nOut of order\n\nText.");
        Assert.Equal(HeadingStyle.ChapterCaps, BookProfile.Detect(words)!.Headings);
        Assert.Null(BookProfile.Detect("No Gutenberg lines here.\n\nChapter 1.\n\nText."));
        Assert.Null(BookProfile.Detect(Book("Plain", "No One", "Just prose, with no chapter headings at all.")));
    }

    // ── intake: a download from a fake Project Gutenberg ───────────────────────────────────────────────────────────────────────

    private sealed class FakeWeb(Func<Uri, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(string Url, string? UserAgent)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri!.ToString(), request.Headers.UserAgent.ToString()));
            return Task.FromResult(respond(request.RequestUri));
        }
    }

    private static HttpResponseMessage Bytes(string text) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(text)) };

    private static (BookIntake Intake, FakeWeb Web, List<TimeSpan> Pauses) Intake(TempRoot root, Func<Uri, HttpResponseMessage> respond)
    {
        var web = new FakeWeb(respond);
        var client = new HttpClient(web);
        client.DefaultRequestHeaders.UserAgent.ParseAdd(BookIntake.UserAgent);
        var pauses = new List<TimeSpan>();
        return (new BookIntake(client, root.Path, (t, _) => { pauses.Add(t); return Task.CompletedTask; }), web, pauses);
    }

    [Fact]
    public async Task Books_add_downloads_the_text_byte_for_byte_and_prepares_the_book()
    {
        using var root = new TempRoot();
        var text = Book("A Tiny Tale", "Jane Doe", CapsBody);
        var (intake, web, pauses) = Intake(root, uri => uri.Host == "gutendex.com"
            ? Bytes("""{"id":99001,"title":"A Tiny Tale","authors":[{"name":"Doe, Jane"}],"languages":["en"],"formats":{"text/html":"https://www.gutenberg.org/ebooks/99001.html.images","text/plain; charset=us-ascii":"https://www.gutenberg.org/ebooks/99001.txt.utf-8"}}""")
            : uri.AbsolutePath == "/ebooks/99001.txt.utf-8" ? Bytes(text) : new HttpResponseMessage(HttpStatusCode.NotFound));
        CliContext.Reset();
        var console = new TestConsole();
        console.Profile.Width = 200;

        var (exit, outcome) = await new BooksRunner(intake, root.Path, console, interactive: false).AddAsync(new BookAddOptions("https://www.gutenberg.org/ebooks/99001"), TestContext.Current.CancellationToken);

        Assert.Equal(Exit.Ok, exit);
        Assert.Equal(("tiny-tale", "A Tiny Tale", "Jane Doe", 99001), (outcome!.Id, outcome.Title, outcome.Author, outcome.Gutenberg));
        // The catalogue first, then the text it names: one request each, a pause between, the CLI's own User-Agent.
        Assert.Equal(["https://gutendex.com/books/99001", "https://www.gutenberg.org/ebooks/99001.txt.utf-8"], web.Requests.Select(r => r.Url));
        Assert.All(web.Requests, r => Assert.StartsWith("TemporalGraph-CLI", r.UserAgent));
        Assert.Equal([BookIntake.Pause], pauses);
        // Byte for byte, with its SHA-256 in the manifest.
        var bytes = File.ReadAllBytes(SourceTexts.PathOf(root.Path, "tiny-tale"));
        Assert.Equal(Encoding.UTF8.GetBytes(text), bytes);
        var entry = Assert.Single(intake.Manifest());
        Assert.Equal((Stage0.Sha256(bytes), "https://www.gutenberg.org/ebooks/99001.txt.utf-8", 99001), (entry.Sha256, entry.SourceUrl, entry.Gutenberg));
        Assert.Equal(outcome.Sha256, entry.Sha256);
        Assert.NotNull(entry.FetchedAt);
        Assert.Empty(entry.Chat!);
        Assert.Null(entry.Hero);
        // Detected profile, Stage 0's chapters in the database, the chapter table for the person.
        Assert.Equal(("detected", 3, HeadingStyle.ChapterCaps), (outcome.ProfileSource, outcome.Profile!.ExpectedChapters, outcome.Profile.Headings));
        Assert.True(outcome.ChecksPassed);
        Assert.Equal([(1, "THE FIRST DAY", "It began on a Monday."), (2, "THE SECOND DAY", "It went on."), (3, "THE LAST DAY", "It ended.")],
            outcome.Chapters.Select(c => (c.Number, c.Title, c.FirstLine)));
        Assert.Equal(3, BookShelf.Chapters(Path.Combine(RepoPaths.Books(root.Path), "tiny-tale.sqlite")));
        Assert.Contains("THE SECOND DAY", console.Output);
        Assert.True(File.Exists(Path.Combine(RepoPaths.Runs(root.Path), outcome.RunId!, RunFolder.Stage0Json)));
        var json = BooksRunner.Json(outcome);
        Assert.Equal(["id", "title", "author", "gutenberg", "sourceUrl", "sourceText", "sha256", "bytes", "replaced", "profile", "chapters", "checks", "checksPassed", "runId"], json.Select(kv => kv.Key));

        // Again without --force and with the same text: "already present, unchanged", exit 0, nothing written. With another
        // text it is refused. With --force and a wrong count: added, exit 4, an override.
        var again1 = new TestConsole();
        var runner = new BooksRunner(intake, root.Path, again1, interactive: false);
        var manifestBefore = File.ReadAllText(Path.Combine(RepoPaths.Books(root.Path), BookManifest.FileName));
        var runsBefore = Runs(root.Path);
        var (same, unchanged) = await runner.AddAsync(new BookAddOptions("99001"), TestContext.Current.CancellationToken);
        Assert.Equal(Exit.Ok, same);
        Assert.True(unchanged!.Unchanged);
        Assert.Contains("tiny-tale is already present, unchanged", again1.Output);
        Assert.Empty(CliContext.Problems);
        Assert.Equal(manifestBefore, File.ReadAllText(Path.Combine(RepoPaths.Books(root.Path), BookManifest.FileName)));
        Assert.Equal(runsBefore, Runs(root.Path));
        File.AppendAllText(SourceTexts.PathOf(root.Path, "tiny-tale"), "another text");
        Assert.Equal(Exit.BadInput, (await runner.AddAsync(new BookAddOptions("99001"), TestContext.Current.CancellationToken)).Exit);
        CliContext.Reset();
        var (forced, again) = await runner.AddAsync(new BookAddOptions("99001", ExpectChapters: 4, Force: true), TestContext.Current.CancellationToken);
        Assert.Equal(Exit.CheckFailed, forced);
        Assert.True(again!.Replaced);
        Assert.Equal("stage0-check", Assert.Single(CliContext.Problems).Type);
        Assert.Contains("chapter-count", CliContext.Problems[0].Detail);
        Assert.Equal(4, BookProfiles.ReadOverride(root.Path, "tiny-tale")!.ExpectedChapters);

        // index run refuses the book while a Stage 0 check fails (exit 3, the check named), before it looks for a model.
        CliContext.Reset();
        var index = new IndexRunner(root.Path, new TestConsole(), false, () => new ConfigurationBuilder().Build());
        Assert.Equal(Exit.NotReady, await index.RunAsync(new IndexRunOptions("tiny-tale"), TestContext.Current.CancellationToken));
        Assert.Equal("stage0-check", CliContext.Problems[0].Type);
        Assert.Contains("chapter-count", CliContext.Problems[0].Detail);

        // list, refetch (unchanged), remove.
        var row = Assert.Single(BookShelf.List(root.Path));
        Assert.Equal(("tiny-tale", true, null), (row.Id, row.SourceText, row.IndexedRun));
        Assert.False((await intake.RefetchAsync("tiny-tale", TestContext.Current.CancellationToken)).Changed);
        Assert.Equal(Exit.BadInput, runner.Remove("tiny-tale", yes: false));   // no terminal, no --yes
        Assert.Equal(Exit.Ok, runner.Remove("tiny-tale", yes: true));
        Assert.Empty(intake.Manifest());
        Assert.False(File.Exists(SourceTexts.PathOf(root.Path, "tiny-tale")));
        Assert.False(File.Exists(BookProfiles.OverridePath(root.Path, "tiny-tale")));
        Assert.True(Directory.Exists(RepoPaths.Runs(root.Path)));   // runs are kept
        CliContext.Reset();
    }

    [Fact]
    public async Task Books_add_falls_back_to_the_text_header_a_txt_url_a_local_file_and_reports_what_it_cannot_do()
    {
        using var root = new TempRoot();
        var text = Book("Short Pieces", "John Roe", "1. Alpha\n\nFirst text.\n\n2. Beta\n\nSecond text.");
        // The catalogue is down and the first text path is missing: the second path, after a pause; title and author from the header.
        var (intake, web, pauses) = Intake(root, uri => uri.Host == "gutendex.com" ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : uri.AbsolutePath.EndsWith("/pg99002.txt") || uri.Host == "example.org" ? Bytes(text) : new HttpResponseMessage(HttpStatusCode.NotFound));
        var ct = TestContext.Current.CancellationToken;

        var added = await intake.AddAsync(new BookAddOptions("99002"), ct);

        Assert.Equal(("short-pieces", "Short Pieces", "John Roe", "https://www.gutenberg.org/cache/epub/99002/pg99002.txt"), (added.Id, added.Title, added.Author, added.SourceUrl));
        Assert.Equal(["https://gutendex.com/books/99002", "https://www.gutenberg.org/ebooks/99002.txt.utf-8", "https://www.gutenberg.org/cache/epub/99002/pg99002.txt"], web.Requests.Select(r => r.Url));
        Assert.Equal([BookIntake.Pause], pauses);   // between the two text requests
        Assert.Equal(HeadingStyle.Numbered, added.Profile!.Headings);

        // A .txt URL on any host is used as it is (no catalogue), and --id, --title and --author win.
        var direct = await intake.AddAsync(new BookAddOptions("https://example.org/texts/pieces.txt", Id: "pieces-2", Title: "Pieces Again", Author: "J. Roe"), ct);
        Assert.Equal(("pieces-2", "Pieces Again", "J. Roe", null, "https://example.org/texts/pieces.txt"), (direct.Id, direct.Title, direct.Author, direct.Gutenberg, direct.SourceUrl));

        // A local file is copied in.
        var local = Path.Combine(root.Path, "mine.txt");
        File.WriteAllText(local, Book("My Own Book", "Me Myself", CapsBody));
        var copied = await intake.AddAsync(new BookAddOptions(local), ct);
        Assert.Equal("my-own-book", copied.Id);
        Assert.Equal(File.ReadAllBytes(local), File.ReadAllBytes(SourceTexts.PathOf(root.Path, "my-own-book")));

        // Bad input: not a ref, a bad id, a missing file, no title to be had. A download that fails is not bad input.
        await Assert.ThrowsAsync<BookInputException>(() => intake.AddAsync(new BookAddOptions("the hound"), ct));
        await Assert.ThrowsAsync<BookInputException>(() => intake.AddAsync(new BookAddOptions("99002", Id: "Not An Id"), ct));
        await Assert.ThrowsAsync<BookInputException>(() => intake.AddAsync(new BookAddOptions(Path.Combine(root.Path, "missing.txt")), ct));
        var bare = Path.Combine(root.Path, "bare.txt");
        File.WriteAllText(bare, "No header here.\r\n*** START OF THE PROJECT GUTENBERG EBOOK X ***\r\nChapter 1.\r\n\r\nText.\r\n*** END OF THE PROJECT GUTENBERG EBOOK X ***\r\n");
        var noTitle = await Assert.ThrowsAsync<BookInputException>(() => intake.AddAsync(new BookAddOptions(bare), ct));
        Assert.Contains("--title", noTitle.Message);
        Assert.Contains("--author", noTitle.Message);
        await Assert.ThrowsAsync<BookDownloadException>(() => intake.AddAsync(new BookAddOptions("99003"), ct));

        // A text with no chapter headings is added (text, manifest, an empty database) and reported: exit 4.
        File.WriteAllText(bare, Book("Plain", "No One", "Just prose, with no chapter headings at all."));
        CliContext.Reset();
        var (exit, plain) = await new BooksRunner(intake, root.Path, new TestConsole(), interactive: false).AddAsync(new BookAddOptions(bare), ct);
        Assert.Equal(Exit.CheckFailed, exit);
        Assert.Null(plain!.Profile);
        Assert.True(File.Exists(Path.Combine(RepoPaths.Books(root.Path), "plain.sqlite")));
        Assert.Equal(["short-pieces", "pieces-2", "my-own-book", "plain"], intake.Manifest().Select(b => b.Id));
        CliContext.Reset();
    }

    // ── index run ──────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Index_run_preflight_refuses_before_any_model_call()
    {
        var root = RepoPaths.FindRoot();
        var runsBefore = Directory.Exists(RepoPaths.Runs(root)) ? Directory.GetDirectories(RepoPaths.Runs(root)).Length : 0;
        IConfiguration Config(params (string Key, string Value)[] values) =>
            new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => KeyValuePair.Create(v.Key, (string?)v.Value))).Build();

        // No model configured: exit 3, pointing at setup.
        CliContext.Reset();
        var none = new IndexRunner(root, new TestConsole(), false, () => Config());
        Assert.Equal(Exit.NotReady, await none.RunAsync(new IndexRunOptions(Hound), TestContext.Current.CancellationToken));
        Assert.Equal("model-not-configured", Assert.Single(CliContext.Problems).Type);
        Assert.Contains($"{CliName.Wrapper} setup", CliContext.Problems[0].Detail);

        // A model and a price: the estimate is the measured tokens per chunk times the book's chunks, and --budget-usd refuses above it.
        var priced = new IndexRunner(root, new TestConsole(), false, () => Config(("Llm:Profiles:p:Type", "OpenAI"), ("Llm:Profiles:p:Model", "m"), ("Llm:Profiles:p:ApiKey", "fake-key-for-tests-0123456789"),
            ("Pricing:Currency", "USD"), ("Pricing:InputPerMillionTokens", "1"), ("Pricing:OutputPerMillionTokens", "5")));
        CliContext.Reset();
        var (plan, exit) = priced.Preflight(new IndexRunOptions(Hound, Llm: "p", BudgetUsd: 1000m));
        Assert.Equal(Exit.Ok, exit);
        Assert.Equal(((0, 5), (1, 15), 16), (plan!.Stages, plan.Chapters, plan.Estimate.Chunks));
        Assert.Equal(16 * IndexPlan.TokensPerChunk.Values.Sum(v => v.In), plan.Estimate.InputTokens);
        Assert.Equal(Math.Round((plan.Estimate.InputTokens * 1m + plan.Estimate.OutputTokens * 5m) / 1_000_000m, 2), plan.Estimate.Cost);
        Assert.Equal(Exit.NotReady, priced.Preflight(new IndexRunOptions(Hound, Llm: "p", BudgetUsd: 0.5m)).Exit);
        Assert.Equal("over-budget", CliContext.Problems[^1].Type);
        // Chapters 1-3 only, stages 1-2: fewer chunks, two stages.
        var part = priced.Preflight(new IndexRunOptions(Hound, Chapters: "1-3", Stages: "1-2", Llm: "p")).Plan!;
        Assert.True(part.Estimate.Chunks is > 0 and < 16);
        Assert.Equal(part.Estimate.Chunks * (IndexPlan.TokensPerChunk[1].In + IndexPlan.TokensPerChunk[2].In), part.Estimate.InputTokens);
        // Without a price the cost is not known; a dollar budget then refuses (see the budget tests below).
        var unpriced = new IndexRunner(root, new TestConsole(), false, () => Config(("Llm:Profiles:p:Type", "OpenAI"), ("Llm:Profiles:p:Model", "m"), ("Llm:Profiles:p:ApiKey", "fake-key-for-tests-0123456789")));
        Assert.Null(unpriced.Preflight(new IndexRunOptions(Hound, Llm: "p")).Plan!.Estimate.Cost);

        // Bad input and missing stage input.
        Assert.Equal(Exit.BadInput, priced.Preflight(new IndexRunOptions(Hound, Chapters: "9-99", Llm: "p")).Exit);
        Assert.Equal(Exit.BadInput, priced.Preflight(new IndexRunOptions(Hound, Stages: "0-5", FromRun: "x", Llm: "p")).Exit);
        Assert.Equal(Exit.NotReady, priced.Preflight(new IndexRunOptions(Hound, Stages: "4-5", Llm: "p")).Exit);
        Assert.Equal(Exit.BadInput, priced.Preflight(new IndexRunOptions(Hound, Stages: "4-5", FromRun: "no-such-run", Llm: "p")).Exit);
        Assert.Equal(runsBefore, Directory.Exists(RepoPaths.Runs(root)) ? Directory.GetDirectories(RepoPaths.Runs(root)).Length : 0);   // nothing was started
        CliContext.Reset();

        Assert.Equal((0, 5), IndexPlan.ParseStages(null));
        Assert.Equal((2, 5), IndexPlan.ParseStages("2..5"));
        Assert.Equal((3, 3), IndexPlan.ParseStages("3"));
        Assert.Null(IndexPlan.ParseStages("5-2"));
        Assert.Null(IndexPlan.ParseStages("6"));
    }

    // ── The budget guard ────────────────────────────────────────────────────────────────────────────────────────────────

    private static readonly (string Key, string Value)[] ProfileP =
        [("Llm:Profiles:p:Type", "OpenAI"), ("Llm:Profiles:p:Model", "model-p"), ("Llm:Profiles:p:ApiKey", "fake-key-for-tests-0123456789")];

    /// <summary>Profile p with a price of its own: 1 per million tokens in, 5 out.</summary>
    private static readonly (string Key, string Value)[] PricedP =
        [.. ProfileP, ("Llm:Profiles:p:Pricing:Currency", "USD"), ("Llm:Profiles:p:Pricing:InputPerMillionTokens", "1"), ("Llm:Profiles:p:Pricing:OutputPerMillionTokens", "5")];

    private static Func<IConfiguration> ConfigOf(params (string Key, string Value)[] values) =>
        () => new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => KeyValuePair.Create(v.Key, (string?)v.Value))).Build();

    /// <summary>A root with one small book ("tiny-tale", 3 chapters, 3 chunks), added through the intake with a fake web.</summary>
    private static async Task<TempRoot> RootWithTinyTale()
    {
        var root = new TempRoot();
        var (intake, _, _) = Intake(root, _ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var file = Path.Combine(root.Path, "tiny.txt");
        File.WriteAllText(file, Book("A Tiny Tale", "Jane Doe", CapsBody));
        Assert.True((await intake.AddAsync(new BookAddOptions(file), TestContext.Current.CancellationToken)).ChecksPassed);
        return root;
    }

    private static int Runs(string root) => Directory.Exists(RepoPaths.Runs(root)) ? Directory.GetDirectories(RepoPaths.Runs(root)).Length : 0;

    /// <summary>The run folders with a model call logged (every run folder starts with an empty calls.jsonl).</summary>
    private static int CallLogs(string root) => Directory.Exists(RepoPaths.Runs(root)) ? Directory.GetFiles(RepoPaths.Runs(root), RunFolder.CallsJsonl, SearchOption.AllDirectories).Count(f => new FileInfo(f).Length > 0) : 0;

    /// <summary>A dollar budget cannot be kept without a price, so nothing runs.</summary>
    [Fact]
    public async Task Budget_usd_without_a_price_is_exit_3_price_unknown_and_no_run_folder()
    {
        using var root = await RootWithTinyTale();
        var before = Runs(root.Path);
        var staged = new List<int>();
        CliContext.Reset();
        var runner = new IndexRunner(root.Path, new TestConsole(), false, ConfigOf(ProfileP), (stage, _, _) => { staged.Add(stage); return Task.FromResult(0); });

        var exit = await runner.RunAsync(new IndexRunOptions("tiny-tale", Llm: "p", BudgetUsd: 1m), TestContext.Current.CancellationToken);

        Assert.Equal(Exit.NotReady, exit);
        var problem = Assert.Single(CliContext.Problems);
        Assert.Equal("price-unknown", problem.Type);
        Assert.Equal("--budget-usd needs a configured price for profile 'p' and model 'model-p'; set Llm:Profiles:p:Pricing (input and output per million tokens) or use --budget-tokens. Nothing was run.", problem.Detail);
        Assert.Equal(before, Runs(root.Path));
        Assert.Empty(staged);
        // Stage 0 alone calls no model, so there is nothing to budget and nothing to refuse.
        Assert.Equal(Exit.Ok, runner.Preflight(new IndexRunOptions("tiny-tale", Stages: "0", Llm: "p", BudgetUsd: 1m)).Exit);
        // The price may also be the Pricing section the run folders use.
        var global = new IndexRunner(root.Path, new TestConsole(), false, ConfigOf([.. ProfileP, ("Pricing:InputPerMillionTokens", "1"), ("Pricing:OutputPerMillionTokens", "5")]));
        Assert.Equal(0.86m, global.Preflight(new IndexRunOptions("tiny-tale", Llm: "p", BudgetUsd: 5m)).Plan!.Estimate.Cost);
        CliContext.Reset();
    }

    /// <summary>With a price, an estimate over the dollar budget refuses, as before.</summary>
    [Fact]
    public async Task Budget_usd_with_a_price_and_a_higher_estimate_is_exit_3_over_budget()
    {
        using var root = await RootWithTinyTale();
        var before = Runs(root.Path);
        CliContext.Reset();
        var runner = new IndexRunner(root.Path, new TestConsole(), false, ConfigOf(PricedP), (_, _, _) => throw new InvalidOperationException("a stage ran"));

        var exit = await runner.RunAsync(new IndexRunOptions("tiny-tale", Llm: "p", BudgetUsd: 0.5m), TestContext.Current.CancellationToken);

        Assert.Equal(Exit.NotReady, exit);
        var problem = Assert.Single(CliContext.Problems);
        Assert.Equal("over-budget", problem.Type);
        Assert.StartsWith("The estimated cost, 0.86 USD, is over --budget-usd 0.50", problem.Detail);   // 3 chunks: 258,903 in at 1, 120,297 out at 5
        Assert.Contains(IndexPlan.EstimateBasis, problem.Detail);
        Assert.Equal(before, Runs(root.Path));
        Assert.Equal(Exit.Ok, runner.Preflight(new IndexRunOptions("tiny-tale", Llm: "p", BudgetUsd: 0.86m)).Exit);   // at the budget is not over it
        CliContext.Reset();
    }

    /// <summary>A token budget needs no price; the message carries the estimate and its basis.</summary>
    [Fact]
    public void Budget_tokens_below_the_estimate_is_exit_3_over_budget_with_the_estimate_and_its_basis()
    {
        var root = RepoPaths.FindRoot();
        var before = Runs(root);
        CliContext.Reset();
        var runner = new IndexRunner(root, new TestConsole(), false, ConfigOf(ProfileP));

        var (plan, exit) = runner.Preflight(new IndexRunOptions(Hound, Chapters: "1-1", Llm: "p", BudgetTokens: 1000));

        Assert.Null(plan);
        Assert.Equal(Exit.NotReady, exit);
        var problem = Assert.Single(CliContext.Problems);
        Assert.Equal("over-budget", problem.Type);
        // Hound ch. 1 is one chunk: 86,301 tokens in and 40,099 out over Stages 1 to 5.
        Assert.Equal($"The estimated tokens, 126,400 (86,301 in and 40,099 out for 1 chunks), are over --budget-tokens 1,000 ({IndexPlan.EstimateBasis}). Nothing was run.", problem.Detail);
        Assert.Equal(before, Runs(root));
        // Both budgets may be given; either one refuses. Here the price is known and the dollar budget is generous, the token budget is not.
        var priced = new IndexRunner(root, new TestConsole(), false, ConfigOf(PricedP));
        Assert.Equal(Exit.NotReady, priced.Preflight(new IndexRunOptions(Hound, Chapters: "1-1", Llm: "p", BudgetUsd: 1000m, BudgetTokens: 1000)).Exit);
        Assert.Equal("over-budget", CliContext.Problems[^1].Type);
        Assert.Contains("--budget-tokens", CliContext.Problems[^1].Detail);
        Assert.Equal(Exit.NotReady, priced.Preflight(new IndexRunOptions(Hound, Chapters: "1-1", Llm: "p", BudgetUsd: 0.01m, BudgetTokens: 100_000_000)).Exit);
        Assert.Contains("--budget-usd", CliContext.Problems[^1].Detail);
        Assert.Equal(Exit.BadInput, priced.Preflight(new IndexRunOptions(Hound, Llm: "p", BudgetTokens: -1)).Exit);
        CliContext.Reset();
    }

    /// <summary>A token budget above the estimate lets the pre-flight pass (the test stops there: no stage is run).</summary>
    [Fact]
    public void Budget_tokens_above_the_estimate_passes_the_preflight()
    {
        var root = RepoPaths.FindRoot();
        var before = Runs(root);
        CliContext.Reset();
        var runner = new IndexRunner(root, new TestConsole(), false, ConfigOf(ProfileP));

        var (plan, exit) = runner.Preflight(new IndexRunOptions(Hound, Chapters: "1-1", Llm: "p", BudgetTokens: 100_000_000));

        Assert.Equal(Exit.Ok, exit);
        Assert.Empty(CliContext.Problems);
        Assert.Equal((1, 86_301L, 40_099L, (decimal?)null, "p"), (plan!.Estimate.Chunks, plan.Estimate.InputTokens, plan.Estimate.OutputTokens, plan.Estimate.Cost, plan.Profile));
        Assert.Equal(Exit.Ok, runner.Preflight(new IndexRunOptions(Hound, Chapters: "1-1", Llm: "p", BudgetTokens: 126_400)).Exit);   // exactly the estimate
        Assert.Equal(Exit.NotReady, runner.Preflight(new IndexRunOptions(Hound, Chapters: "1-1", Llm: "p", BudgetTokens: 126_399)).Exit);
        Assert.Equal(before, Runs(root));
        CliContext.Reset();
    }

    private static TestConsole Terminal(Action<TestConsoleInput> keys)
    {
        var console = new TestConsole();
        console.Profile.Width = 200;
        console.Interactive();
        keys(console.Input);
        return console;
    }

    /// <summary>At a terminal, a run whose cost is not known needs an explicit yes; "n" and Enter alone both run nothing.</summary>
    [Theory]
    [InlineData("n")]
    [InlineData("")]
    public async Task At_a_terminal_an_unknown_cost_needs_an_explicit_yes(string answer)
    {
        using var root = await RootWithTinyTale();
        var before = Runs(root.Path);
        var staged = new List<int>();
        var console = Terminal(input => input.PushTextWithEnter(answer));
        CliContext.Reset();
        var runner = new IndexRunner(root.Path, console, interactive: true, ConfigOf(ProfileP), (stage, _, _) => { staged.Add(stage); return Task.FromResult(0); });

        var exit = await runner.RunAsync(new IndexRunOptions("tiny-tale", Llm: "p"), TestContext.Current.CancellationToken);

        Assert.Equal(Exit.Ok, exit);
        Assert.Contains("Cost is not known (no price configured for p). Start anyway? [y/n] (n):", console.Output);
        Assert.Contains("Nothing was run.", console.Output);
        Assert.Empty(staged);
        Assert.Equal(before, Runs(root.Path));
        Assert.Empty(CliContext.Problems);
    }

    /// <summary>The home menu's "Index a book" calls RunAsync with no book; the same prompt and the same default apply.</summary>
    [Fact]
    public async Task Through_the_menu_an_unknown_cost_needs_the_same_yes_and_yes_starts_it()
    {
        using var root = await RootWithTinyTale();
        var before = Runs(root.Path);
        var staged = new List<int>();
        Func<int, RunFolder, CancellationToken, Task<int>> fake = (stage, _, _) => { staged.Add(stage); return Task.FromResult(0); };
        // The menu: pick the book (Enter), then the one question; with an unknown cost Enter alone is "no".
        var declined = Terminal(input => { input.PushKey(ConsoleKey.Enter); input.PushKey(ConsoleKey.Enter); });
        CliContext.Reset();
        var first = await new IndexRunner(root.Path, declined, true, ConfigOf([.. ProfileP, ("Indexing:Llm", "p")]), fake).RunAsync(new IndexRunOptions(null), TestContext.Current.CancellationToken);
        Assert.True(first == Exit.Ok, string.Join(" | ", CliContext.Problems.Select(p => p.Detail)) + declined.Output);
        Assert.Contains("Start indexing? (the cost is not known: no price configured for p) [y/n] (n):", declined.Output);
        Assert.Contains("Nothing was run.", declined.Output);
        Assert.Empty(staged);
        Assert.Equal(before, Runs(root.Path));

        // "y" starts it: every stage and build-db, through the fake stage runner (no model).
        var accepted = Terminal(input => { input.PushKey(ConsoleKey.Enter); input.PushTextWithEnter("y"); });
        Assert.Equal(Exit.Ok, await new IndexRunner(root.Path, accepted, true, ConfigOf([.. ProfileP, ("Indexing:Llm", "p")]), fake).RunAsync(new IndexRunOptions(null), TestContext.Current.CancellationToken));
        Assert.Equal([1, 2, 3, 4, 5, 6], staged);
        Assert.Equal(before + 1, Runs(root.Path));
        Assert.Equal(0, CallLogs(root.Path));
        CliContext.Reset();
    }

    /// <summary>With a known cost the question is "Start?" and Enter starts the run.</summary>
    [Fact]
    public async Task At_a_terminal_a_known_cost_starts_on_enter()
    {
        using var root = await RootWithTinyTale();
        var before = Runs(root.Path);
        var staged = new List<int>();
        var console = Terminal(input => input.PushKey(ConsoleKey.Enter));
        CliContext.Reset();
        var runner = new IndexRunner(root.Path, console, interactive: true, ConfigOf(PricedP), (stage, _, _) => { staged.Add(stage); return Task.FromResult(0); });

        var exit = await runner.RunAsync(new IndexRunOptions("tiny-tale", Llm: "p"), TestContext.Current.CancellationToken);

        Assert.Equal(Exit.Ok, exit);
        Assert.Contains("cost about 0.86 USD", console.Output);
        Assert.Contains("Start? [y/n] (y):", console.Output);
        Assert.DoesNotContain("Start anyway?", console.Output);
        Assert.DoesNotContain("Nothing was run.", console.Output);
        Assert.Equal([1, 2, 3, 4, 5, 6], staged);   // Stage 0 is written by the runner itself; 6 is build-db
        Assert.Equal(before + 1, Runs(root.Path));
        var run = Directory.GetDirectories(RepoPaths.Runs(root.Path)).Single(d => File.Exists(Path.Combine(d, RunFolder.Stage0Json)) && File.ReadAllText(Path.Combine(d, RunFolder.RunJson)).Contains("index run"));
        Assert.True(File.Exists(Path.Combine(run, RunFolder.Stage0Json)));
        Assert.Contains("index run --book tiny-tale", File.ReadAllText(Path.Combine(run, RunFolder.RunJson)));
        Assert.Equal(0, CallLogs(root.Path));       // no model was called
        CliContext.Reset();
    }

    /// <summary>--yes skips the question in both cases; with an unknown cost it says so in one line on stderr.</summary>
    [Fact]
    public async Task Yes_skips_the_question_and_says_on_stderr_when_the_cost_is_not_known()
    {
        using var root = await RootWithTinyTale();
        const string notice = "Cost not known (no price configured); running because --yes was given.";
        foreach (var (config, expected) in new[] { (ConfigOf(ProfileP), notice + Environment.NewLine), (ConfigOf(PricedP), "") })
        {
            var staged = new List<int>();
            var stderr = new StringWriter();
            var console = Terminal(_ => { });   // no key is pushed: a question would fail the test
            CliContext.Reset();
            var runner = new IndexRunner(root.Path, console, interactive: true, config, (stage, _, _) => { staged.Add(stage); return Task.FromResult(0); }, stderr);

            var exit = await runner.RunAsync(new IndexRunOptions("tiny-tale", Llm: "p", Yes: true), TestContext.Current.CancellationToken);
            Assert.True(exit == Exit.Ok, string.Join(" | ", CliContext.Problems.Select(p => p.Detail)) + console.Output);

            // The notice and nothing else, apart from the next step.
            Assert.Equal(expected, string.Concat(stderr.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Where(l => !l.StartsWith("Next: ")).Select(l => l + Environment.NewLine)));
            Assert.DoesNotContain("[y/n]", console.Output);
            Assert.Equal([1, 2, 3, 4, 5, 6], staged);
        }
        Assert.Equal(0, CallLogs(root.Path));
        CliContext.Reset();
    }

    /// <summary>The envelope carries the new problem types. Each command line is refused at the pre-flight whatever this
    /// machine has configured (the one-token budget refuses when a price is known).</summary>
    [Fact]
    public async Task The_json_envelope_carries_price_unknown_and_over_budget()
    {
        var root = RepoPaths.FindRoot();
        var before = Runs(root);

        var (exit, stdout) = await Run("index", "run", "--book", Hound, "--chapters", "1-1", "--budget-tokens", "1000", "--json");
        var json = JsonNode.Parse(stdout)!;
        Assert.Equal((Exit.NotReady, false, "index run"), (exit, json["ok"]!.GetValue<bool>(), json["command"]!.GetValue<string>()));
        var problem = Assert.Single(json["problems"]!.AsArray())!;
        Assert.Equal("over-budget", problem["type"]!.GetValue<string>());
        Assert.Contains("126,400", problem["detail"]!.GetValue<string>());
        Assert.Contains(IndexPlan.EstimateBasis, problem["detail"]!.GetValue<string>());
        Assert.Null(json["result"]);

        (exit, stdout) = await Run("index", "run", "--book", Hound, "--budget-usd", "1", "--budget-tokens", "1", "--json");
        var usd = Assert.Single(JsonNode.Parse(stdout)!["problems"]!.AsArray())!;
        Assert.Equal(Exit.NotReady, exit);
        Assert.Contains(usd["type"]!.GetValue<string>(), new[] { "price-unknown", "over-budget" });
        if (usd["type"]!.GetValue<string>() == "price-unknown") Assert.Contains("or use --budget-tokens", usd["detail"]!.GetValue<string>());
        Assert.Equal(before, Runs(root));
    }

    /// <summary>The envelope's price-unknown, whatever this machine has configured: the runner with a profile that has no price.</summary>
    [Fact]
    public async Task Price_unknown_reaches_the_envelope_as_a_problem()
    {
        using var root = await RootWithTinyTale();
        CliContext.Reset();
        var exit = await new IndexRunner(root.Path, new TestConsole(), false, ConfigOf(ProfileP)).RunAsync(new IndexRunOptions("tiny-tale", Llm: "p", BudgetUsd: 1m), TestContext.Current.CancellationToken);

        var envelope = JsonNode.Parse(OutputCapture.Envelope("index run", exit, "", "", CliContext.Result, CliContext.Problems))!;

        Assert.Equal((false, 3), (envelope["ok"]!.GetValue<bool>(), envelope["exit"]!.GetValue<int>()));
        Assert.Equal(["type", "title", "detail"], envelope["problems"]![0]!.AsObject().Select(kv => kv.Key));
        Assert.Equal(("price-unknown", "Not ready"), (envelope["problems"]![0]!["type"]!.GetValue<string>(), envelope["problems"]![0]!["title"]!.GetValue<string>()));
        Assert.Null(envelope["result"]);
        CliContext.Reset();
    }

    // ── Index rebuild and the launch scripts ────────────────────────────────────────────────────────────────────────────

    private static readonly (string Key, string Value)[] IndexingP = [.. ProfileP, ("Indexing:Llm", "p")];

    /// <summary>A root with two small books, "tiny-tale" then "short-pieces", in that manifest order.</summary>
    private static async Task<TempRoot> RootWithTwoBooks()
    {
        var root = await RootWithTinyTale();
        var (intake, _, _) = Intake(root, _ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var file = Path.Combine(root.Path, "pieces.txt");
        File.WriteAllText(file, Book("Short Pieces", "John Roe", "1. Alpha\n\nFirst text.\n\n2. Beta\n\nSecond text."));
        Assert.True((await intake.AddAsync(new BookAddOptions(file), TestContext.Current.CancellationToken)).ChecksPassed);
        return root;
    }

    private static JsonNode RebuildResult() => System.Text.Json.JsonSerializer.SerializeToNode(CliContext.Result!, RunFolder.Json)!;

    /// <summary>The dry run lists every manifest book with its estimate and the sum, and runs nothing.</summary>
    [Fact]
    public async Task Rebuild_dry_run_lists_all_three_books_with_estimates_and_runs_nothing()
    {
        var root = RepoPaths.FindRoot();
        var before = Runs(root);
        var staged = new List<int>();
        var console = new TestConsole();
        console.Profile.Width = 200;
        CliContext.Reset();
        var runner = new IndexRunner(root, console, false, ConfigOf(IndexingP), (stage, _, _) => { staged.Add(stage); return Task.FromResult(0); });

        var exit = await runner.RebuildAsync(new RebuildOptions([], DryRun: true), TestContext.Current.CancellationToken);

        Assert.Equal(Exit.Ok, exit);
        var result = RebuildResult();
        Assert.True(result["dryRun"]!.GetValue<bool>());
        var books = result["books"]!.AsArray();
        Assert.Equal(["hound-of-the-baskervilles", "three-musketeers", "treasure-island"], books.Select(b => b!["book"]!.GetValue<string>()));   // manifest order
        Assert.Equal([(15, 16), (67, 73), (34, 34)], books.Select(b => (b!["chapters"]!.GetValue<int>(), b["chunks"]!.GetValue<int>())));
        Assert.All(books, b => Assert.Equal(b!["chunks"]!.GetValue<int>() * 86_301L, b["estimatedInputTokens"]!.GetValue<long>()));
        Assert.All(books, b => Assert.Null(b!["estimatedCost"]));
        Assert.All(books, b => Assert.Null(b!["runId"]));
        Assert.Equal((123, 123 * 86_301L, 123 * 40_099L), (result["estimate"]!["chunks"]!.GetValue<int>(), result["estimate"]!["inputTokens"]!.GetValue<long>(), result["estimate"]!["outputTokens"]!.GetValue<long>()));
        Assert.Null(result["estimate"]!["cost"]);
        Assert.Contains("Rebuild plan", console.Output);
        Assert.Contains("10,615,023", console.Output);   // the summed estimate, tokens in
        Assert.Contains("not known", console.Output);
        Assert.Contains("Dry run: nothing was run.", console.Output);
        Assert.Empty(staged);
        Assert.Equal(before, Runs(root));
        CliContext.Reset();

        // Through the command line and the envelope. Without a model on the machine the pre-flight refuses (3); either way nothing runs.
        var (code, stdout) = await Run("index", "rebuild", "--dry-run", "--json");
        var json = JsonNode.Parse(stdout)!;
        Assert.Equal("index rebuild", json["command"]!.GetValue<string>());
        Assert.Contains(code, new[] { Exit.Ok, Exit.NotReady });
        if (code == Exit.Ok) Assert.Equal(3, json["result"]!["books"]!.AsArray().Count);
        else Assert.Equal("model-not-configured", json["problems"]![0]!["type"]!.GetValue<string>());
        Assert.Equal(before, Runs(root));
    }

    /// <summary>--book picks a subset, in the order given.</summary>
    [Fact]
    public async Task Rebuild_dry_run_with_two_books_lists_exactly_those_in_that_order()
    {
        var root = RepoPaths.FindRoot();
        var before = Runs(root);
        CliContext.Reset();
        var runner = new IndexRunner(root, new TestConsole(), false, ConfigOf(IndexingP), (_, _, _) => throw new InvalidOperationException("a stage ran"));

        Assert.Equal(Exit.Ok, await runner.RebuildAsync(new RebuildOptions(["treasure-island", "three-musketeers"], DryRun: true), TestContext.Current.CancellationToken));

        Assert.Equal(["treasure-island", "three-musketeers"], RebuildResult()["books"]!.AsArray().Select(b => b!["book"]!.GetValue<string>()));
        Assert.Equal(34 + 73, RebuildResult()["estimate"]!["chunks"]!.GetValue<int>());
        Assert.Equal(Exit.BadInput, await runner.RebuildAsync(new RebuildOptions(["treasure-island", "no-such-book"], DryRun: true), TestContext.Current.CancellationToken));
        Assert.Equal(before, Runs(root));
        CliContext.Reset();
    }

    /// <summary>The budgets apply per book; the first book over one is named and nothing runs.</summary>
    [Fact]
    public async Task Rebuild_with_a_token_budget_of_one_is_exit_3_over_budget_naming_the_first_book()
    {
        var root = RepoPaths.FindRoot();
        var before = Runs(root);
        CliContext.Reset();
        var runner = new IndexRunner(root, new TestConsole(), false, ConfigOf(IndexingP), (_, _, _) => throw new InvalidOperationException("a stage ran"));

        var exit = await runner.RebuildAsync(new RebuildOptions([], BudgetTokens: 1, DryRun: true), TestContext.Current.CancellationToken);

        Assert.Equal(Exit.NotReady, exit);
        var problem = Assert.Single(CliContext.Problems);
        Assert.Equal("over-budget", problem.Type);
        Assert.StartsWith("hound-of-the-baskervilles: The estimated tokens, 2,022,400 ", problem.Detail);
        Assert.Equal(3, RebuildResult()["books"]!.AsArray().Count);   // the plan is still the result
        // Not a dry run: the same refusal, before anything runs. A budget between two books' estimates names the first one over it.
        CliContext.Reset();
        Assert.Equal(Exit.NotReady, await runner.RebuildAsync(new RebuildOptions([], BudgetTokens: 5_000_000, Yes: true), TestContext.Current.CancellationToken));
        Assert.StartsWith("three-musketeers: ", Assert.Single(CliContext.Problems).Detail);
        // A dollar budget without a price refuses for the first book.
        CliContext.Reset();
        Assert.Equal(Exit.NotReady, await runner.RebuildAsync(new RebuildOptions([], BudgetUsd: 100m, DryRun: true), TestContext.Current.CancellationToken));
        Assert.Equal("price-unknown", Assert.Single(CliContext.Problems).Type);
        Assert.Equal(before, Runs(root));
        CliContext.Reset();
    }

    /// <summary>A stage that fails on the first book stops the rebuild with its exit code; the second book is never started.</summary>
    [Fact]
    public async Task Rebuild_stops_at_the_first_book_that_fails_and_does_not_start_the_next()
    {
        using var root = await RootWithTwoBooks();
        var before = Runs(root.Path);
        var staged = new List<(string Run, int Stage)>();
        CliContext.Reset();
        var runner = new IndexRunner(root.Path, new TestConsole(), false, ConfigOf(IndexingP),
            (stage, run, _) => { staged.Add((run.RunId, stage)); return Task.FromResult(stage == 3 ? Exit.Failed : Exit.Ok); });

        var exit = await runner.RebuildAsync(new RebuildOptions([]), TestContext.Current.CancellationToken);

        Assert.Equal(Exit.Failed, exit);
        Assert.Equal([1, 2, 3], staged.Select(s => s.Stage));               // book 1 stopped at Stage 3
        Assert.Single(staged.Select(s => s.Run).Distinct());                // one run folder: book 2 was never started
        Assert.Equal(before + 1, Runs(root.Path));
        var books = RebuildResult()["books"]!.AsArray();
        Assert.Equal(["tiny-tale", "short-pieces"], books.Select(b => b!["book"]!.GetValue<string>()));
        Assert.Equal((Exit.Failed, false), (books[0]!["exit"]!.GetValue<int>(), books[0]!["promoted"]!.GetValue<bool>()));
        Assert.Null(books[1]!["exit"]);
        Assert.Null(books[1]!["runId"]);
        Assert.Equal(0, CallLogs(root.Path));
        CliContext.Reset();
    }

    /// <summary>A failed leak test (build-db's exit 4) stops the rebuild with exit 4, and that book is not promoted.</summary>
    [Fact]
    public async Task Rebuild_with_a_failed_leak_test_is_exit_4_and_promotes_nothing_for_that_book()
    {
        using var root = await RootWithTwoBooks();
        var staged = new List<int>();
        CliContext.Reset();
        var runner = new IndexRunner(root.Path, new TestConsole(), false, ConfigOf(IndexingP),
            (stage, run, _) =>
            {
                staged.Add(stage);
                if (stage != 6) return Task.FromResult(Exit.Ok);
                run.Write(Pipeline.LeakTestsJson, new { passed = false });
                return Task.FromResult(CliContext.Problem(Exit.CheckFailed, "Leak tests failed (spoilers-on)", "leak-test"));
            });

        var exit = await runner.RebuildAsync(new RebuildOptions([], Yes: true), TestContext.Current.CancellationToken);

        Assert.Equal(Exit.CheckFailed, exit);
        Assert.Equal([1, 2, 3, 4, 5, 6], staged);                            // the whole of book 1, nothing of book 2
        Assert.Contains(CliContext.Problems, p => p.Type == "leak-test");
        Assert.False(File.Exists(IndexPrepareCommand.ProvenancePath(root.Path, "tiny-tale")));   // not promoted
        var books = RebuildResult()["books"]!.AsArray();
        Assert.Equal((Exit.CheckFailed, false), (books[0]!["exit"]!.GetValue<int>(), books[0]!["promoted"]!.GetValue<bool>()));
        Assert.Null(books[1]!["exit"]);
        Assert.Equal(0, CallLogs(root.Path));
        CliContext.Reset();
    }

    /// <summary>One question for the whole set; with an unknown cost Enter alone runs nothing.</summary>
    [Fact]
    public async Task Rebuild_at_a_terminal_with_an_unknown_cost_runs_nothing_on_enter()
    {
        using var root = await RootWithTwoBooks();
        var before = Runs(root.Path);
        var staged = new List<int>();
        Func<int, RunFolder, CancellationToken, Task<int>> fake = (stage, _, _) => { staged.Add(stage); return Task.FromResult(Exit.Ok); };
        var console = Terminal(input => input.PushKey(ConsoleKey.Enter));
        CliContext.Reset();

        var exit = await new IndexRunner(root.Path, console, true, ConfigOf(IndexingP), fake).RebuildAsync(new RebuildOptions([]), TestContext.Current.CancellationToken);

        Assert.Equal(Exit.Ok, exit);
        Assert.Contains("Cost is not known for 2 of 2 books (no price configured for p). Rebuild anyway? [y/n] (n):", console.Output);
        Assert.Contains("Nothing was run.", console.Output);
        Assert.Empty(staged);
        Assert.Equal(before, Runs(root.Path));

        // With a price for every book the question is "Rebuild 2 books?" and Enter starts: both books, one question, each in its own run.
        var priced = Terminal(input => input.PushKey(ConsoleKey.Enter));
        var stderr = new StringWriter();
        exit = await new IndexRunner(root.Path, priced, true, ConfigOf([.. PricedP, ("Indexing:Llm", "p")]), fake, stderr).RebuildAsync(new RebuildOptions([]), TestContext.Current.CancellationToken);
        Assert.Equal(Exit.Ok, exit);
        Assert.Contains("Rebuild 2 books? [y/n] (y):", priced.Output);
        Assert.DoesNotContain("Start?", priced.Output);
        Assert.Equal([1, 2, 3, 4, 5, 6, 1, 2, 3, 4, 5, 6], staged);
        Assert.Equal(before + 2, Runs(root.Path));
        // Nothing on stderr but each book's next step.
        Assert.All(stderr.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries), l => Assert.StartsWith("Next: ", l));
        var books = RebuildResult()["books"]!.AsArray();
        Assert.All(books, b => Assert.Equal(Exit.Ok, b!["exit"]!.GetValue<int>()));
        Assert.Equal(2, books.Select(b => b!["runId"]!.GetValue<string>()).Distinct().Count());
        // --yes with an unknown cost: no question, one line on stderr for the whole rebuild.
        staged.Clear();
        var silent = Terminal(_ => { });
        exit = await new IndexRunner(root.Path, silent, true, ConfigOf(IndexingP), fake, stderr).RebuildAsync(new RebuildOptions(["short-pieces"], Yes: true), TestContext.Current.CancellationToken);
        Assert.Equal(Exit.Ok, exit);
        Assert.Single(stderr.ToString().Split(Environment.NewLine), "Cost not known (no price configured); running because --yes was given.");
        Assert.Equal([1, 2, 3, 4, 5, 6], staged);
        Assert.Equal(0, CallLogs(root.Path));
        CliContext.Reset();
    }

    /// <summary>The launch scripts hold the lines they should, and "no command" is the menu's case whatever --repo adds.</summary>
    [Fact]
    public void The_launch_scripts_pass_everything_through_with_the_repo_root()
    {
        var root = RepoPaths.FindRoot();
        string Script(string name) => File.ReadAllText(Path.Combine(root, name)).Replace("\r\n", "\n");
        Assert.EndsWith("root=\"$(cd \"$(dirname \"$0\")\" && pwd)\"\nexec dotnet run --project \"$root/tools/TemporalGraph.Cli\" -- \"$@\" --repo \"$root\"\n", Script("temporalgraph.cli.sh"));
        Assert.EndsWith("dotnet run --project \"$PSScriptRoot/tools/TemporalGraph.Cli\" -- @args --repo \"$PSScriptRoot\"; exit $LASTEXITCODE\n", Script("temporalgraph.cli.ps1"));
        Assert.EndsWith("exec \"$(dirname \"$0\")/temporalgraph.cli.sh\" index rebuild \"$@\"\n", Script("temporalgraph.rebuild.sh"));
        Assert.EndsWith("& \"$PSScriptRoot/temporalgraph.cli.ps1\" index rebuild @args; exit $LASTEXITCODE\n", Script("temporalgraph.rebuild.ps1"));
        Assert.Equal("#!/usr/bin/env sh\nexec \"$(dirname \"$0\")/temporalgraph.cli.sh\" setup \"$@\"\n", Script("setup.sh"));
        Assert.Equal("& \"$PSScriptRoot/temporalgraph.cli.ps1\" setup @args; exit $LASTEXITCODE\n", Script("setup.ps1"));
        Assert.All(new[] { "temporalgraph.cli.sh", "temporalgraph.rebuild.sh", "setup.sh" }, s => Assert.StartsWith("#!/usr/bin/env sh\n", Script(s)));

        Assert.True(CliApp.NoCommand([]));
        Assert.True(CliApp.NoCommand(["--repo", "/some/where"]));          // what the script sends for no arguments
        Assert.True(CliApp.NoCommand(["--json", "--repo", "books"]));      // a folder named like a command is still the --repo value
        Assert.False(CliApp.NoCommand(["--help", "--repo", "/some/where"]));
        Assert.False(CliApp.NoCommand(["books", "list", "--repo", "/some/where"]));
        Assert.Equal("index rebuild", CliApp.CommandName(["index", "rebuild", "--dry-run", "--repo", "/some/where"]));
    }

    [Fact]
    public async Task Without_a_terminal_no_command_is_the_help_and_exit_2_even_with_repo()
    {
        var (exit, stdout) = await Run("--repo", RepoPaths.FindRoot(), "--json");
        var json = JsonNode.Parse(stdout)!;
        Assert.Equal((Exit.BadInput, "home"), (exit, json["command"]!.GetValue<string>()));
        Assert.Equal("no-terminal", json["problems"]![0]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void Index_status_reads_a_run_folder_and_cancel_leaves_its_file()
    {
        using var root = new TempRoot();
        var run = RunFolder.Create(RepoPaths.Runs(root.Path));
        var pricing = new TemporalGraph.Llm.Pricing(null, null, null);
        run.WriteStageCost("stage-1", pricing.CostReport(10, 1000, 200), pricing.CostReport);
        run.WriteStageCost("stage-2", pricing.CostReport(4, 500, 50), pricing.CostReport);
        run.RecordCommand(new { command = "index mentions" }, new Dictionary<string, object?> { ["book"] = "a-book" });
        var console = new TestConsole();
        console.Profile.Width = 200;
        var runner = new IndexRunner(root.Path, console, false, () => new ConfigurationBuilder().Build());
        CliContext.Reset();

        Assert.Equal(Exit.Ok, runner.Status(null, run.RunId));

        Assert.Equal(["stage-1", "stage-2"], IndexPlan.Stages(run.Path).Select(s => s.Stage));
        Assert.Equal((14L, 1500L, 250L, (decimal?)null), IndexPlan.Total(IndexPlan.Stages(run.Path)) is var t ? (t.Calls, t.InputTokens, t.OutputTokens, t.Cost) : default);
        Assert.Contains("stage-2", console.Output);
        Assert.Contains("not known", console.Output);                       // no price configured
        Assert.Contains("Tokens by stage", console.Output);                 // the breakdown, by tokens then
        Assert.Contains("cost.json", console.Output);                       // the tree of the folder
        Assert.Equal(Exit.BadInput, runner.Status(null, "no-such-run"));
        Assert.Equal(Exit.BadInput, runner.Cancel("no-such-run"));
        Assert.Equal(Exit.Ok, runner.Cancel(run.RunId));
        Assert.True(File.Exists(run.File(IndexRunner.CancelFile)));
        Assert.Equal("a-book", BookShelf.RunBook(run.Path));
        CliContext.Reset();
    }

    // ── the consoles ───────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Query_console_runs_queries_and_dot_commands()
    {
        using var root = new TempRoot().WithHound();
        var graph = BookGraph.Load(Path.Combine(RepoPaths.Books(root.Path), Hound + ".sqlite"));
        var session = new QuerySession(graph, Hound, root.Path, 15, false, null);
        var console = new TestConsole();
        console.Profile.Width = 240;
        var lines = new ScriptedLines([
            ".schema",
            "MATCH (c:Character) WHERE visible(c)",          // a query over two lines, ended by ";"
            "RETURN count(c) AS characters;",
            ".chapter 1",
            "MATCH (c:Character) WHERE visible(c) RETURN count(c) AS characters",
            "",                                               // … or by a blank line
            ".spoilers on", ".character Holmes", ".params",
            ".chapter 99", ".nonsense", "MATCH (n RETURN n;", // each says what is wrong and the console goes on
            ".samples", "",                                   // the picked file goes into the buffer; Enter runs it
            ".json on", "RETURN 1 AS one;", ".json off",
            ".timeline", ".save my-count", ".save my-count", ".help", ".quit",
            "RETURN 2 AS afterquit;",
        ]);
        var picked = new List<int>();

        var exit = await new QueryConsole(session, console, lines, files => { picked.Add(files.Count); return files.Single(f => f.Name == "characters-so-far"); }).RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(Exit.Ok, exit);
        var output = console.Output;
        // The prompt shows the state; a continuation line has its own.
        Assert.Equal($"{Hound} › ch 15 › spoilers off › ", lines.Prompts[0]);
        Assert.Equal("… ", lines.Prompts[2]);
        Assert.Contains($"{Hound} › ch 1 › spoilers on › Holmes › ", lines.Prompts);
        // .schema: the full schema's labels and edge types, with counts.
        foreach (var name in new[] { "Character", "Location", "Event", "Fact", "PARTICIPATED_IN", "DISCOVERED", "PERCEIVES", "MENTIONED_IN", "IDENTICAL_TO", "LOCATED_AT", "RELATED_TO" })
            Assert.Contains(name, output);
        var provenance = JsonNode.Parse(File.ReadAllText(Path.Combine(RepoPaths.FindRoot(), "data", "books", "provenance", Hound + ".json")))!;
        Assert.Contains(provenance["counts"]!["labels"]!["Character"]!.GetValue<int>().ToString(), output);   // every character by ch. 15 (the count of the promoted graph, whatever it is)
        Assert.Contains("$pos = 16159", output);                             // .params at ch. 1
        Assert.Contains("$character = Holmes", output);
        Assert.Contains(".chapter needs a number from 1 to 15", output);
        Assert.Contains("Unknown command .nonsense", output);
        Assert.Contains("at line 1", output);                                // the engine's message for a query that does not parse
        Assert.Equal([16], picked);   // this root has the sixteen tool queries (no samples folder)
        Assert.Contains("In the buffer", output);
        Assert.Contains("\"one\"", output);                                  // .json on
        Assert.Contains("anchor", output);                                   // .timeline
        Assert.Contains(provenance["anchor"]!["edtf"]!.GetValue<string>(), output);   // the run's own anchor date
        Assert.Contains("Saved queries/hound-of-the-baskervilles/my-count.cypher", output);
        Assert.Contains("my-count.cypher exists", output);
        Assert.Equal("// Saved from the query console.\n// Parameters: .\nRETURN 1 AS one\n", File.ReadAllText(Path.Combine(root.Path, "queries", Hound, "my-count.cypher")));
        Assert.Contains(".timeline", output);                                // .help
        Assert.DoesNotContain("afterquit", output);                          // nothing after .quit (a word no fact of the book has)
        Assert.Equal((1, true, "Holmes"), (session.Chapter, session.Spoilers, session.Character));
    }

    [Fact]
    public async Task Chat_console_keeps_the_conversation_across_a_chapter_and_starts_a_new_one_for_another_character()
    {
        using var root = new TempRoot().WithHound();
        using var server = new FakeLlmServer();
        var config = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "templates", "cli.appsettings.json"))
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Llm:Type"] = "OpenAI", ["Llm:Model"] = "my-deployment", ["Llm:Endpoint"] = server.BaseUrl + "/openai/v1/", ["Llm:ApiKey"] = "fake-key-for-tests-0123456789",
            }).Build();
        void Reply(string who, string answer)
        {
            server.OpenAIMessages.Enqueue(FakeLlmServer.ToolCall("call_" + server.OpenAIMessages.Count, "facts_about", new { character = who }));
            server.OpenAIMessages.Enqueue(FakeLlmServer.Text(answer));
        }
        Reply("Holmes", "I am a consulting detective.");
        Reply("Holmes", "A baronet has come from Canada.");
        Reply("Watson", "I am a doctor, lately of the army.");
        CliContext.Reset();
        var (session, opened) = ChatSession.Open(root.Path, config, Hound, null);
        Assert.Equal(Exit.Ok, opened);
        using var _ = session;
        Assert.Equal("Sherlock Holmes", session!.Roster[0]);
        Assert.Null(session.Begin("Sherlock Holmes", 3, false));
        var console = new TestConsole();
        console.Profile.Width = 200;
        var lines = new ScriptedLines([
            "Who are you?", ".looked", ".chapter 5", "What is new?",
            ".chapter 99", ".character Irene Adler",          // refused, the conversation stays as it is
            ".character Watson", "And who are you?", ".cost", ".save", ".help", ".quit", "never asked",
        ]);

        var exit = await new ChatConsole(session, console, lines).RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(Exit.Ok, exit);
        var output = console.Output;
        // Each reply: the answer under the display name, who answered with the tokens, what was looked up.
        Assert.Contains("I am a consulting detective.", output);
        Assert.Contains("Holmes", output);
        Assert.Contains("Answered by " + FakeLlmServer.OpenAIModel, output);
        Assert.Contains("cost not known", output);
        Assert.Contains("What I looked up:", output);
        Assert.Contains("facts_about(character: \"Holmes\")", output);
        Assert.Contains("row(s) in", output);                                 // .looked
        Assert.Contains("No chapter 99", output);
        Assert.Contains("No character goes by 'Irene Adler'", output);
        Assert.Contains("a new conversation", output);
        Assert.Contains("3 turn(s), 6 model call(s)", output);                // .cost
        Assert.DoesNotContain("never asked", string.Join("\n", server.Requests.Select(r => r.Body)));
        // The prompt shows who is being talked to (by the display name) and where the reader is.
        var prompts = lines.Prompts.Distinct().ToList();
        Assert.Equal(3, prompts.Count);
        Assert.Equal("Sherlock Holmes · ch 3 › ", prompts[0]);
        Assert.Equal("Sherlock Holmes · ch 5 › ", prompts[1]);
        Assert.Matches("^(Dr\\. )?Watson · ch 5 › $", prompts[2]);

        // Six requests: two per turn. The turn after .chapter 5 carries the first turn and the pack of chapter 5; the turn with Watson
        // carries neither Holmes's turns nor his name in the prompt.
        Assert.Equal(6, server.Requests.Count);
        var third = JsonNode.Parse(server.Requests[2].Body)!["messages"]!.AsArray();
        Assert.Contains("up to the end of chapter 5.", third[0]!["content"]!.GetValue<string>());
        Assert.Equal(["system", "system", "user", "assistant", "user"], third.Select(m => m!["role"]!.GetValue<string>()));
        Assert.Equal(("Who are you?", "I am a consulting detective.", "What is new?"), (third[2]!["content"]!.GetValue<string>(), third[3]!["content"]!.GetValue<string>(), third[4]!["content"]!.GetValue<string>()));
        var fifth = JsonNode.Parse(server.Requests[4].Body)!["messages"]!.AsArray();
        Assert.Equal(["system", "system", "user"], fifth.Select(m => m!["role"]!.GetValue<string>()));
        Assert.Matches("^You are (Dr\\. )?Watson,", fifth[0]!["content"]!.GetValue<string>());
        Assert.DoesNotContain("consulting detective", server.Requests[4].Body);

        // The run folder: the transcript as chat --message writes it, run.json and cost.json; never the key.
        var turns = File.ReadAllLines(session.Run.File(ChatJsonl.FileName)).Select(l => JsonNode.Parse(l)!).Where(l => l["type"]!.GetValue<string>() == "turn").ToList();
        Assert.Equal(["Who are you?", "What is new?", "And who are you?"], turns.Select(t => t["question"]!.GetValue<string>()));
        var runJson = JsonNode.Parse(File.ReadAllText(session.Run.File(RunFolder.RunJson)))!;
        Assert.Equal(("Dr. Watson", 5, 3), (runJson["character"]!.GetValue<string>(), runJson["chapter"]!.GetValue<int>(), runJson["questions"]!.GetValue<int>()));
        Assert.True(File.Exists(session.Run.File(RunFolder.CostJson)));
        foreach (var file in Directory.GetFiles(session.Run.Path)) Assert.DoesNotContain("fake-key-for-tests-0123456789", File.ReadAllText(file));
        CliContext.Reset();
    }

    [Fact]
    public void Chat_console_needs_a_model_an_indexed_book_and_a_known_book()
    {
        using var root = new TempRoot().WithHound();
        IConfiguration empty = new ConfigurationBuilder().Build();
        CliContext.Reset();
        Assert.Equal(Exit.NotReady, ChatSession.Open(root.Path, empty, Hound, null).Exit);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Llm:Type"] = "OpenAI", ["Llm:Model"] = "m", ["Llm:Endpoint"] = "http://localhost:1/openai/v1/", ["Llm:ApiKey"] = "fake-key-for-tests-0123456789",
        }).Build();
        Assert.Equal(Exit.BadInput, ChatSession.Open(root.Path, config, "no-such-book", null).Exit);
        File.Delete(Path.Combine(RepoPaths.Books(root.Path), Hound + ".sqlite"));
        Assert.Equal(Exit.NotReady, ChatSession.Open(root.Path, config, Hound, null).Exit);
        Assert.Equal(["model-not-configured", "bad-input", "book-not-indexed"], CliContext.Problems.Select(p => p.Type));
        CliContext.Reset();
    }

    [Fact]
    public void A_promoted_book_without_queries_gets_the_tool_queries_of_the_full_schema()
    {
        using var root = new TempRoot().WithHound();
        var copied = IndexRunner.SeedQueries(root.Path, "a-new-book");
        Assert.Equal(16, copied.Count);
        Assert.Empty(ChatTools.MissingFiles(Path.Combine(root.Path, "queries", "a-new-book")));
        Assert.Empty(IndexRunner.SeedQueries(root.Path, "a-new-book"));   // a folder that exists is left as it is
        Assert.Empty(IndexRunner.SeedQueries(root.Path, Hound));
    }

    [Fact]
    public void The_home_menu_has_the_seven_entries_and_one_status_line()
    {
        Assert.Equal(["Add a book from Project Gutenberg", "Index a book", "Query a book", "Chat with a character", "Books and runs", "Set up a model", "Exit"], HomeCommand.Menu);
        using var root = new TempRoot().WithHound();
        var line = HomeCommand.StatusLine(root.Path, new ConfigurationBuilder().Build());
        Assert.Equal("Chat model: not set up · Indexing model: not set up · 1 book, 0 indexed", line);
    }
}
