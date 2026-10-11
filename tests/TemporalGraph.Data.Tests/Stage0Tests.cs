using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using TemporalGraph.Cli;
using TemporalGraph.Cli.Indexing;
using TemporalGraph.Llm;
using TemporalGraph.Cli.Runs;

namespace TemporalGraph.Data.Tests;

// Stage 0 on the committed texts, the run folder and runs diff. No network (the LLM tests use FakeLlmServer).
public class Stage0Tests
{
    public static TheoryData<string, int, string, string> Books() => new()
    {
        { "hound-of-the-baskervilles", 15, "Mr. Sherlock Holmes", "A Retrospection" },
        { "three-musketeers", 67, "THE THREE PRESENTS OF D’ARTAGNAN THE ELDER", "CONCLUSION" },
        { "treasure-island", 34, "The Old Sea-dog at the Admiral Benbow", "And Last" },
    };

    [Theory]
    [MemberData(nameof(Books))]
    public void Stage0_passes_every_check(string id, int chapters, string firstTitle, string lastTitle)
    {
        var r = Stage0.Prepare(BookProfiles.For(id), SourceTexts.Read(id), SourceTexts.Sha256(id));

        Assert.All(r.Checks, c => Assert.True(c.Passed, $"{c.Name}: {c.Details}"));
        Assert.Equal(["text-hash", "chapter-count", "chapter-numbers", "skipped-parts", "titles", "coverage", "chunks"], r.Checks.Select(c => c.Name));
        Assert.Equal(chapters, r.Chapters.Count);
        Assert.Equal(firstTitle, r.Chapters[0].Title);
        Assert.Equal(lastTitle, r.Chapters[^1].Title);
    }

    [Fact]
    public void Musketeers_preface_and_epilogue_are_skipped_and_wrapped_titles_join()
    {
        const string id = "three-musketeers";
        var r = Stage0.Prepare(BookProfiles.For(id), SourceTexts.Read(id), SourceTexts.Sha256(id));

        Assert.Equal(["front matter (title, contents)", "AUTHOR’S PREFACE", "EPILOGUE"], r.Skipped.Select(p => p.Name));
        // Chapter IV's and XVI's titles wrap onto a second line; LIX has a second title line.
        Assert.Equal("THE SHOULDER OF ATHOS, THE BALDRIC OF PORTHOS AND THE HANDKERCHIEF OF ARAMIS", r.Chapters[3].Title);
        Assert.Equal("WHAT TOOK PLACE AT PORTSMOUTH AUGUST 23, 1628", r.Chapters[58].Title);
    }

    [Fact]
    public void Treasure_island_bare_roman_headings_parts_skipped_and_chapter_17_fixed()
    {
        const string id = "treasure-island";
        var r = Stage0.Prepare(BookProfiles.For(id), SourceTexts.Read(id), SourceTexts.Sha256(id));
        var text = System.Text.Encoding.UTF8.GetString(SourceTexts.Read(id));

        // The six part headings are skipped parts of their own, never chapters, and no chapter's text holds one.
        Assert.Equal(["front matter (title, contents)", "PART ONE--The Old Buccaneer", "PART TWO--The Sea-cook", "PART THREE--My Shore Adventure",
            "PART FOUR--The Stockade", "PART FIVE--My Sea Adventure", "PART SIX--Captain Silver"], r.Skipped.Select(p => p.Name));
        Assert.All(r.Chapters, c => Assert.DoesNotContain("PART ", text[c.CharStart..c.CharEnd]));
        Assert.Equal("VI", r.Chapters[5].Heading);
        Assert.Equal("I Go to Bristol", r.Chapters[6].Title);   // chapter 7 starts after PART TWO, not inside chapter 6
        // Chapter 17's heading reads "XXVII" in the text; the profile fixes it by title, and the check says so.
        Assert.Equal(("XXVII", 17, "Narrative Continued by the Doctor: The Jolly-boat’s Last Trip"), (r.Chapters[16].Heading, r.Chapters[16].Number, r.Chapters[16].Title));
        Assert.Contains("fixed from the profile: \"XXVII\"", Assert.Single(r.Checks, c => c.Name == "chapter-numbers").Details);
        // Contents: 34 entries; wrapped titles joined, dot leaders and page numbers dropped; compared without case.
        Assert.Equal(34, r.Contents.Count);
        Assert.Equal("NARRATIVE CONTINUED BY THE DOCTOR: HOW THE SHIP WAS ABANDONED", r.Contents[15].Title);
        Assert.Equal("THE MAN OF THE ISLAND", r.Contents[14].Title);

        // Without the fix the numbering check fails on the duplicate XXVII.
        var unfixed = Stage0.Prepare(BookProfiles.For(id) with { NumberFixes = null }, SourceTexts.Read(id), SourceTexts.Sha256(id));
        Assert.Contains(unfixed.Checks, c => c.Name == "chapter-numbers" && !c.Passed);
    }

    [Fact]
    public void Chapter_count_check_fails_when_the_expectation_differs()
    {
        const string id = "hound-of-the-baskervilles";
        var wrong = BookProfiles.For(id) with { ExpectedChapters = 14 };

        var r = Stage0.Prepare(wrong, SourceTexts.Read(id), SourceTexts.Sha256(id));

        Assert.False(r.Passed);
        Assert.Equal("found 15, expected 14", Assert.Single(r.Checks, c => !c.Passed).Details);
    }

    [Fact]
    public void Text_hash_check_fails_when_the_embedded_hash_differs()
    {
        const string id = "hound-of-the-baskervilles";
        var r = Stage0.Prepare(BookProfiles.For(id), SourceTexts.Read(id), new string('0', 64));

        Assert.Equal("text-hash", Assert.Single(r.Checks, c => !c.Passed).Name);
    }

    [Fact]
    public void Run_folder_has_every_file_and_no_key()
    {
        using var root = new TempRoot();
        var outcome = IndexPrepareCommand.Run(root.Path, ["hound-of-the-baskervilles", "three-musketeers"], "index prepare --all", writeDatabase: false);

        Assert.True(outcome.Passed);
        foreach (var file in new[] { RunFolder.RunJson, RunFolder.Stage0Json, RunFolder.ChecksJson, RunFolder.CallsJsonl, RunFolder.CostJson })
            Assert.True(File.Exists(outcome.Run.File(file)), $"{file} missing");
        Assert.Equal("", File.ReadAllText(outcome.Run.File(RunFolder.CallsJsonl)));

        var cost = JsonNode.Parse(File.ReadAllText(outcome.Run.File(RunFolder.CostJson)))!;
        // The command reads this machine's settings: "unknown" while the Pricing section is empty (the committed template), else the
        // price of no calls.
        if (Pricing.From(LlmSettings.LoadConfiguration()).Known) Assert.Equal(0m, cost["cost"]!.GetValue<decimal>());
        else Assert.Equal("unknown", cost["cost"]!.GetValue<string>());

        var run = JsonNode.Parse(File.ReadAllText(outcome.Run.File(RunFolder.RunJson)))!;
        Assert.Equal(outcome.Run.RunId, run["runId"]!.GetValue<string>());
        Assert.Equal(2, run["books"]!.AsArray().Count);

        AssertNoKey(outcome.Run.Path);
    }

    [Fact]
    public void Runs_diff_of_two_identical_runs_is_empty_and_spots_a_change()
    {
        using var root = new TempRoot();
        var a = IndexPrepareCommand.Run(root.Path, ["hound-of-the-baskervilles"], "index prepare --book hound-of-the-baskervilles", writeDatabase: false);
        var b = IndexPrepareCommand.Run(root.Path, ["hound-of-the-baskervilles"], "index prepare --book hound-of-the-baskervilles", writeDatabase: false);

        Assert.NotEqual(a.Run.RunId, b.Run.RunId);
        Assert.Empty(RunDiff.Compare(a.Run.Path, b.Run.Path));

        // A moved chapter boundary shows up.
        var stage0 = b.Run.File(RunFolder.Stage0Json);
        var json = JsonNode.Parse(File.ReadAllText(stage0))!;
        json["books"]![0]!["chapters"]![2]!["charStart"] = 1;
        File.WriteAllText(stage0, json.ToJsonString());
        Assert.Contains(RunDiff.Compare(a.Run.Path, b.Run.Path), d => d.StartsWith("book hound-of-the-baskervilles chapter 3:"));
    }

    [Fact]
    public async Task Llm_calls_go_to_calls_jsonl_with_run_tags_and_no_key()
    {
        using var root = new TempRoot();
        using var server = new FakeLlmServer();
        var run = RunFolder.Create(Path.Combine(root.Path, "runs"));
        var settings = FakeSettings(server);
        var context = new LlmCallContext { Run = run, Stage = "llm-check" };

        using var client = ChatClientFactory.Create(settings, maxRetries: 0, new LlmPipelineOptions(CaptureContent: true, context));
        var response = await client.AsAIAgent(name: "llm-check").RunAsync(LlmCheckCommand.Prompt, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("ready", response.Text);
        var line = JsonNode.Parse(Assert.Single(File.ReadAllLines(run.File(RunFolder.CallsJsonl))))!;
        Assert.Equal("call", line["type"]!.GetValue<string>());
        Assert.Equal(run.RunId, line["runId"]!.GetValue<string>());
        Assert.Equal("my-deployment", line["model"]!.GetValue<string>());
        Assert.Equal(FakeLlmServer.OpenAIModel, line["response"]!["modelId"]!.GetValue<string>());
        Assert.Equal(11, line["usage"]!["input"]!.GetValue<int>());
        Assert.Contains(LlmCheckCommand.Prompt, line["request"]!.ToJsonString());
        Assert.NotNull(line["latencyMs"]);

        var log = client.GetService<CallLogChatClient>()!;
        Assert.Equal((1, 11, 1), (log.Calls, log.InputTokens, log.OutputTokens));
        AssertNoKey(run.Path, settings.ApiKey!);
    }

    [Theory]
    [InlineData("OpenAI")]
    [InlineData("Anthropic")]
    public async Task Rate_limited_call_is_retried_with_a_logged_retry(string type)
    {
        using var root = new TempRoot();
        using var server = new FakeLlmServer(rateLimitFirst: 1);
        var run = RunFolder.Create(Path.Combine(root.Path, "runs"));
        var context = new LlmCallContext { Run = run, Stage = "test" };
        var waits = new List<TimeSpan>();
        var pipeline = new LlmPipelineOptions(CallContext: context, RetryDelay: (t, _) => { waits.Add(t); return Task.CompletedTask; });

        using var client = type == "OpenAI"
            ? ChatClientFactory.Create(FakeSettings(server), maxRetries: 0, pipeline)
            : ChatClientFactory.Create(FakeSettings(server, anthropic: true), maxRetries: 0, server.BaseUrl, pipeline);
        var response = await client.GetResponseAsync(LlmCheckCommand.Prompt, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("ready", response.Text);
        Assert.Equal([RetryOnTransientChatClient.Backoff(1)], waits);
        Assert.Equal(2, server.Requests.Count);
        var lines = File.ReadAllLines(run.File(RunFolder.CallsJsonl)).Select(l => JsonNode.Parse(l)!).ToList();
        Assert.Equal(["call", "retry", "call"], lines.Select(l => l["type"]!.GetValue<string>()));
        Assert.NotNull(lines[0]["error"]);
        Assert.Equal("429", lines[1]["reason"]!.GetValue<string>());
        Assert.Null(lines[2]["error"]);
    }

    private static LlmSettings FakeSettings(FakeLlmServer server, bool anthropic = false) =>
        LlmSettings.From(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Llm:Type"] = anthropic ? "Anthropic" : "OpenAI",
            ["Llm:Model"] = "my-deployment",
            ["Llm:Endpoint"] = anthropic ? null : server.BaseUrl + "/openai/v1/",
            ["Llm:ApiKey"] = "fake-key-for-tests-0123456789",
        }).Build());

    /// <summary>No run file contains the key: the test key, and the configured one if any (never printed).</summary>
    private static void AssertNoKey(string runPath, string? extraKey = null)
    {
        var keys = new[] { extraKey, LlmSettings.From(LlmSettings.LoadConfiguration()).ApiKey }.Where(k => !string.IsNullOrEmpty(k)).ToList();
        foreach (var file in Directory.GetFiles(runPath))
        {
            var content = File.ReadAllText(file);
            Assert.False(content.Contains("ApiKey", StringComparison.OrdinalIgnoreCase), $"{Path.GetFileName(file)} mentions ApiKey");
            foreach (var key in keys)
                Assert.False(content.Contains(key!), $"{Path.GetFileName(file)} contains an API key"); // message never shows the key
        }
    }

    [Fact]
    public void Prepare_keeps_a_promoted_book_unless_forced()
    {
        using var root = new TempRoot();
        const string hound = "hound-of-the-baskervilles", musketeers = "three-musketeers";
        var books = TemporalGraph.Cli.Runs.RepoPaths.Books(root.Path);
        Directory.CreateDirectory(Path.Combine(books, "provenance"));
        var houndDb = Path.Combine(books, hound + ".sqlite");
        var provenance = IndexPrepareCommand.ProvenancePath(root.Path, hound);
        File.WriteAllText(provenance, "{\"runId\":\"x\"}");
        File.WriteAllBytes(houndDb, [1, 2, 3]);   // stands in for a database holding a promoted graph

        // Default (as the AppHost's prepare --all): Hound is skipped, untouched; Musketeers is rebuilt.
        var plain = IndexPrepareCommand.Run(root.Path, [hound, musketeers], "index prepare --all", writeDatabase: true);
        Assert.True(plain.Passed);
        Assert.Equal([hound], plain.Protected);
        Assert.Empty(plain.Dropped);
        Assert.Equal([1, 2, 3], File.ReadAllBytes(houndDb));
        Assert.True(File.Exists(provenance));
        Assert.True(new FileInfo(Path.Combine(books, musketeers + ".sqlite")).Length > 3);
        var runJson = JsonNode.Parse(File.ReadAllText(plain.Run.File(RunFolder.RunJson)))!;
        Assert.Equal(hound, runJson["settings"]!["databaseSkipped"]![0]!.GetValue<string>());

        // --force: rebuilt from Stage 0 (empty graph), provenance dropped.
        var forced = IndexPrepareCommand.Run(root.Path, [hound], "index prepare --book hound-of-the-baskervilles --force", writeDatabase: true, force: true);
        Assert.Empty(forced.Protected);
        Assert.Equal([hound], forced.Dropped);
        Assert.False(File.Exists(provenance));
        using (var db = BookDatabase.OpenReadOnlyFile(houndDb))
        {
            Assert.Equal(15, db.Chapters.Count());
            Assert.Equal(0, db.Nodes.Count());
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        // Without a provenance file the book is rebuilt as before, and nothing is reported.
        var again = IndexPrepareCommand.Run(root.Path, [hound], "index prepare --book hound-of-the-baskervilles", writeDatabase: true);
        Assert.Empty(again.Protected);
        Assert.Empty(again.Dropped);
    }

    private sealed class TempRoot : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "TemporalGraph.Data.Tests", Guid.NewGuid().ToString("N"));
        /// <summary>With the source texts and the profile overrides, which Stage 0 now reads from the repo root.</summary>
        public TempRoot()
        {
            var repo = TemporalGraph.Cli.Runs.RepoPaths.FindRoot();
            foreach (var folder in new[] { "source", "profiles" })
            {
                var target = Directory.CreateDirectory(System.IO.Path.Combine(Path, "data", "books", folder)).FullName;
                foreach (var file in Directory.GetFiles(System.IO.Path.Combine(repo, "data", "books", folder)))
                    File.Copy(file, System.IO.Path.Combine(target, System.IO.Path.GetFileName(file)));
            }
        }
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
    }
}
