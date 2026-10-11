using System.Text.Json;
using Microsoft.Extensions.Configuration;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using TemporalGraph.Cli;
using TemporalGraph.Cli.Indexing;
using TemporalGraph.Cli.Runs;
using TemporalGraph.Cli.Scoring;

namespace TemporalGraph.Data.Tests;

// Stage 1 self-consistency voting, and frozen Stage 1 input for Stage 2. No network (in-memory stub client).
public class VotingAndFrozenTests
{
    private const string Hound = "hound-of-the-baskervilles";
    private static readonly string Text = IndexingShared.BookText(Hound);
    private static readonly Stage0Result Stage0 = IndexingShared.Stage0For(Hound);
    private static string Root => RepoPaths.FindRoot();

    /// <summary>A mention at [start, start + text length) with evidence [evStart, evEnd).</summary>
    private static Mention M(string text, int start, int evStart, int evEnd, MentionType type = MentionType.Character, int chunk = 0) =>
        new(0, text, type, chunk, 1, "first", start, start + text.Length, "quote", new EvidenceMatch("exact", 1, evStart, evEnd));

    // ---- Vote matching ------------------------------------------------------------------------------

    [Fact]
    public void Votes_count_samples_that_found_the_same_type_text_and_overlapping_evidence()
    {
        IReadOnlyList<Mention>[] samples =
        [
            [M("Holmes", 100, 90, 130), M("Watson", 200, 190, 240)],
            [M("Holmes", 100, 95, 125), M("Baker Street", 300, 290, 330, MentionType.Location), M("Watson", 200, 190, 240, MentionType.Location)],
            [M("holmes", 100, 80, 110), M("Watson", 201, 195, 245)],   // case differs; Watson's offsets differ by one
        ];

        var voted = Stage1.Vote(samples);

        Assert.Equal(3, voted.Single(m => m.Text.Equals("Holmes", StringComparison.OrdinalIgnoreCase)).Votes);
        var watson = voted.Single(m => m.Text == "Watson" && m.Type == MentionType.Character);
        Assert.Equal(2, watson.Votes);
        Assert.Equal(200, watson.CharStart);                                                      // most common offsets; ties → earliest sample
        Assert.Equal(1, voted.Single(m => m.Text == "Watson" && m.Type == MentionType.Location).Votes); // another type is another mention
        Assert.Equal(1, voted.Single(m => m.Text == "Baker Street").Votes);
        Assert.Equal("Holmes", voted.First().Text);                                               // the earliest sample's surface form
    }

    [Fact]
    public void Evidence_must_overlap_and_each_sample_votes_once()
    {
        IReadOnlyList<Mention>[] samples =
        [
            [M("Holmes", 100, 90, 130), M("Holmes", 100, 90, 130), M("Holmes", 5000, 4990, 5030)], // a repeat, and a second occurrence far away
            [M("Holmes", 5002, 4995, 5030)],
        ];

        var voted = Stage1.Vote(samples);

        Assert.Equal(2, voted.Count);
        Assert.Equal(1, voted.Single(m => m.CharStart == 100).Votes);   // the repeat in sample 0 counts once
        Assert.Equal(2, voted.Single(m => m.CharStart == 5000).Votes);  // matched only where the evidence overlaps
    }

    [Fact]
    public void Two_occurrences_in_one_quote_stay_two_mentions()
    {
        // "Holmes … Holmes" inside one evidence quote, in both samples.
        IReadOnlyList<Mention>[] samples =
        [
            [M("Holmes", 100, 90, 160), M("Holmes", 140, 90, 160)],
            [M("Holmes", 140, 90, 160), M("Holmes", 100, 90, 160)],
        ];

        var voted = Stage1.Vote(samples);

        Assert.Equal([(100, 2), (140, 2)], voted.Select(m => (m.CharStart, m.Votes)));
    }

    [Fact]
    public void Possessives_and_curly_quotes_normalize_to_the_same_mention()
    {
        IReadOnlyList<Mention>[] samples = [[M("Mortimer’s", 100, 90, 130)], [M("Mortimer", 100, 90, 130)]];
        Assert.Equal(2, Assert.Single(Stage1.Vote(samples)).Votes);
    }

    [Fact]
    public void Min_votes_refilters_the_pool_without_new_calls()
    {
        var pool = Stage1.Vote([
            [M("Holmes", 100, 90, 130), M("Watson", 200, 190, 240)],
            [M("Holmes", 100, 90, 130)],
            [M("Holmes", 100, 90, 130), M("Watson", 200, 190, 240), M("Mortimer", 300, 290, 330)],
        ]);
        var r = new Stage1Result(Hound, 1, 1, false, 0.9, [new ChunkSummary(0, 1, 0, 1000, 6, 0, 3, 0)], pool, [], 0, Samples: 3, MinVotes: 1, Pool: pool);

        Assert.Equal(["Holmes", "Watson", "Mortimer"], Stage1.WithMinVotes(r, 1).Mentions.Select(m => m.Text));
        Assert.Equal(["Holmes", "Watson"], Stage1.WithMinVotes(r, 2).Mentions.Select(m => m.Text));
        var three = Stage1.WithMinVotes(r, 3);
        Assert.Equal(["Holmes"], three.Mentions.Select(m => m.Text));
        Assert.Equal(3, three.MinVotes);
        Assert.Equal(1, three.Chunks[0].Accepted);
        Assert.Throws<ArgumentOutOfRangeException>(() => Stage1.WithMinVotes(r, 4));
        Assert.Throws<InvalidOperationException>(() => Stage1.WithMinVotes(r with { Pool = null }, 2));
    }

    [Fact]
    public void Voting_settings_are_validated()
    {
        Assert.Empty(new VotingSettings(3, 2).Validate());
        Assert.NotEmpty(new VotingSettings(0, 1).Validate());
        Assert.NotEmpty(new VotingSettings(3, 4).Validate());
        Assert.NotEmpty(new VotingSettings(3, 0).Validate());
        var template = new ConfigurationBuilder().AddJsonFile(Path.Combine(AppContext.BaseDirectory, "templates", "cli.appsettings.json")).Build();
        var voting = IndexingShared.IndexingSettings.From(template).Voting;
        Assert.Empty(voting.Validate());
        Assert.Equal(IndexingShared.IndexingSettings.DefaultSamples, voting.Samples);
        Assert.Equal(IndexingShared.IndexingSettings.DefaultMinVotes, voting.MinVotes);
        Assert.Equal((5, 4), (voting.Samples, voting.MinVotes)); // the default
        var settings = IndexingShared.IndexingSettings.From(template);
        Assert.Equal((5, 2), (settings.Stage3Voting!.Samples, settings.Stage3Voting.MinVotes));
        Assert.Equal((5, 3), (settings.Stage4Voting!.Samples, settings.Stage4Voting.MinVotes));
        var defaults = IndexingShared.IndexingSettings.From(new ConfigurationBuilder().Build());
        Assert.Equal((IndexingShared.IndexingSettings.DefaultStage3Samples, IndexingShared.IndexingSettings.DefaultStage3MinVotes), (defaults.Stage3Voting!.Samples, defaults.Stage3Voting.MinVotes));
        Assert.Equal((IndexingShared.IndexingSettings.DefaultStage4Samples, IndexingShared.IndexingSettings.DefaultStage4MinVotes), (defaults.Stage4Voting!.Samples, defaults.Stage4Voting.MinVotes));
    }

    // ---- Stage 1 with k samples -----------------------------------------------------------------------

    /// <summary>A chunk-1 sentence and two capitalized words in it.</summary>
    private static (ChunkInfo Chunk, string Sentence, string A, string B) Fixture()
    {
        var chunk = Stage0.Chunks.First(c => c.Chapter == 1);
        var chunkText = Text[chunk.CharStart..chunk.CharEnd];
        var sentence = chunkText.Split("\r\n").Select(l => l.Trim()).First(l => Regex.Matches(l, @"\b[A-Z][a-z]{3,}\b").Select(m => m.Value).Distinct().Count() >= 2);
        var words = Regex.Matches(sentence, @"\b[A-Z][a-z]{3,}\b").Select(m => m.Value).Distinct().ToList();
        return (chunk, sentence, words[0], words[1]);
    }

    private static string Reply(params (string Text, string Evidence)[] ms) =>
        JsonSerializer.Serialize(new { mentions = ms.Select(m => new { text = m.Text, type = "Character", evidence = m.Evidence }) });

    /// <summary>Call n (0-based, in arrival order) gets <paramref name="reply"/>(n). Thread-safe: samples run in parallel.</summary>
    private sealed class CountingStub(Func<int, string> reply) : IChatClient
    {
        private int _calls = -1;
        public int Calls => _calls + 1;
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply(Interlocked.Increment(ref _calls)))));
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [Fact]
    public async Task Stage1_with_k_samples_keeps_mentions_with_at_least_m_votes_and_records_the_votes()
    {
        var (chunk, sentence, a, b) = Fixture();
        // Every sample finds A; only one of the three finds B.
        var client = new CountingStub(n => n == 0 ? Reply((a, sentence), (b, sentence)) : Reply((a, sentence)));
        var skill = OldSkills.Load(SkillFile.ExtractMentions);

        var r = await Stage1.RunAsync(client, null, skill, Stage0 with { Chunks = [chunk] }, Text, 1, 1, glean: false, threshold: 0.9, run: null,
            TestContext.Current.CancellationToken, new VotingSettings(Samples: 3, MinVotes: 2));

        Assert.Equal(3, client.Calls);
        Assert.Equal((3, 2), (r.Samples, r.MinVotes));
        var kept = Assert.Single(r.Mentions);
        Assert.Equal((a, 3), (kept.Text, kept.Votes));
        Assert.Equal([(a, 3), (b, 1)], r.Pool!.Select(m => (m.Text, m.Votes)).OrderByDescending(x => x.Votes));
        Assert.Equal(4, r.Chunks[0].Returned);   // 2 + 1 + 1 over the samples
        Assert.Equal(1, r.Chunks[0].Accepted);
        Assert.Equal(new[] { a, b }.Order(), Stage1.WithMinVotes(r, 1).Mentions.Select(m => m.Text).Order());
    }

    [Fact]
    public async Task One_sample_is_a_single_extraction_with_one_vote_each()
    {
        var (chunk, sentence, a, _) = Fixture();
        var client = new CountingStub(_ => Reply((a, sentence)));
        var skill = OldSkills.Load(SkillFile.ExtractMentions);

        var r = await Stage1.RunAsync(client, null, skill, Stage0 with { Chunks = [chunk] }, Text, 1, 1, glean: false, threshold: 0.9, run: null, TestContext.Current.CancellationToken);

        Assert.Equal(1, client.Calls);
        Assert.Equal(1, Assert.Single(r.Mentions).Votes);
    }

    // ---- Frozen Stage 1 input ---------------------------------------------------------------------------

    [Fact]
    public async Task Frozen_input_copies_stage1_into_a_new_run_and_stage2_runs_on_exactly_those_mentions()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "TemporalGraph.Data.Tests", Guid.NewGuid().ToString("N"));
        var runs = Path.Combine(tmp, "runs");
        try
        {
            // A source run: Stage 1 with k = 3, m = 2, written like index mentions writes it.
            var (chunk, sentence, a, b) = Fixture();
            var source = RunFolder.Create(runs);
            var client = new CountingStub(n => n == 0 ? Reply((a, sentence), (b, sentence)) : Reply((a, sentence)));
            var skill = OldSkills.Load(SkillFile.ExtractMentions);
            var stage1 = await Stage1.RunAsync(client, null, skill, Stage0 with { Chunks = [chunk] }, Text, 1, 1, false, 0.9, source,
                TestContext.Current.CancellationToken, new VotingSettings(3, 2));
            Stage1File.WriteStage1(source, stage1, skill.Record());
            source.AppendFailure(new { type = FailureTags.QuoteMismatch, stage = Stage1.StageName, runId = source.RunId });
            source.AppendFailure(new { type = FailureTags.SpuriousEntity, stage = ScoreCommand.Stage, runId = source.RunId });
            source.RecordCommand(new { command = "index mentions" }, new Dictionary<string, object?>
            {
                ["book"] = Hound,
                ["chapters"] = new { first = 1, last = 3 },
                ["textSha256"] = Stage0.TextSha256,
                ["skills"] = new Dictionary<string, object> { [skill.Name] = skill.Record() },
                ["settings"] = new { chunkTokens = 1000, overlapTokens = 100, glean = false, samples = 3, minVotes = 2 },
                ["sampling"] = new Dictionary<string, object> { [Stage1.StageName] = new { temperature = 0, seed = 1 } },
            });

            var frozen = FrozenStage1.Copy(source, runs);
            var reFiltered = FrozenStage1.Copy(source, runs, minVotes: 1);

            Assert.NotEqual(source.RunId, frozen.RunId);
            Assert.Equal("", File.ReadAllText(frozen.File(RunFolder.CallsJsonl)));     // no calls
            var copied = IndexingShared.Read<Stage1Result>(frozen, RunFolder.Stage1Json);
            Assert.Equal(stage1.Mentions.Select(m => (m.Text, m.CharStart, m.Votes)), copied.Mentions.Select(m => (m.Text, m.CharStart, m.Votes)));
            Assert.Equal([a], copied.Mentions.Select(m => m.Text));
            Assert.Equal(2, IndexingShared.Read<Stage1Result>(reFiltered, RunFolder.Stage1Json).Mentions.Count);

            var runJson = JsonNode.Parse(File.ReadAllText(frozen.File(RunFolder.RunJson)))!;
            Assert.Equal(source.RunId, runJson["stage1From"]!["runId"]!.GetValue<string>());
            Assert.Equal(3, runJson["chapters"]!["last"]!.GetValue<int>());
            Assert.NotNull(runJson["skills"]![SkillFile.ExtractMentions]);
            Assert.NotNull(runJson["sampling"]![Stage1.StageName]);
            Assert.Empty(KeepRule.PlanDifferences([source, frozen]));                // same chapters, same chunk plan
            Assert.Equal(1, JsonNode.Parse(File.ReadAllText(reFiltered.File(RunFolder.RunJson)))!["settings"]!["minVotes"]!.GetValue<int>());
            var failures = File.ReadAllLines(frozen.File(RunFolder.FailuresJsonl)).Select(l => JsonNode.Parse(l)!).ToList();
            Assert.Equal([Stage1.StageName], failures.Select(f => f["stage"]!.GetValue<string>()));  // Stage 1 lines only, not old scoring lines

            // Stage 2 on the frozen copy sees exactly the source's mentions.
            var resolver = new CountingStub(_ => """{"sameEntity":false,"canonicalName":null,"reason":"test"}""");
            var stage2 = await Stage2.RunAsync(resolver, null, SkillFile.Load(Root, SkillFile.ResolveEntities), copied.BookId, copied.Mentions, new ResolveSettings(), frozen, TestContext.Current.CancellationToken);
            Assert.Equal([a], stage2.Entities.Select(e => e.CanonicalName));
        }
        finally { if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true); }
    }

    [Fact]
    public void Old_stage1_files_without_a_pool_still_load()
    {
        var json = """{"bookId":"x","firstChapter":1,"lastChapter":1,"glean":false,"evidenceThreshold":0.9,"chunks":[],"mentions":[{"id":1,"text":"A","type":"Character","chunk":0,"chapter":1,"pass":"first","charStart":1,"charEnd":2,"evidence":"A","evidenceMatch":{"method":"exact","score":1,"charStart":1,"charEnd":2}}],"rejected":[],"duplicatesDropped":0}""";
        var r = JsonSerializer.Deserialize<Stage1Result>(json, RunFolder.Json)!;
        Assert.Equal((1, 1), (r.Samples, r.MinVotes));
        Assert.Null(r.Pool);
        Assert.Equal(1, r.Mentions[0].Votes);
    }
}
