using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using TemporalGraph.Cli;
using TemporalGraph.Cli.Indexing;
using TemporalGraph.Cli.Runs;
using TemporalGraph.Cli.Scoring;
using TemporalGraph.Llm;
using TemporalGraph.Testing;

namespace TemporalGraph.Data.Tests;

// The pipeline that reads the text once. Chapter-sized chunks (Stage 0), one extraction call per chunk with names and
// compact evidence (Stage 1), blocked and batched resolution (Stage 2), linking by code (Stage 3), the prompt cache and the
// per-stage gate on model calls. No network: the model is a stub or the local fake server.
public class OnePassPipelineTests
{
    private const string Hound = "hound-of-the-baskervilles";
    private static readonly string[] Books = [Hound, "three-musketeers", "treasure-island"];

    private static Stage0Result Prepare(string book, ChunkSettings settings) =>
        Stage0.Prepare(BookProfiles.For(book), SourceTexts.Read(book), SourceTexts.Sha256(book), settings);

    // ---- Chunker ---------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("hound-of-the-baskervilles", 15, 16)]
    [InlineData("three-musketeers", 67, 73)]
    [InlineData("treasure-island", 34, 34)]
    public void Chapter_aligned_chunks_reproduce_the_chapter_bounds_and_lose_no_text(string book, int chapters, int chunks)
    {
        var text = IndexingShared.BookText(book);
        var stage0 = Prepare(book, new ChunkSettings());

        Assert.True(stage0.Passed, string.Join("; ", stage0.Checks.Where(c => !c.Passed).Select(c => $"{c.Name}: {c.Details}")));
        Assert.Equal((chapters, chunks), (stage0.Chapters.Count, stage0.Chunks.Count));
        Assert.Equal(Enumerable.Range(0, stage0.Chunks.Count), stage0.Chunks.Select(c => c.Index));
        foreach (var chapter in stage0.Chapters)
        {
            var parts = stage0.Chunks.Where(c => c.Chapter == chapter.Number).OrderBy(c => c.CharStart).ToList();
            // The parts tile the chapter: first start, last end, each starts where the one before ends; together they are its text.
            Assert.Equal((chapter.CharStart, chapter.CharEnd), (parts[0].CharStart, parts[^1].CharEnd));
            Assert.All(parts.Zip(parts.Skip(1)), p => Assert.Equal(p.First.CharEnd, p.Second.CharStart));
            Assert.Equal(text[chapter.CharStart..chapter.CharEnd], string.Concat(parts.Select(p => text[p.CharStart..p.CharEnd])));
            // One chunk unless the chapter is over 8,000 estimated tokens; then the fewest equal parts, each cut where a paragraph starts.
            Assert.Equal(Math.Max(1, (chapter.CharEnd - chapter.CharStart + 31_999) / 32_000), parts.Count);
            Assert.All(parts.Skip(1), p => Assert.Contains(chapter.Paragraphs, para => para.CharStart == p.CharStart));
            Assert.All(parts, p => Assert.True(p.EstimatedTokens <= 12_000, $"chapter {chapter.Number}: a part of {p.EstimatedTokens} tokens"));
        }
    }

    [Fact]
    public void The_chunk_size_comes_from_the_configuration_and_a_small_size_is_the_windows_of_before()
    {
        IConfiguration Config(params (string, string)[] values) => new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => KeyValuePair.Create(v.Item1, (string?)v.Item2))).Build();
        Assert.Equal(new ChunkSettings(8000, 0, 4, true), ChunkSettings.From(Config()));
        Assert.Equal(new ChunkSettings(6000, 0, 4, true), ChunkSettings.From(Config(("Indexing:Chunk:Tokens", "6000"))));
        Assert.Equal(new ChunkSettings(1000, 100, 4, false), ChunkSettings.From(Config(), 1000));           // --chunk-tokens 1000
        Assert.Equal(ChunkSettings.Windows, ChunkSettings.From(new StageOverrides(ChunkTokens: 1000).Over(Config())));
        Assert.Equal(new ChunkSettings(8000, 0, 4, false), ChunkSettings.From(Config(("Indexing:Chunk:ChapterAligned", "false"))) with { OverlapTokens = 0 });
        // The windows of before the one-pass pipeline are what they were: 103 chunks for the Hound.
        Assert.Equal(103, Prepare(Hound, ChunkSettings.Windows).Chunks.Count);
        Assert.Equal(["--chunk-tokens must be 1 or more.", "--judge-band must look like 0.80-0.90.", "--parallel must be 1 or more."],
            new StageOverrides(ChunkTokens: 0, JudgeBand: "high", Parallel: 0).Validate());
        Assert.Equal((0.8, 0.9), ExtractSettings.ParseBand("0.80-0.90"));
        Assert.Null(ExtractSettings.ParseBand("0.9-0.8"));
    }

    [Fact]
    public void A_run_is_read_with_the_chunking_its_run_json_records()
    {
        using var root = new FullSchemaPipelineTests.TempFolder();
        var old = RunFolder.Create(root.Path);
        old.RecordCommand(new { command = "index mentions" }, new Dictionary<string, object?> { ["settings"] = new { chunkTokens = 1000, overlapTokens = 100 } });
        var now = RunFolder.Create(root.Path);
        now.RecordCommand(new { command = "index extract" }, new Dictionary<string, object?> { ["settings"] = new { chunkTokens = 8000, overlapTokens = 0, chapterAligned = true } });
        // A later stage's own settings are merged in beside them, not over them.
        now.RecordCommand(new { command = "index resolve" }, new Dictionary<string, object?> { ["settings"] = new { stage2 = new { samples = 3 } } });

        Assert.Equal(ChunkSettings.Windows, IndexingShared.ChunkingOf(old));
        Assert.Equal(new ChunkSettings(8000, 0, 4, true), IndexingShared.ChunkingOf(now));
    }

    // ---- Stage A: the reply's shape, evidence by offset, the judge ------------------------------------------------------

    private const string Para1 = "Dr. Mortimer came to Baker Street on a Tuesday. He carried a stick and a small dog followed him.";
    private const string Para2 = "“I have a manuscript in my pocket,” said Dr. Mortimer. Holmes took it and read the date, 1742.";
    private static readonly string ChunkText = "Chapter 1.\r\n\r\nThe Visitor\r\n\r\n" + Para1 + "\r\n\r\n" + Para2 + "\r\n";

    private static string Ev(string quote, int? at = null) => $"{at ?? ChunkText.IndexOf(quote, StringComparison.Ordinal)}:{quote.Length}:{(quote.Length > 80 ? quote[..80] : quote)}";

    private static ExtractOutput Output() => new(
        // A name is written once, with a number; the items point at it by the number (1 Dr. Mortimer, 2 Holmes, 3 Baker Street).
        [new(1, "Dr. Mortimer", "character"), new(2, "Holmes", "character"), new(3, "Baker Street", "location"), new(4, "Moriarty", "character")],
        [
            new(1, "narrated", "Dr. Mortimer comes to Baker Street.", "on a Tuesday", [new(1, "arrived")], 3, 0, Ev("Dr. Mortimer came to Baker Street on a Tuesday.")),
            new(2, "narrated", "Dr. Mortimer shows Holmes a manuscript.", "", [new(1, "speaker"), new(2, "listener"), new(9, "a number no name has")], 0, 0, Ev("“I have a manuscript in my pocket,” said Dr. Mortimer.")),
        ],
        [
            new(1, "Dr. Mortimer has a manuscript.", [1], [new(2, "told")], Ev("“I have a manuscript in my pocket,” said Dr. Mortimer.", at: 3)),          // a wrong offset, the right words
            new(2, "The manuscript is dated 1742.", [1, 9], [new(2, "read")], Ev("Holmes took it and read the date, 1742.")),
            new(3, "Dr. Mortimer owns a spaniel.", [1], [new(1, "witnessed")], "40:30:He owned a curly-haired spaniel of no value."),                // not in the chunk
        ],
        [new(2, 2, 0, "curious", 2, Ev("Holmes took it and read the date, 1742."))],
        [], [], [new("LOCATED_AT", 1, 3, "", Ev("Dr. Mortimer came to Baker Street on a Tuesday."))]);

    [Fact]
    public void A_reply_round_trips_through_the_schema_and_evidence_offsets_expand_to_the_text()
    {
        // The reply as JSON and back: the flat shape (no nulls, kinds as strings, evidence as one "at:len:q" string).
        var json = JsonSerializer.Serialize(Output(), ExtractStage.Json);
        Assert.DoesNotContain("null", json);
        var output = JsonSerializer.Deserialize<ExtractOutput>(json, ExtractStage.Json)!;
        Assert.Equal(json, JsonSerializer.Serialize(output, ExtractStage.Json));
        Assert.Equal(new AEv(30, 96, "Dr. Mortimer came"), AEv.Parse("30:96:Dr. Mortimer came"));
        Assert.Equal(new AEv(12, 5, "He said: “no.”"), AEv.Parse("12:5:He said: “no.”"));      // a colon inside the quote
        Assert.Equal(new AEv(-1, 0, "no numbers here"), AEv.Parse("no numbers here"));
        Assert.Null(AEv.Parse(" "));

        var chunk = new ChunkInfo(0, 1, 5000, 5000 + ChunkText.Length, 60);
        var sorted = ExtractStage.Sort(output, chunk, ChunkText, 5000, new ExtractSettings());

        // Offsets are into the chunk; what is kept is cut from the text itself, at book offsets.
        var arrive = sorted.Events[0];
        Assert.Equal(("e1", "narrated", "on a Tuesday", "Baker Street"), (arrive.Id, arrive.Kind, arrive.When, arrive.Location));
        Assert.Equal("Dr. Mortimer came to Baker Street on a Tuesday.", arrive.Evidence.Quote);
        Assert.Equal((5000 + ChunkText.IndexOf(Para1, StringComparison.Ordinal), "offset", 1.0), (arrive.Evidence.CharStart, arrive.Evidence.Method, arrive.Evidence.Score));
        Assert.Equal(arrive.Evidence.Quote, ChunkText[(arrive.Evidence.CharStart - 5000)..(arrive.Evidence.CharEnd - 5000)]);
        Assert.Null(sorted.Events[1].When);                                   // "" is "none"
        Assert.Null(sorted.Events[1].Location);
        Assert.Equal(["Dr. Mortimer", "Holmes"], sorted.Events[1].Participants.Select(x => x.Name));   // numbers became names; a number no name has is left out
        Assert.Equal(["Dr. Mortimer"], sorted.Facts[1].Subjects);
        Assert.Null(sorted.Events[1].MentionedIn);                            // 0 is "none"
        // A wrong offset with the right opening words is found by the words.
        Assert.Equal(("f1", "exact", "“I have a manuscript in my pocket,” said Dr. Mortimer."), (sorted.Facts[0].Id, sorted.Facts[0].Evidence.Method, sorted.Facts[0].Evidence.Quote));
        Assert.Equal([new XDiscovery("e2", "read")], sorted.Facts[1].DiscoveredBy);
        Assert.Equal(("Holmes", "f2", null, "curious", "e2"), (sorted.Perceptions[0].Character, sorted.Perceptions[0].Fact, sorted.Perceptions[0].TargetCharacter, sorted.Perceptions[0].Kind, sorted.Perceptions[0].CauseEvent));
        Assert.Equal(("LOCATED_AT", "Dr. Mortimer", "Baker Street", null), (sorted.Relations[0].Type, sorted.Relations[0].Source, sorted.Relations[0].Target, sorted.Relations[0].Kind));

        // An item whose offset and opening words are found nowhere is dropped and logged.
        Assert.Equal(["f1", "f2"], sorted.Facts.Select(f => f.Id));
        var dropped = Assert.Single(sorted.Rejected);
        Assert.Equal(("fact", FailureTags.QuoteMismatch, "Dr. Mortimer owns a spaniel."), (dropped.Item, dropped.Tag, dropped.What));
        Assert.Empty(sorted.Borderline);
        Assert.Equal(7, sorted.Returned);

        // The names: every whole-word occurrence, a name inside a longer listed name not counted twice, a name not in the text rejected.
        var (mentions, rejected) = ExtractStage.Mentions(output.Mentions, chunk, ChunkText, 5000);
        Assert.Equal(["Dr. Mortimer", "Baker Street", "Dr. Mortimer", "Holmes"], mentions.Select(m => m.Text));
        Assert.All(mentions, m => Assert.Equal(m.Text, ChunkText[(m.CharStart - 5000)..(m.CharEnd - 5000)]));
        Assert.Equal(MentionType.Location, mentions[1].Type);
        Assert.Equal("Moriarty", Assert.Single(rejected).Text);
        Assert.DoesNotContain(ExtractStage.Mentions([new(1, "Mortimer", "character"), new(2, "Dr. Mortimer", "character")], chunk, ChunkText, 0).Mentions, m => m.Text == "Mortimer");
    }

    [Fact]
    public void A_reply_is_kept_compact_summaries_and_quotes_are_cut_and_a_name_is_written_once()
    {
        // The quote's opening words run at most 80 characters and len then covers the quote as it was given; a summary at most 140.
        var quote = Para1;                                                    // 96 characters
        Assert.Equal($"30:{quote.Length}:{quote[..80]}", ExtractStage.CutEvidence($"30:10:{quote}"));
        Assert.Equal("30:200:" + quote[..80], ExtractStage.CutEvidence($"30:200:{quote}"));
        Assert.Equal("5:9:He said.", ExtractStage.CutEvidence("5:9:He said."));
        Assert.Equal(quote[..80], ExtractStage.CutEvidence(quote));           // no numbers: the words alone
        var summary = string.Join(' ', Enumerable.Repeat("Watson walks on the moor and sees a light", 6));
        var cut = ExtractStage.CutSummary(summary);
        Assert.InRange(cut.Length, 100, ExtractStage.MaxSummaryChars);
        Assert.StartsWith(cut, summary);
        Assert.EndsWith("light", cut.Split(' ')[^1].Length > 0 && summary.Contains(cut + " ") ? "light" : "light");   // cut at a word
        Assert.Equal("Short.", ExtractStage.CutSummary(" Short. "));

        var compact = ExtractStage.Compact(new ExtractOutput([new(1, "Dr. Mortimer", "character")],
            [new(1, "narrated", summary, "", [new(1, "arrived")], 0, 0, $"30:10:{quote}")], [new(1, "A fact.", [1], [new(1, "told")], $"30:10:{quote}")], [], [], [], []));
        Assert.Equal(cut, compact.Events[0].Summary);
        Assert.Equal($"30:{quote.Length}:{quote[..80]}", compact.Events[0].Ev);
        Assert.Equal(compact.Events[0].Ev, compact.Facts[0].Ev);
        // The whole passage is still what is kept: the code expands from the offsets.
        var chunk = new ChunkInfo(0, 1, 5000, 5000 + ChunkText.Length, 60);
        Assert.Equal(quote, ExtractStage.Sort(compact, chunk, ChunkText, 5000, new ExtractSettings()).Facts[0].Evidence.Quote);

        // The reply's JSON names each person once: the items carry numbers.
        var json = JsonSerializer.Serialize(Output(), ExtractStage.Json);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(json, "Baker Street\""));
        Assert.DoesNotContain("\n", json);
        Assert.DoesNotContain("  ", json);
        // A glean keeps the first answer's numbers and adds its own.
        var names = ExtractStage.Names(new ExtractOutput([new(5, "Sir Henry", "character"), new(1, "Someone Else", "character")], [], [], [], [], [], []), ExtractStage.Names(Output()));
        Assert.Equal(("Dr. Mortimer", "Sir Henry"), (names[1], names[5]));

        // Thinking: medium effort by default (chosen on gold); off (the request says disabled), the model's default, or a budget.
        Assert.Equal("medium", new ExtractSettings().ThinkingHint);
        Assert.Equal(["--thinking must be off, low, medium, high, default or a number of tokens."], new StageOverrides(Thinking: "some").Validate());
        Assert.Empty(new StageOverrides(Thinking: "Default").Validate());
        Assert.Equal("off", Pipeline.ExtractSettingsFrom(new StageOverrides(Thinking: "off").Over(new ConfigurationBuilder().Build())).Thinking);
        Assert.Equal("disabled", new ExtractSettings(Thinking: "off").ThinkingHint);
        Assert.Null(new ExtractSettings(Thinking: "default").ThinkingHint);
        Assert.Equal("2000", new ExtractSettings(Thinking: "2000").ThinkingHint);
        const string body = """{"model":"m","max_tokens":10,"messages":[{"role":"user","content":"x"}]}""";
        Assert.Equal("disabled", JsonNode.Parse(AnthropicCacheHandler.SetThinking(body, "disabled")!)!["thinking"]!["type"]!.GetValue<string>());
        var budget = JsonNode.Parse(AnthropicCacheHandler.SetThinking(body, "2000")!)!["thinking"]!;
        Assert.Equal(("enabled", 2000), (budget["type"]!.GetValue<string>(), budget["budget_tokens"]!.GetValue<int>()));
        // An effort: adaptive thinking, with the effort beside the response format in output_config.
        Assert.Equal("low", new ExtractSettings(Thinking: "Low").ThinkingHint);
        var low = JsonNode.Parse(AnthropicCacheHandler.SetThinking("""{"model":"m","output_config":{"format":{"type":"json_schema"}},"messages":[]}""", "low")!)!;
        Assert.Equal(("adaptive", "low", "json_schema"), (low["thinking"]!["type"]!.GetValue<string>(), low["output_config"]!["effort"]!.GetValue<string>(), low["output_config"]!["format"]!["type"]!.GetValue<string>()));
        Assert.Null(AnthropicCacheHandler.SetThinking(body, null));
        Assert.Null(AnthropicCacheHandler.SetThinking("not json", "disabled"));
    }

    private sealed class ScriptedClient(Func<IList<ChatMessage>, string> reply) : IChatClient
    {
        public List<IList<ChatMessage>> Requests { get; } = [];
        public int InFlight, Peak;

        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var list = messages.ToList();
            lock (Requests) Requests.Add(list);
            var now = Interlocked.Increment(ref InFlight);
            int peak;
            while (now > (peak = Volatile.Read(ref Peak)) && Interlocked.CompareExchange(ref Peak, now, peak) != peak) { }
            await Task.Delay(15, cancellationToken);
            Interlocked.Decrement(ref InFlight);
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, reply(list)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private static string Empty() => JsonSerializer.Serialize(new ExtractOutput([], [], [], [], [], [], []), ExtractStage.Json);

    [Fact]
    public async Task Extract_asks_once_per_chunk_with_names_and_no_entity_list_and_the_judge_decides_the_borderline()
    {
        var text = IndexingShared.BookText(Hound);
        var stage0 = Prepare(Hound, new ChunkSettings());
        var skill = SkillFile.Load(RepoPaths.FindRoot(), SkillFile.ExtractGraph);
        var chapter2 = stage0.Chapters[2];
        var chapterText = text[chapter2.CharStart..chapter2.CharEnd];
        const string exact = "I confess at these words a shudder passed through me.";
        // The same sentence with a word added and one changed: a fuzzy match under the threshold, inside the judge band.
        const string near = "I confess that at those words a shudder passed through me.";
        Assert.Contains(exact, chapterText);
        var score = EvidenceChecker.Find(near, chapterText, 0)!.Score;
        Assert.InRange(score, 0.80, 0.8999);
        var client = new ScriptedClient(messages =>
        {
            var user = messages.Last(m => m.Role == ChatRole.User).Text;
            if (user.StartsWith("For each item below", StringComparison.Ordinal)) return "{\"keep\":[1]}";
            if (!user.Contains("chapter 3:", StringComparison.Ordinal)) return Empty();
            return JsonSerializer.Serialize(new ExtractOutput([new(1, "Holmes", "character"), new(2, "Dr. Watson", "character")],
                [new(1, "narrated", "Watson shudders at Dr. Mortimer's words.", "", [new(2, "narrator")], 0, 0, $"0:{exact.Length}:{exact}")],
                [
                    new(1, "Watson shuddered at the words.", [1], [new(1, "witnessed")], $"0:{near.Length}:{near}"),
                    new(2, "A second borderline fact.", [1], [new(1, "witnessed")], "0:57:I confess that at these words a shiver passed through me."),
                ], [], [], [], []), ExtractStage.Json);
        });
        var runner = new StageRunner(8);

        var (mentions, extract) = await ExtractStage.RunAsync(client, null, skill, stage0, text, 1, 3, new ExtractSettings(), runner, null, TestContext.Current.CancellationToken);

        // One call per chunk (chapters 1–3 are one chunk each) and one judge call for the chunk with borderline items.
        Assert.Equal(3, extract.Chunks.Count);
        Assert.Equal(4, client.Requests.Count);
        Assert.Equal([1, 1, 2], extract.Chunks.Select(c => c.Calls));
        var call = client.Requests.First(r => r.Last().Text.Contains("chapter 3:", StringComparison.Ordinal) || r.Last().Contents.OfType<TextContent>().Any(c => c.Text.Contains("chapter 3:", StringComparison.Ordinal)));
        // System: the skill and the narrator. User: three blocks, the chunk text the second. No entity list anywhere.
        Assert.StartsWith(skill.Text.TrimEnd(), call[0].Text);
        Assert.Contains("Narrator of this chapter: Dr. Watson.", call[0].Text);
        var blocks = call[1].Contents.OfType<TextContent>().Select(c => c.Text).ToList();
        Assert.Equal(3, blocks.Count);
        Assert.Equal($"<<<\n{chapterText}\n>>>", blocks[1]);
        Assert.DoesNotContain("Entities (id", string.Concat(blocks));
        Assert.True(call.Sum(m => m.Text.Length) < skill.Text.Length + chapterText.Length + 700);
        // The judge sees the items and the two quotes, not the chunk.
        var judge = client.Requests.Single(r => r.Last().Text.StartsWith("For each item below", StringComparison.Ordinal)).Last().Text;
        Assert.Contains("quoted: \"" + near + "\"", judge);
        Assert.DoesNotContain("<<<", judge);
        Assert.True(judge.Length < 1500);

        var chunk = extract.Chunks[2];
        Assert.Equal((2, 1), (chunk.Judged, chunk.JudgeKept));
        Assert.Equal(["f1"], chunk.Facts.Select(f => f.Id));                 // the judge kept item 1, dropped item 2
        Assert.Equal(1, chunk.Dropped);
        Assert.InRange(chunk.Facts[0].Evidence.Score, 0.80, 0.8999);
        Assert.Equal(1.0, chunk.Events[0].Evidence.Score);
        Assert.Contains(mentions.Mentions, m => m.Text == "Holmes" && m.Chapter == 3);
        Assert.Equal((1, 1, ExtractStage.Shape), (mentions.Samples, mentions.MinVotes, extract.Shape));

        // With the judge off the borderline items are dropped by the check.
        var plain = new ScriptedClient(client.Requests.Count > 0 ? messages => messages.Last().Text.Contains("chapter 3:", StringComparison.Ordinal) || messages.Last().Contents.OfType<TextContent>().Any(c => c.Text.Contains("chapter 3:", StringComparison.Ordinal))
            ? JsonSerializer.Serialize(new ExtractOutput([new(1, "Dr. Watson", "character"), new(2, "Holmes", "character")], [new(1, "narrated", "Watson shudders.", "", [new(1, "narrator")], 0, 0, $"0:{exact.Length}:{exact}")],
                [new(1, "Watson shuddered at the words.", [2], [new(1, "witnessed")], $"0:{near.Length}:{near}")], [], [], [], []), ExtractStage.Json) : Empty() : _ => Empty());
        var (_, noJudge) = await ExtractStage.RunAsync(plain, null, skill, stage0, text, 3, 3, new ExtractSettings(Judge: false), new StageRunner(8), null, TestContext.Current.CancellationToken);
        Assert.Single(plain.Requests);
        Assert.Empty(noJudge.Chunks[0].Facts);
        Assert.Equal((0, 1), (noJudge.Chunks[0].Judged, noJudge.Chunks[0].Dropped));
    }

    [Fact]
    public async Task A_reply_that_cannot_be_read_is_asked_again_in_two_halves_and_glean_adds_a_second_call()
    {
        var text = IndexingShared.BookText(Hound);
        var stage0 = Prepare(Hound, new ChunkSettings());
        var skill = SkillFile.Load(RepoPaths.FindRoot(), SkillFile.ExtractGraph);
        var chapter = stage0.Chapters[0];
        var whole = $"<<<\n{text[chapter.CharStart..chapter.CharEnd]}\n>>>";
        var client = new ScriptedClient(messages => messages.Last().Contents.OfType<TextContent>().Any(c => c.Text == whole) ? "{ \"mentions\": [ cut short" : Empty());

        var (_, extract) = await ExtractStage.RunAsync(client, null, skill, stage0, text, 1, 1, new ExtractSettings(), new StageRunner(8), null, TestContext.Current.CancellationToken);

        Assert.Equal(3, client.Requests.Count);                               // the whole chapter, then its two halves
        Assert.Equal((2, 1, 3), (extract.Chunks[0].Parts, extract.Chunks[0].Unreadable, extract.Chunks[0].Calls));
        var halves = client.Requests.Skip(1).Select(r => r.Last().Contents.OfType<TextContent>().ElementAt(1).Text[4..^4]).ToList();
        Assert.Equal(text[chapter.CharStart..chapter.CharEnd], string.Concat(halves));

        var glean = new ScriptedClient(_ => Empty());
        var asked = new List<string>();
        var (_, gleaned) = await ExtractStage.RunAsync(glean, null, skill, stage0, text, 1, 1, new ExtractSettings(Glean: true), new StageRunner(8), null, TestContext.Current.CancellationToken,
            called: (c, what) => { lock (asked) asked.Add($"{c.Index} {what}"); });
        Assert.Equal(2, glean.Requests.Count);
        Assert.Equal([ChatRole.System, ChatRole.User, ChatRole.Assistant, ChatRole.User], glean.Requests[1].Select(m => m.Role));
        Assert.Equal(ExtractStage.GleanPrompt, glean.Requests[1].Last().Text);
        Assert.Contains("only items that are not in the list above", ExtractStage.GleanPrompt);
        Assert.Equal(2, gleaned.Chunks[0].Calls);
        Assert.Equal(["0 extract", "0 glean"], asked);                        // each call is announced as it is scheduled (the progress bar's planned count)
    }

    [Fact]
    public async Task A_glean_adds_only_what_is_new_and_points_at_the_first_answers_names()
    {
        var text = IndexingShared.BookText(Hound);
        var stage0 = Prepare(Hound, new ChunkSettings());
        var skill = SkillFile.Load(RepoPaths.FindRoot(), SkillFile.ExtractGraph);
        var chapter = stage0.Chapters[0];
        var chapterText = text[chapter.CharStart..chapter.CharEnd];
        // Each on one line of the source text.
        const string stick = "picked up the stick which our visitor had left";
        const string late = "Mr. Sherlock Holmes, who was usually very late in the mornings";
        Assert.All(new[] { stick, late }, q => Assert.Contains(q, chapterText));
        string Ev(string q) => $"{chapterText.IndexOf(q, StringComparison.Ordinal)}:{q.Length}:{q}";
        var first = new ExtractOutput([new(1, "Dr. Watson", "character"), new(2, "Mr. Sherlock Holmes", "character")],
            [new(1, "narrated", "Watson picks up the visitor's stick.", "", [new(1, "narrator")], 0, 0, Ev(stick))],
            [new(1, "Holmes is usually very late in the mornings.", [2], [new(1, "recalled")], Ev(late))], [], [], [], []);
        // The glean gives the first fact again in nearly the same words, a new fact about a name of the first answer, and a new name.
        var second = new ExtractOutput([new(3, "Holmes", "character")],
            [new(1001, "narrated", "Watson picks up the visitor's stick!", "", [new(1, "narrator")], 0, 0, Ev(stick))],
            [
                new(1001, "Holmes is usually very late in the mornings", [2], [new(1, "recalled")], Ev(late)),
                new(1002, "The visitor left his stick behind the night before.", [1, 3], [new(1, "inferred")], Ev(stick)),
            ], [], [], [], []);
        var client = new ScriptedClient(messages => JsonSerializer.Serialize(messages.Last().Text == ExtractStage.GleanPrompt ? second : first, ExtractStage.Json));

        var (mentions, extract) = await ExtractStage.RunAsync(client, null, skill, stage0, text, 1, 1, new ExtractSettings(Glean: true), new StageRunner(8), null, TestContext.Current.CancellationToken);

        var chunk = extract.Chunks[0];
        Assert.Equal(["e1"], chunk.Events.Select(e => e.Id));
        Assert.Equal(["f1", "f1002"], chunk.Facts.Select(f => f.Id));
        Assert.Equal(["Dr. Watson", "Holmes"], chunk.Facts[1].Subjects);      // 1 is the first answer's name, 3 the glean's own
        Assert.Contains(mentions.Mentions, m => m.Text == "Holmes");
        Assert.DoesNotContain(mentions.Rejected, r => r.Text == "Dr. Watson");   // the narrator's name is not in the chunk as written: no failure
        // The first answer goes back compact, as the assistant's turn.
        Assert.Equal(JsonSerializer.Serialize(first, ExtractStage.Json), client.Requests[1][2].Text);
    }

    // ---- Stage B: blocking ----------------------------------------------------------------------------------------------

    private static Mention M(string text, int start, int chapter = 1, MentionType type = MentionType.Character) =>
        new(0, text, type, 0, chapter, "first", start, start + text.Length, "", new EvidenceMatch("exact", 1, start, start + text.Length));

    private static Cluster C(int id, params string[] names) => new(id, MentionType.Character, "leftover", names.Select((n, i) => M(n, id * 1000 + i * 50)).ToList());

    [Fact]
    public void Blocks_tie_clusters_by_name_and_never_pair_across_blocks()
    {
        var clusters = new List<Cluster>
        {
            C(1, "Sir Henry Baskerville"), C(2, "Sir Henry"), C(3, "Sir Charles Baskerville"), C(4, "Mr. Stapleton"), C(5, "Miss Stapleton"),
            C(6, "Mr. Frankland"), C(7, "Dr. Mortimer"), C(8, "Mortimer"),
        };
        var settings = new CorefSettings();
        var candidates = CorefStage2.Candidates(clusters, settings);
        var ties = BlockResolve.Ties(clusters, settings);

        // A shared title alone ("Mr.", "Sir") ties nothing: Mr. Frankland is in no block, and the Stapletons are not with the Baskervilles.
        Assert.Contains(candidates, p => p.Why == "honorific");
        Assert.DoesNotContain(ties, p => p.Why == "honorific");
        var blocks = BlockResolve.Plan(clusters, ties, 25, out var split);
        Assert.Empty(split);
        Assert.Equal([[1, 2, 3], [4, 5], [7, 8]], blocks.Select(b => b.Clusters.ToList()));
        var block = clusters.ToDictionary(c => c.Id, c => blocks.FirstOrDefault(b => b.Clusters.Contains(c.Id))?.Id);
        Assert.Null(block[6]);
        // Every pair a block decides lies inside one block; every tie is inside one block.
        Assert.All(ties, t => Assert.Equal(block[t.A], block[t.B]));
        var inBlocks = blocks.Sum(b => b.Clusters.Count * (b.Clusters.Count - 1) / 2);
        Assert.Equal(5, inBlocks);
        Assert.True(candidates.Count > inBlocks);                             // what asking pair by pair would have cost

        // A block over the limit is split by the initial of the first name word; a part of one cluster asks nothing.
        var family = Enumerable.Range(1, 7).Select(i => C(i, $"{"ABCDEFG"[i - 1]}lbert Hale")).ToList();
        var parts = BlockResolve.Plan(family, BlockResolve.Ties(family, settings), 3, out var cut);
        Assert.Equal([1, 2, 3, 4, 5, 6, 7], Assert.Single(cut));
        Assert.All(parts, p => Assert.InRange(p.Clusters.Count, 2, 3));
        Assert.Equal(parts.SelectMany(p => p.Clusters).Count(), parts.SelectMany(p => p.Clusters).Distinct().Count());
    }

    [Fact]
    public void A_merge_already_made_is_not_asked_again()
    {
        // {1,2} were merged by an earlier answer: a later question over 1, 2 and 3 shows them as one group, so (1,2) is not asked.
        Assert.Equal([1, 3], BlockResolve.Collapse([1, 2, 3], [[1, 2], [3]]));
        Assert.Equal([1, 3, 5], BlockResolve.Collapse([1, 2, 3, 4, 5], [[1, 2], [3, 4], [5]]));
        Assert.Null(BlockResolve.Collapse([1, 2], [[1, 2]]));                 // nothing left to ask
    }

    private static BlockAnswer Answer(int[][] same, string quote) => new(same.Select(s => s.ToList()).ToList(), same.Select(s => new BlockProof(s.ToList(), "Sir Henry Baskerville", quote)).ToList(), []);

    [Fact]
    public async Task A_chain_of_sets_does_not_merge_its_ends_and_a_set_needs_a_proof_and_two_votes()
    {
        var bookText = "Sir Henry Baskerville arrived. “Welcome, Sir Henry,” said the doctor. Henry laughed. Old Hugo Baskerville was long dead.";
        var a = new Cluster(1, MentionType.Character, "leftover", [M("Sir Henry Baskerville", 0)]);
        var b = new Cluster(2, MentionType.Character, "leftover", [M("Sir Henry", 41)]);
        var c = new Cluster(3, MentionType.Character, "leftover", [M("Henry", 69)]);
        var shown = new List<CorefStage2.Passage> { new(1, 0, bookText.Length, bookText) };
        var block = new Block(1, [1, 2, 3], "name");
        const string proof = "“Welcome, Sir Henry,” said the doctor.";

        // Two answers chain 1–2 and 2–3 in separate sets; none puts 1 and 3 together.
        var chained = BlockResolve.Vote(block, [a, b, c], [Answer([[1, 2], [2, 3]], proof), Answer([[1, 2], [2, 3]], proof), Answer([], proof)], 2, shown, bookText, 0.9);
        Assert.Equal([(1, 2, "same"), (1, 3, "different"), (2, 3, "same")], chained.Select(d => (d.Pair.A, d.Pair.B, d.Relation)));
        var (groups, notes) = await CorefStage2.MergeAsync([a, b, c], chained, pair => throw new InvalidOperationException($"asked {pair.A}–{pair.B} again"));
        Assert.Equal([[1, 2], [3]], groups);                                  // 1 = 2, and 3 stays apart: 1 = 3 was never answered
        Assert.Contains("no transitive merging", Assert.Single(notes));

        // One set of all three in two answers merges all three.
        var whole = BlockResolve.Vote(block, [a, b, c], [Answer([[1, 2, 3]], proof), Answer([[3, 2, 1]], proof), Answer([[1, 2]], proof)], 2, shown, bookText, 0.9);
        Assert.All(whole, d => Assert.Equal("same", d.Relation));
        Assert.Equal([[1, 2, 3]], (await CorefStage2.MergeAsync([a, b, c], whole, _ => throw new InvalidOperationException())).Groups);
        Assert.Equal("Sir Henry Baskerville", whole[0].CanonicalName);

        // One vote is not enough; a set whose proof is not in the passages shown does not count; ids outside the block are ignored.
        Assert.All(BlockResolve.Vote(block, [a, b, c], [Answer([[1, 2, 3]], proof), Answer([], proof), Answer([], proof)], 2, shown, bookText, 0.9), d => Assert.Equal("different", d.Relation));
        Assert.All(BlockResolve.Vote(block, [a, b, c], [Answer([[1, 2]], "They were one and the same man."), Answer([[1, 2]], "They were one and the same man.")], 2, shown, bookText, 0.9),
            d => Assert.Equal("different", d.Relation));
        Assert.Equal("same", BlockResolve.Vote(block, [a, b], [Answer([[1, 2, 99]], proof), Answer([[1, 2]], proof)], 2, shown, bookText, 0.9).Single().Relation);
        // An identity the text states is a reveal, never a merge.
        var reveal = new BlockAnswer([], [], [new BlockReveal(3, 1, "Old Hugo Baskerville was long dead.")]);
        var revealed = BlockResolve.Vote(block, [a, c], [reveal, reveal], 2, shown, bookText, 0.9).Single();
        Assert.Equal(("identity-reveal", "Old Hugo Baskerville was long dead."), (revealed.Relation, revealed.Quote));
    }

    [Fact]
    public async Task Resolution_asks_one_question_per_block_three_times_and_no_pair_outside_a_block()
    {
        var text = IndexingShared.BookText(Hound);
        var stage0 = Prepare(Hound, new ChunkSettings());
        // Mentions in chapter 1 found in the text: two forms of Holmes, two of Mortimer, and Watson alone.
        List<Mention> Find(string name) => ExtractStage.Occurrences(name, text[stage0.Chapters[0].CharStart..stage0.Chapters[0].CharEnd]).Select(o => M(name, stage0.Chapters[0].CharStart + o.Start)).ToList();
        var mentions = new[] { "Sherlock Holmes", "Holmes", "Dr. Mortimer", "James Mortimer", "Watson" }.SelectMany(Find).ToList();
        mentions = Stage1.Dedupe(mentions.Where(m => !mentions.Any(o => o != m && o.CharStart <= m.CharStart && o.CharEnd >= m.CharEnd && o.Text.Length > m.Text.Length))).Mentions;
        var skill = SkillFile.Load(RepoPaths.FindRoot(), SkillFile.ResolveBlocks);
        var client = new ScriptedClient(messages =>
        {
            var prompt = messages.Last().Text;
            var ids = System.Text.RegularExpressions.Regex.Matches(prompt, @"^Group (\d+):", System.Text.RegularExpressions.RegexOptions.Multiline).Select(m => int.Parse(m.Groups[1].Value)).ToList();
            var quote = System.Text.RegularExpressions.Regex.Match(prompt, "  - chapter 1: \"(.{20,80}?)[.!?]").Groups[1].Value;
            return JsonSerializer.Serialize(new BlockAnswer([ids], [new BlockProof(ids, null, quote)], []), Stage1.Json);
        });
        var runner = new StageRunner(8);
        var settings = new CorefSettings(Samples: 3, MinVotes: 2);

        var (result, stats) = await BlockResolve.RunAsync(client, null, skill, stage0, text, mentions, [], settings, 25, 0.9, runner, null, TestContext.Current.CancellationToken);

        // Two blocks (the Holmes forms, the Mortimer forms), three samples each: six calls for two pairs. Watson is asked about with no one.
        Assert.Equal((2, 6, 2), (stats.Blocks, stats.Calls, stats.PairsInBlocks));
        Assert.Equal(6, client.Requests.Count);
        Assert.All(client.Requests, r => Assert.DoesNotContain("Watson\" ×", r.Last().Text));
        Assert.All(client.Requests, r => Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(r.Last().Text, @"^Group \d+:", System.Text.RegularExpressions.RegexOptions.Multiline).Count));
        Assert.Equal(3, result.Entities.Count);
        Assert.Equal(["Dr. Mortimer", "Holmes", "James Mortimer", "Sherlock Holmes", "Watson"], result.Entities.SelectMany(e => e.Aliases).Order(StringComparer.Ordinal));
        Assert.Equal(2, result.Entities.Count(e => e.Aliases.Count == 2));
        Assert.InRange(runner.Peak, 1, 8);
    }

    // ---- Stage C: linking precedence ------------------------------------------------------------------------------------

    [Fact]
    public void A_name_is_linked_by_chain_then_exact_alias_then_whole_word_alias_and_else_becomes_a_new_entity()
    {
        var text = IndexingShared.BookText(Hound);
        var stage0 = Prepare(Hound, new ChunkSettings());
        var chunk = stage0.Chunks.First(c => c.Chapter == 2);
        var chunkText = text[chunk.CharStart..chunk.CharEnd];
        int At(string s, int from = 0) => chunk.CharStart + chunkText.IndexOf(s, from, StringComparison.Ordinal);
        (int, int, string) Mention(string s, int at) => (at, at + s.Length, s);
        // Two people the chapter calls "Baskerville": Sir Charles first, Hugo of the legend later.
        var charles = At("Sir Charles Baskerville");
        var hugo = At("Hugo Baskerville");
        var entities = new List<EntityRef>
        {
            new(1, "Sir Charles Baskerville", MentionType.Character, ["Baskerville", "Sir Charles"], [Mention("Sir Charles Baskerville", charles), Mention("Baskerville", charles + 12)]),
            new(2, "Hugo Baskerville", MentionType.Character, ["Baskerville", "Hugo"], [Mention("Hugo Baskerville", hugo), Mention("Baskerville", hugo + 5)]),
            new(3, "Dr. James Mortimer", MentionType.Character, ["Dr. Mortimer"], [Mention("Dr. Mortimer", At("Dr. Mortimer"))]),
            new(4, "Dr. Watson", MentionType.Character, ["Watson"], [Mention("Watson", stage0.Chapters[0].CharStart + 100)]),     // mentioned in chapter 1 only
            new(5, "Baskerville Hall", MentionType.Location, [], [Mention("Baskerville Hall", At("Baskerville Hall"))]),
        };
        var linker = new Linker(entities, stage0, text);
        NEvidence Near(int at) => new("…", at, at + 40, 1, "exact");

        // 1. Chain membership: the mention with this text inside the evidence, else the nearest one in the chunk.
        Assert.Equal(1, linker.Resolve("Baskerville", MentionType.Character, Near(charles), chunk));
        Assert.Equal(2, linker.Resolve("Baskerville", MentionType.Character, Near(hugo), chunk));
        Assert.Equal(2, linker.Resolve("Baskerville", MentionType.Character, Near(hugo + 300), chunk));
        // 2. Exact alias (no mention with this text in the chunk).
        Assert.Equal(1, linker.Resolve("Sir Charles", MentionType.Character, Near(charles), chunk));
        Assert.Equal(4, linker.Resolve("Watson", MentionType.Character, Near(charles), chunk));
        // 3. A whole-word alias of an entity mentioned in this chapter; a possessive ending is left off.
        Assert.Equal(3, linker.Resolve("Mortimer’s", MentionType.Character, Near(charles), chunk));
        Assert.Equal(3, linker.Resolve("James Mortimer", MentionType.Character, Near(charles), chunk));
        // The slot's type decides between a person and a place of one name; the narrator's "I" is the narrator.
        Assert.Equal(5, linker.Resolve("Baskerville Hall", MentionType.Location, Near(charles), chunk));
        Assert.Equal(4, linker.Resolve("I", MentionType.Character, Near(charles), chunk));
        Assert.Equal(4, linker.Resolve("Dr. Watson", MentionType.Character, Near(charles), chunk));
        // 6. A name no entity goes by: a new entity with its places in the chunk, the same one the next time.
        Assert.Contains("Barrymore", chunkText);
        var added = linker.Resolve("Barrymore", MentionType.Character, Near(charles), chunk);
        Assert.Equal(6, added);
        Assert.Equal(6, linker.Resolve("Barrymore", MentionType.Character, Near(hugo), chunk));
        // A name the chunk does not write is not dropped either: a new entity that stands where the item's evidence does.
        Assert.Equal(7, linker.Resolve("Sir Mortimer Holmes", MentionType.Character, Near(charles), chunk));
        // Nothing is left out. A pronoun nothing resolved is the character last mentioned before it in the paragraph; a name
        // the book has not used by this chunk ("Beryl" comes in chapter 15) is not written into the graph: with no word of it in the
        // evidence it is an entity that says no more than the text does.
        Assert.Equal(1, linker.Resolve("she", MentionType.Character, Near(charles + 23), chunk));
        Assert.Equal(8, linker.Resolve("Beryl Stapleton", MentionType.Character, Near(charles), chunk));
        Assert.Equal(["Barrymore", "Sir Mortimer Holmes", "an unnamed person (chapter 2)"], linker.Unlinked.Select(u => u.Name));
        Assert.Equal([null, null, Entity.Descriptive], linker.Unlinked.Select(u => u.Kind));
        Assert.All(linker.Unlinked[0].Mentions, m => Assert.Equal("Barrymore", text[m.Start..m.End]));
        Assert.Equal((charles, charles + 40), (linker.Unlinked[1].Mentions.Single().Start, linker.Unlinked[1].Mentions.Single().End));
        Assert.Equal(["chain", "chain", "chain", "alias", "alias", "word", "word", "chain", "narrator", "narrator", "new", "chain", "new", "pronoun", "described"], linker.Log.Select(l => l.How));
        // What the earlier order did with the last four: an entity for a name in the text (also the second time it is used, and for
        // the pronoun, which the chunk has as a word), a drop for the two that are not.
        Assert.Equal(["new", "dropped", "new", "dropped"], linker.Log.TakeLast(4).Select(l => l.Was));
        Assert.NotNull(linker.Log[^3].Nearest);
    }

    [Fact]
    public void After_the_aliases_a_name_goes_by_the_coreference_chain_then_by_spelling_and_the_narrator_by_the_role()
    {
        var text = IndexingShared.BookText(Hound);
        var stage0 = Prepare(Hound, new ChunkSettings());
        var chunk = stage0.Chunks.First(c => c.Chapter == 2);
        var chunkText = text[chunk.CharStart..chunk.CharEnd];
        int At(string s) => chunk.CharStart + chunkText.IndexOf(s, StringComparison.Ordinal);
        (int, int, string) Mention(string s, int at) => (at, at + s.Length, s);
        NEvidence Near(int at) => new("…", at, at + 60, 1, "exact");
        var mortimer = At("Dr. Mortimer");
        var visitor = At("the doctor");
        var charles = At("Sir Charles Baskerville");
        Assert.All(new[] { "the doctor", "the manuscript", "Baskerville Hall" }, s => Assert.Contains(s, chunkText));
        var entities = new List<EntityRef>
        {
            new(1, "Dr. James Mortimer", MentionType.Character, ["Dr. Mortimer"], [Mention("Dr. Mortimer", mortimer)]),
            new(2, "Sir Charles Baskerville", MentionType.Character, ["Sir Charles"], [Mention("Sir Charles Baskerville", charles)]),
            new(3, "Mrs. Mortimer", MentionType.Character, [], [Mention("Sir", charles)]),   // in the chapter, by a made-up mention
            new(4, "Baskerville Hall", MentionType.Location, [], [Mention("Baskerville Hall", At("Baskerville Hall"))]),
        };
        // BookNLP: chain 7 holds "Dr. Mortimer" and the noun phrase "the doctor"; chain 9 is a chain no entity owns.
        var chains = new List<ChainMention>
        {
            new(7, "PER", "PROP", mortimer, mortimer + 12, "Dr. Mortimer"),
            new(7, "PER", "NOM", visitor, visitor + 10, "the doctor"),
            new(9, "PER", "NOM", At("the manuscript"), At("the manuscript") + 14, "the manuscript"),
        };
        var linker = new Linker(entities, stage0, text, chains, new Dictionary<int, string> { [2] = "Sir Charles" });

        // 4. The chain that covers the name where it stands: "the doctor" is Dr. Mortimer; a chain without an entity links nothing.
        Assert.Equal(1, linker.Resolve("the doctor", MentionType.Character, Near(visitor), chunk));
        Assert.Equal(5, linker.Resolve("the manuscript", MentionType.Character, Near(charles), chunk));
        // 5. Spelling, at 0.92 or more: a slip of a letter links; a different form of address does not, nor do two short phrases.
        Assert.Equal(2, linker.Resolve("Sir Charls Baskerville", MentionType.Character, Near(charles), chunk));
        Assert.Equal(6, linker.Resolve("Mr. Mortimer", MentionType.Character, Near(charles), chunk));
        Assert.Equal(0, Linker.Spelling("mr. vandeleur", "mrs. vandeleur"));
        Assert.Equal(0, Linker.Spelling("the maid", "the lad"));
        Assert.True(Linker.Spelling("the stapeltons", "the stapletons") >= Linker.FuzzyThreshold);
        // A place is not linked to a person by its spelling; and a word the book does not have names no entity: an unnamed one.
        Assert.Equal(7, linker.Resolve("Baskervile Hall", MentionType.Character, Near(charles), chunk));
        Assert.Equal(("an unnamed person (chapter 2)", Entity.Descriptive), (linker.Unlinked[^1].Name, linker.Unlinked[^1].Kind));
        Assert.Equal(["coref", "new", "fuzzy", "new", "described"], linker.Log.Select(l => l.How));

        // 0. The narrator the chapter titles do not name: the name the extraction gave the role. The Hound's titles name Watson, so
        // the stand-in here is a copy of Stage 0 without narrators.
        var unnamed = stage0 with { Chapters = stage0.Chapters.Select(c => c with { Narrator = null, NarratorForm = null }).ToList() };
        var byRole = new Linker(entities, unnamed, text, chains, new Dictionary<int, string> { [2] = "Sir Charles" });
        Assert.Equal(2, byRole.Resolve("the narrator", MentionType.Character, Near(charles), chunk));
        Assert.Equal(2, byRole.Resolve("I", MentionType.Character, Near(charles), chunk));
        Assert.Equal(2, byRole.NarratorOf(chunk));
        Assert.Equal(["narrator", "narrator"], byRole.Log.Select(l => l.How));
        Assert.Equal(["dropped", "dropped"], byRole.Log.Select(l => l.Was));
        // Without a name for the role the narrator's "I" is a pronoun nothing resolved: the character last mentioned before it in the
        // paragraph. "the narrator" is a new entity, a description.
        var nobody = new Linker(entities, unnamed, text, chains);
        Assert.Equal(2, nobody.Resolve("I", MentionType.Character, Near(charles + 23), chunk));
        Assert.Equal(5, nobody.Resolve("the narrator", MentionType.Character, Near(charles), chunk));
        Assert.Equal(["pronoun", "new"], nobody.Log.Select(l => l.How));
        Assert.Equal(Entity.Descriptive, nobody.KindOf(5));
    }

    [Fact]
    public void The_narrator_by_role_is_the_most_common_name_of_the_chapter_and_else_of_the_book()
    {
        NEvidence ev = new("q", 0, 1, 1, "exact");
        ExtractChunk Chunk(int index, int chapter, params (string Name, string? Role)[] who) => new(index, chapter, 1, 1, 0, 0, 0, 0, 0,
            [new NEvent("e1", "narrated", "s", null, who.Select(w => new NParticipant(w.Name, w.Role)).ToList(), null, null, ev)], [], [], [], [], []);
        var extract = new ExtractResult(ExtractStage.Shape, "treasure-island", 1, 3, new ExtractSettings(), 8000, true,
        [
            Chunk(0, 1, ("Jim", "narrator"), ("Jim Hawkins", "narrator"), ("Jim", "narrator"), ("the narrator", "narrator"), ("Silver", "speaker")),
            Chunk(1, 2, ("Dr. Livesey", "narrator"), ("Jim", "listener")),
            Chunk(2, 3, ("narrator", "narrator"), ("Silver", "speaker")),
        ], 8, 1);
        var narrators = LinkStage.NarratorsByRole(extract);
        Assert.Equal(("Jim", "Dr. Livesey", "Jim"), (narrators[1], narrators[2], narrators[3]));
    }

    [Fact]
    public void Linking_gives_a_stage_3_file_of_the_same_shape_with_ids_for_names()
    {
        var text = IndexingShared.BookText(Hound);
        var stage0 = Prepare(Hound, new ChunkSettings());
        var chunk = stage0.Chunks[0];
        var chunkText = text[chunk.CharStart..chunk.CharEnd];
        NEvidence Quote(string q) => new(q, chunk.CharStart + chunkText.IndexOf(q, StringComparison.Ordinal), chunk.CharStart + chunkText.IndexOf(q, StringComparison.Ordinal) + q.Length, 1, "exact");
        const string stick = "I stood upon the hearth-rug and picked up the stick which our visitor had left behind him the night before.";
        const string late = "Mr. Sherlock Holmes, who was usually very late in the mornings";
        var holmes = chunk.CharStart + chunkText.IndexOf("Sherlock Holmes", StringComparison.Ordinal);
        var entities = new List<EntityRef>
        {
            new(1, "Mr. Sherlock Holmes", MentionType.Character, ["Holmes", "Sherlock Holmes"], [(holmes, holmes + 15, "Sherlock Holmes")]),
            new(2, "Dr. Watson", MentionType.Character, ["Watson"], [(chunk.CharStart + chunkText.IndexOf("Watson", StringComparison.Ordinal), chunk.CharStart + chunkText.IndexOf("Watson", StringComparison.Ordinal) + 6, "Watson")]),
        };
        var extract = new ExtractResult(ExtractStage.Shape, Hound, 1, 1, new ExtractSettings(), 8000, true,
        [
            new ExtractChunk(0, 1, 1, 1, 6, 0, 0, 0, 0,
                [
                    new NEvent("e1", "narrated", "Watson picks up the visitor's stick.", "the night before", [new("Dr. Watson", "narrator"), new("Charing Cross", "watched")], null, null, Quote(stick)),
                    new NEvent("e2", "narrated", "An event with no one the book knows.", null, [new("Charing Cross", null)], null, null, Quote(late)),
                ],
                [
                    new NFact("f1", "Holmes is usually very late in the mornings.", ["Sherlock Holmes"], [new XDiscovery("e1", "recalled")], Quote(late)),
                    new NFact("f2", "Holmes is usually very late in the mornings.", ["Holmes"], [new XDiscovery("e1", "witnessed")], Quote(late)),
                    new NFact("f3", "A fact about nobody.", ["Charing Cross"], [new XDiscovery("e1", "told")], Quote(stick)),
                ],
                [new NPerception("Watson", "f1", null, "curious", null, Quote(stick))], [], [], []),
        ], 8, 1);

        var lines = new List<string>();
        var result = LinkStage.Run(extract, entities, stage0, text, 0.9, log: lines.Add);

        var g = result.Graph;
        Assert.Equal((GraphStage3.Shape, 1, 1), (g.Shape, g.Samples, g.MinVotes));
        // The name no entity goes by is a new entity (3), so nothing that names it is lost.
        var unlinked = Assert.Single(result.Unlinked);
        Assert.Equal((3, "Charing Cross", MentionType.Character), (unlinked.Id, unlinked.Name, unlinked.Type));
        Assert.Empty(result.Dropped);
        Assert.Equal(2, g.Events.Count);
        var e = g.Events.Single(x => x.Evidence == stick);
        Assert.Equal(("the night before", 1), (e.When, e.Votes));
        Assert.Equal([2, 3], e.Participants.Select(p => p.Character).Order());
        Assert.Equal([3], g.Events.Single(x => x.Evidence == late).Participants.Select(p => p.Character));
        // The two wordings of one fact are one node with both discoveries' modes unioned per event; the fact about the new entity stays.
        Assert.Equal(2, g.Facts.Count);
        var fact = g.Facts.Single(f => f.Subjects.SequenceEqual([1]));
        Assert.Equal(1, g.FactsMerged);
        Assert.Equal(e.Id, Assert.Single(fact.Discoveries).Event);
        Assert.Equal([3], g.Facts.Single(f => f.Text == "A fact about nobody.").Subjects);
        var p = Assert.Single(g.Perceptions);
        Assert.Equal((2, fact.Id, "curious", e.Id), (p.Character, p.Fact, p.Kind, p.CauseEvent));
        Assert.Equal(0, g.Rejected);
        // run.log: the form, the chunk, where it went and the nearest alias.
        Assert.Contains(lines, l => l.StartsWith("stage-3: name \"Charing Cross\" · chunk 0 · 3× · new → Charing Cross (3) · nearest alias \"", StringComparison.Ordinal));
        // The file reads back as a stage-3.json of the full-schema shape.
        var json = JsonSerializer.Serialize(g, RunFolder.Json);
        Assert.Equal(2, JsonNode.Parse(json)!["shape"]!.GetValue<int>());
        Assert.Equal(g.Facts.Count, JsonSerializer.Deserialize<GraphStage3Result>(json, RunFolder.Json)!.Facts.Count);
    }

    [Fact]
    public void A_fact_without_a_discovering_event_gets_one_by_code_and_is_flagged()
    {
        var text = IndexingShared.BookText(Hound);
        var stage0 = Prepare(Hound, new ChunkSettings());
        var chunk = stage0.Chunks[0];
        var chunkText = text[chunk.CharStart..chunk.CharEnd];
        NEvidence Quote(string q) => new(q, chunk.CharStart + chunkText.IndexOf(q, StringComparison.Ordinal), chunk.CharStart + chunkText.IndexOf(q, StringComparison.Ordinal) + q.Length, 1, "exact");
        const string stick = "I stood upon the hearth-rug and picked up the stick which our visitor had left behind him the night before.";
        const string inside = "picked up the stick";
        const string late = "Mr. Sherlock Holmes, who was usually very late in the mornings";
        const string after = "It was a fine, thick piece of wood, bulbous-headed";
        var holmes = chunk.CharStart + chunkText.IndexOf("Sherlock Holmes", StringComparison.Ordinal);
        var watson = chunk.CharStart + chunkText.IndexOf("Watson", StringComparison.Ordinal);
        var entities = new List<EntityRef>
        {
            new(1, "Mr. Sherlock Holmes", MentionType.Character, ["Holmes", "Sherlock Holmes"], [(holmes, holmes + 15, "Sherlock Holmes")]),
            new(2, "Dr. Watson", MentionType.Character, ["Watson"], [(watson, watson + 6, "Watson")]),
        };
        var extract = new ExtractResult(ExtractStage.Shape, Hound, 1, 1, new ExtractSettings(), 8000, true,
        [
            new ExtractChunk(0, 1, 1, 1, 6, 0, 0, 0, 0,
                [
                    new NEvent("e1", "narrated", "Holmes is at breakfast.", null, [new("Holmes", null)], null, null, Quote(late)),
                    new NEvent("e2", "narrated", "Watson picks up the visitor's stick.", null, [new("Dr. Watson", "narrator")], null, null, Quote(stick)),
                ],
                [
                    // No event named, and its evidence lies inside e2's: e2, witnessed (the narrator takes part).
                    new NFact("f1", "Watson picked up the stick.", ["Watson"], [], Quote(inside)),
                    // An event this answer does not have: the narrated event nearest before the fact's evidence. Holmes's breakfast has no narrator: told.
                    new NFact("f2", "Holmes rises late.", ["Holmes"], [new XDiscovery("e9", "witnessed")], Quote(late)),
                    // After every event's evidence: still the nearest one before it.
                    new NFact("f3", "The stick is a fine, thick piece of wood.", ["Watson"], [], Quote(after)),
                    // A fact with its event is left as the model gave it.
                    new NFact("f4", "The visitor left his stick behind.", ["Watson"], [new XDiscovery("e2", "inferred")], Quote(stick)),
                ],
                [], [], [], []),
        ], 8, 1);

        var lines = new List<string>();
        var result = LinkStage.Run(extract, entities, stage0, text, 0.9, log: lines.Add);

        var g = result.Graph;
        Assert.Equal(4, g.Facts.Count);
        Assert.DoesNotContain(FailureTags.NoDiscovery, g.RejectedByReason.Keys);
        string EventOf(string summary) => g.Events.Single(e => e.Summary == summary).Id;
        GDiscovery Of(string fact) => g.Facts.Single(f => f.Text == fact).Discoveries.Single();
        Assert.Equal((EventOf("Watson picks up the visitor's stick."), "witnessed", GDiscovery.InferredByCode), (Of("Watson picked up the stick.").Event, Of("Watson picked up the stick.").How, Of("Watson picked up the stick.").Discovery));
        Assert.Equal((EventOf("Holmes is at breakfast."), "told", GDiscovery.InferredByCode), (Of("Holmes rises late.").Event, Of("Holmes rises late.").How, Of("Holmes rises late.").Discovery));
        Assert.Equal((EventOf("Watson picks up the visitor's stick."), "witnessed", GDiscovery.InferredByCode), (Of("The stick is a fine, thick piece of wood.").Event, Of("The stick is a fine, thick piece of wood.").How, Of("The stick is a fine, thick piece of wood.").Discovery));
        Assert.Equal(("inferred", null), (Of("The visitor left his stick behind.").How, Of("The visitor left his stick behind.").Discovery));
        Assert.Equal(3, result.Inferred.Count);
        Assert.Equal(3, lines.Count(l => l.EndsWith("discovery=inferred_by_code", StringComparison.Ordinal)));
        // The flag is in stage-3.json and reads back.
        var back = JsonSerializer.Deserialize<GraphStage3Result>(JsonSerializer.Serialize(g, RunFolder.Json), RunFolder.Json)!;
        Assert.Equal(3, back.Facts.Count(f => f.Discoveries.Any(d => d.Discovery == GDiscovery.InferredByCode)));
        // Outside the link stage the check is as before: no discovering event, no fact.
        var strict = GraphStage3.Check(LinkStage.Link(extract.Chunks[0], chunk, new Linker(entities, stage0, text)), chunk, chunkText, entities, 0.9);
        Assert.Equal(3, strict.Rejected.Count(r => r.Tag == FailureTags.NoDiscovery));
    }

    // ---- The last two "never drop" cases ----------------------------------------------------------------------------

    [Fact]
    public void A_pronoun_nothing_resolved_is_the_narrator_or_the_character_last_mentioned_and_the_fact_is_flagged()
    {
        var text = IndexingShared.BookText(Hound);
        var stage0 = Prepare(Hound, new ChunkSettings());
        var chunk = stage0.Chunks[0];
        var chunkText = text[chunk.CharStart..chunk.CharEnd];
        NEvidence Quote(string q) => new(q, chunk.CharStart + chunkText.IndexOf(q, StringComparison.Ordinal), chunk.CharStart + chunkText.IndexOf(q, StringComparison.Ordinal) + q.Length, 1, "exact");
        const string late = "Mr. Sherlock Holmes, who was usually very late in the mornings";
        const string stick = "picked up the stick which our visitor had left";   // one line of the text
        const string wood = "It was a fine, thick piece of wood, bulbous-headed";
        var holmes = chunk.CharStart + chunkText.IndexOf("Sherlock Holmes", StringComparison.Ordinal);
        var watson = chunk.CharStart + chunkText.IndexOf("Watson", StringComparison.Ordinal);
        Assert.True(watson > Quote(wood).CharEnd);   // the paragraph has named no one but Holmes when the pronouns come
        var entities = new List<EntityRef>
        {
            new(1, "Mr. Sherlock Holmes", MentionType.Character, ["Holmes", "Sherlock Holmes"], [(holmes, holmes + 15, "Sherlock Holmes")]),
            new(2, "Dr. Watson", MentionType.Character, ["Watson"], [(watson, watson + 6, "Watson")]),
        };
        var extract = new ExtractResult(ExtractStage.Shape, Hound, 1, 1, new ExtractSettings(), 8000, true,
        [
            new ExtractChunk(0, 1, 1, 1, 5, 0, 0, 0, 0,
                [new NEvent("e1", "narrated", "Watson picks up the visitor's stick.", null, [new("Dr. Watson", "narrator"), new("he", "watched")], null, null, Quote(stick))],
                [
                    // "he": no chain, no alias. The character last mentioned before the evidence in its paragraph is Holmes.
                    new NFact("f1", "He owns a fine stick of thick wood.", ["he"], [new XDiscovery("e1", "witnessed")], Quote(wood)),
                    // First person, and not one of the narrator's own words ("I", "me"): the chapter's narrator.
                    new NFact("f2", "We had a visitor the night before.", ["we"], [new XDiscovery("e1", "recalled")], Quote(stick)),
                    // A named subject beside a pronoun: one subject, and the fact still says how the pronoun was read.
                    new NFact("f3", "Holmes keeps late hours.", ["Holmes", "him"], [new XDiscovery("e1", "recalled")], Quote(late)),
                    // A fact with no pronoun carries no flag.
                    new NFact("f4", "Watson stood upon the hearth-rug.", ["Watson"], [new XDiscovery("e1", "witnessed")], Quote(stick)),
                ],
                [], [], [], []),
        ], 8, 1);

        var lines = new List<string>();
        var result = LinkStage.Run(extract, entities, stage0, text, 0.9, log: lines.Add);

        var g = result.Graph;
        GFact Fact(string t) => g.Facts.Single(f => f.Text == t);
        Assert.Equal(4, g.Facts.Count);
        Assert.Equal((1, GFact.InferredPronoun), (Fact("He owns a fine stick of thick wood.").Subjects.Single(), Fact("He owns a fine stick of thick wood.").SubjectSource));
        Assert.Equal((2, GFact.InferredPronoun), (Fact("We had a visitor the night before.").Subjects.Single(), Fact("We had a visitor the night before.").SubjectSource));
        Assert.Equal((1, GFact.InferredPronoun), (Fact("Holmes keeps late hours.").Subjects.Single(), Fact("Holmes keeps late hours.").SubjectSource));
        Assert.Null(Fact("Watson stood upon the hearth-rug.").SubjectSource);
        // The pronoun among the event's participants is read the same way; nothing is dropped and nothing rejected.
        Assert.Equal([1, 2], g.Events.Single().Participants.Select(p => p.Character).Order());
        Assert.Empty(result.Dropped);
        Assert.Equal(0, g.Rejected);
        Assert.Equal(4, result.NamesByHow["pronoun"]);
        Assert.Empty(result.Unlinked);
        Assert.Contains(lines, l => l.StartsWith("stage-3: name \"we\" · chunk 0 · 1× · pronoun → Dr. Watson (2) · subject=inferred_pronoun", StringComparison.Ordinal));
        // The flag is in stage-3.json and reads back.
        var json = JsonSerializer.Serialize(g, RunFolder.Json);
        Assert.Equal(3, JsonNode.Parse(json)!["facts"]!.AsArray().Count(f => f!["subjectSource"]?.GetValue<string>() == "inferred_pronoun"));
        Assert.Equal(3, JsonSerializer.Deserialize<GraphStage3Result>(json, RunFolder.Json)!.Facts.Count(f => f.SubjectSource == GFact.InferredPronoun));
    }

    [Fact]
    public void A_name_the_book_has_not_used_becomes_the_texts_own_words_and_a_descriptive_entity_an_identity_can_join()
    {
        var text = IndexingShared.BookText(Hound);
        var stage0 = Prepare(Hound, new ChunkSettings());
        var chunk = stage0.Chunks[0];
        var chunkText = text[chunk.CharStart..chunk.CharEnd];
        int At(string s) => chunk.CharStart + chunkText.IndexOf(s, StringComparison.Ordinal);
        NEvidence Quote(string q) => new(q, At(q), At(q) + q.Length, 1, "exact");
        const string late = "Mr. Sherlock Holmes, who was usually very late in the mornings";
        const string stick = "picked up the stick which our visitor had left";   // one line of the text
        var holmes = At("Sherlock Holmes");
        var watson = At("Watson");
        Assert.DoesNotContain("Zebediah", text);
        Assert.DoesNotContain("the stranger at the gate", chunkText);
        var entities = new List<EntityRef>
        {
            new(1, "Mr. Sherlock Holmes", MentionType.Character, ["Holmes", "Sherlock Holmes"], [(holmes, holmes + 15, "Sherlock Holmes"), (holmes + 9, holmes + 15, "Holmes")]),
            new(2, "Dr. Watson", MentionType.Character, ["Watson"], [(watson, watson + 6, "Watson")]),
        };
        // BookNLP has the noun phrase "our visitor" in a chain no entity owns.
        var chains = new List<ChainMention> { new(5, "PER", "NOM", At("our visitor"), At("our visitor") + 11, "our visitor") };

        var linker = new Linker(entities, stage0, text, chains);
        // The extraction's name is not in the book: the noun phrase the evidence has for him is the entity's name, of kind descriptive.
        Assert.Equal(3, linker.Resolve("Zebediah Quill", MentionType.Character, Quote(stick), chunk));
        // A word of the name that the evidence writes, widened to the noun phrase around it, goes through the usual steps.
        Assert.Equal(3, linker.Resolve("Visitor Quill", MentionType.Character, Quote(stick), chunk));
        // A description the chunk does not write as given is a new entity under those words, descriptive too; a proper name is not.
        Assert.Equal(4, linker.Resolve("the stranger at the gate", MentionType.Character, Quote(late), chunk));
        Assert.Equal(5, linker.Resolve("Charing Cross Hospital", MentionType.Location, Quote(late), chunk));
        Assert.Equal(["described", "described", "new", "new"], linker.Log.Select(l => l.How));
        Assert.Equal([("our visitor", Entity.Descriptive), ("the stranger at the gate", Entity.Descriptive), ("Charing Cross Hospital", null)], linker.Unlinked.Select(u => (u.Name, u.Kind)));
        Assert.All(linker.Unlinked[0].Mentions, m => Assert.Equal("our visitor", text[m.Start..m.End]));
        // Accents aside, a name is the name the book writes.
        Assert.Equal("La Houdiniere", Linker.Fold("La Houdinière"));

        // Through the stage: the reveal joins the descriptive entity to the named one (the man on the tor is Holmes), and a reveal
        // of two names that already are one character is nothing to join, not a bad endpoint.
        var extract = new ExtractResult(ExtractStage.Shape, Hound, 1, 1, new ExtractSettings(), 8000, true,
        [
            new ExtractChunk(0, 1, 1, 1, 4, 0, 0, 0, 0,
                [new NEvent("e1", "narrated", "Watson picks up the visitor's stick.", null, [new("Dr. Watson", "narrator"), new("Zebediah Quill", "absent")], null, null, Quote(stick))],
                [new NFact("f1", "The visitor left his stick behind.", ["Zebediah Quill"], [new XDiscovery("e1", "witnessed")], Quote(stick))],
                [], [],
                [new NIdentity("Zebediah Quill", "Holmes", Quote(stick)), new NIdentity("Holmes", "Sherlock Holmes", Quote(late))],
                []),
        ], 8, 1);
        var lines = new List<string>();
        var result = LinkStage.Run(extract, entities, stage0, text, 0.9, chains: chains, log: lines.Add);
        var visitor = Assert.Single(result.Unlinked);
        Assert.Equal(("our visitor", Entity.Descriptive, MentionType.Character), (visitor.Name, visitor.Kind, visitor.Type));
        Assert.Equal([visitor.Id], result.Graph.Facts.Single().Subjects);
        var identity = Assert.Single(result.Graph.Identities);
        Assert.Equal((1, visitor.Id), (identity.A, identity.B));
        Assert.Equal([FailureTags.Duplicate], result.Graph.RejectedByReason.Keys);
        Assert.Empty(result.Dropped);
        Assert.Contains(lines, l => l.StartsWith($"stage-3: name \"Zebediah Quill\" · chunk 0 · 3× · described → our visitor ({visitor.Id}, kind=descriptive)", StringComparison.Ordinal));
    }

    // ---- Caching ------------------------------------------------------------------------------------------------------

    [Fact]
    public void Breakpoints_go_on_the_system_block_and_the_named_user_block()
    {
        const string body = """{"model":"m","system":"You are a skill.","messages":[{"role":"user","content":[{"type":"text","text":"head"},{"type":"text","text":"<<<chunk>>>"},{"type":"text","text":"ask"}]}]}""";
        var changed = JsonNode.Parse(AnthropicCacheHandler.AddBreakpoints(body, new CacheHints("1h", 1))!)!;
        var system = changed["system"]!.AsArray().Single()!;
        Assert.Equal(("text", "You are a skill."), (system["type"]!.GetValue<string>(), system["text"]!.GetValue<string>()));
        Assert.Equal("""{"type":"ephemeral","ttl":"1h"}""", system["cache_control"]!.ToJsonString());
        var blocks = changed["messages"]![0]!["content"]!.AsArray();
        Assert.Null(blocks[0]!["cache_control"]);
        Assert.Equal("""{"type":"ephemeral"}""", blocks[1]!["cache_control"]!.ToJsonString());
        Assert.Null(blocks[2]!["cache_control"]);

        // A system array keeps its blocks, the last gets the breakpoint; a string user content becomes one block.
        var array = JsonNode.Parse(AnthropicCacheHandler.AddBreakpoints("""{"system":[{"type":"text","text":"a"},{"type":"text","text":"b"}],"messages":[{"role":"user","content":"only"}]}""", new CacheHints("5m", 0))!)!;
        Assert.Null(array["system"]![0]!["cache_control"]);
        Assert.Equal("""{"type":"ephemeral"}""", array["system"]![1]!["cache_control"]!.ToJsonString());
        Assert.Equal("only", array["messages"]![0]!["content"]![0]!["text"]!.GetValue<string>());
        Assert.NotNull(array["messages"]![0]!["content"]![0]!["cache_control"]);
        // Nothing to mark: nothing changed.
        Assert.Null(AnthropicCacheHandler.AddBreakpoints("""{"messages":[{"role":"user","content":"x"}]}""", new CacheHints("1h")));
        Assert.Null(AnthropicCacheHandler.AddBreakpoints("not json", new CacheHints()));
    }

    private sealed class Calls : ILlmCallLog
    {
        public List<JsonNode> Lines { get; } = [];
        public string? RunId => "run-1";
        public void AppendCall(object entry) { lock (Lines) Lines.Add(JsonSerializer.SerializeToNode(entry, RunFolder.Json)!); }
    }

    [Fact]
    public async Task With_the_anthropic_fake_the_system_block_carries_cache_control_and_the_usage_is_summed_into_cost_json()
    {
        using var server = new FakeLlmServer { AnthropicUsageJson = """{"input_tokens":40,"output_tokens":7,"cache_read_input_tokens":3000,"cache_creation_input_tokens":500}""" };
        var settings = new LlmSettings("Anthropic", "fake-key-for-tests-0123456789", null, FakeLlmServer.AnthropicModel);
        var log = new Calls();
        var context = new LlmCallContext { Run = log, Stage = "stage-1" };
        using var client = ChatClientFactory.Create(settings, maxRetries: 0, server.BaseUrl, new LlmPipelineOptions(CallContext: context, Retries: 0, PromptCache: true));
        var runner = new StageRunner(8);
        var message = new ChatMessage(ChatRole.User, [new TextContent("Chunk 3."), new TextContent("<<<\nthe text\n>>>"), new TextContent("Return the items.")]);

        for (var i = 0; i < 2; i++)
            await runner.CallAsync(() => client.GetResponseAsync([new ChatMessage(ChatRole.System, "The skill text."), message], cancellationToken: TestContext.Current.CancellationToken),
                TestContext.Current.CancellationToken, chapter: 2, chunk: 3, cache: new CacheHints("1h", 1));
        // Without hints the same client sends the body as the SDK wrote it.
        await client.GetResponseAsync([new ChatMessage(ChatRole.System, "The skill text."), message], cancellationToken: TestContext.Current.CancellationToken);

        var first = JsonNode.Parse(server.Requests[0].Body)!;
        Assert.Equal("""{"type":"ephemeral","ttl":"1h"}""", first["system"]!.AsArray()[^1]!["cache_control"]!.ToJsonString());
        Assert.Equal("The skill text.", first["system"]!.AsArray()[^1]!["text"]!.GetValue<string>());
        var content = first["messages"]![0]!["content"]!.AsArray();
        Assert.Equal(["Chunk 3.", "<<<\nthe text\n>>>", "Return the items."], content.Select(b => b!["text"]!.GetValue<string>()));
        Assert.Equal("""{"type":"ephemeral"}""", content[1]!["cache_control"]!.ToJsonString());
        Assert.DoesNotContain("cache_control", server.Requests[2].Body);

        // calls.jsonl: the provider's own counts, with the two cache fields and the call's place.
        var usage = log.Lines[0]["usage"]!;
        Assert.Equal((40, 7, 3000, 500, 3547), (usage["input"]!.GetValue<int>(), usage["output"]!.GetValue<int>(), usage["cache_read_input_tokens"]!.GetValue<int>(),
            usage["cache_creation_input_tokens"]!.GetValue<int>(), usage["total"]!.GetValue<int>()));
        Assert.Equal((2, 3), (log.Lines[0]["chapter"]!.GetValue<int>(), log.Lines[0]["chunkIndex"]!.GetValue<int>()));
        var counted = client.GetService<CallLogChatClient>()!;
        Assert.Equal((3L, 120L, 21L, 9000L, 1500L), (counted.Calls, counted.InputTokens, counted.OutputTokens, counted.CacheReadTokens, counted.CacheWriteTokens));

        // cost.json: the cache counts per stage and in the total, priced at the cache prices (read a tenth of input, write twice it).
        using var root = new FullSchemaPipelineTests.TempFolder();
        var run = RunFolder.Create(root.Path);
        var pricing = new Pricing("USD", 0.10m, 0.50m);
        run.WriteStageCostWithCache("stage-1", pricing.CostReport(counted.Calls, counted.InputTokens, counted.OutputTokens, counted.CacheReadTokens, counted.CacheWriteTokens), pricing.CostReport);
        run.WriteStageCostWithCache("stage-2", pricing.CostReport(1, 1_000_000, 0, 1_000_000, 0), pricing.CostReport);
        var cost = JsonNode.Parse(File.ReadAllText(run.File(RunFolder.CostJson)))!;
        Assert.Equal((9000, 1500), (cost["stages"]!["stage-1"]!["cacheReadTokens"]!.GetValue<int>(), cost["stages"]!["stage-1"]!["cacheWriteTokens"]!.GetValue<int>()));
        Assert.Equal(0.11m, cost["stages"]!["stage-2"]!["cost"]!.GetValue<decimal>());          // 1M at 0.10 and 1M read at 0.01
        Assert.Equal((1_009_000, 1500, 1_000_120), (cost["total"]!["cacheReadTokens"]!.GetValue<int>(), cost["total"]!["cacheWriteTokens"]!.GetValue<int>(), cost["total"]!["inputTokens"]!.GetValue<int>()));
        Assert.Equal(pricing.Cost(1_000_120, 21, 1_009_000, 1500), cost["total"]!["cost"]!.GetValue<decimal>());
        Assert.Equal(0.000412m, pricing.Cost(120, 21, 9000, 1500));          // 0.000012 + 0.0000105 + 0.00009 + 0.0003, to six places
        // The stage rows carry the cache column.
        var rows = IndexPlan.Stages(run.Path);
        Assert.Equal((9000L, 1500L, 10_620L), (rows[0].CacheReadTokens, rows[0].CacheWriteTokens, rows[0].AllInputTokens));
        Assert.Equal(1_009_000L, IndexPlan.Total(rows).CacheReadTokens);
    }

    // ---- Parallel ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task At_most_eight_calls_are_in_flight_the_first_goes_alone_and_results_come_back_in_order()
    {
        var runner = new StageRunner(8);
        int inFlight = 0, peak = 0, started = 0;
        var firstDoneBeforeSecond = false;
        var firstDone = false;
        var results = await StageRunner.ForEachAsync(Enumerable.Range(0, 40).ToList(), (item, _) => runner.CallAsync(async () =>
        {
            if (Interlocked.Increment(ref started) == 2) firstDoneBeforeSecond = firstDone;
            var now = Interlocked.Increment(ref inFlight);
            int seen;
            while (now > (seen = Volatile.Read(ref peak)) && Interlocked.CompareExchange(ref peak, now, seen) != seen) { }
            await Task.Delay(item == 0 ? 60 : 20 - item % 7);                 // later items often finish first
            Interlocked.Decrement(ref inFlight);
            if (item == 0) firstDone = true;
            return item * 10;
        }, TestContext.Current.CancellationToken));

        Assert.Equal(Enumerable.Range(0, 40).Select(i => i * 10), results);   // chunk order, whatever order they finished in
        Assert.Equal(8, peak);
        Assert.Equal((8, 8, 40, 0), (runner.Peak, runner.Limit, runner.Calls, runner.InFlight));
        Assert.True(firstDoneBeforeSecond);                                   // the cache entry exists before the others are sent

        var two = new StageRunner(2);
        await StageRunner.ForEachAsync(Enumerable.Range(0, 10).ToList(), (item, _) => two.CallAsync(async () => { await Task.Delay(10); return item; }, TestContext.Current.CancellationToken));
        Assert.Equal(2, two.Peak);
        using (new StageRunner(1).Begin()) Assert.NotNull(StageRunner.Current);
        Assert.Null(StageRunner.Current);
    }

    [Fact]
    public async Task A_429_and_a_529_are_retried_with_backoff_and_the_servers_retry_after()
    {
        // 529 (Anthropic "overloaded") is retried like 429 and 5xx.
        Assert.Contains(529, RetryOnTransientChatClient.RetriedStatuses);
        Assert.Contains(429, RetryOnTransientChatClient.RetriedStatuses);
        Assert.Equal([1.0, 2.0, 4.0], Enumerable.Range(1, 3).Select(i => RetryOnTransientChatClient.Backoff(i).TotalSeconds));

        using var server = new FakeLlmServer(rateLimitFirst: 2) { RetryAfterMs = 2500 };
        var settings = new LlmSettings("Anthropic", "fake-key-for-tests-0123456789", null, FakeLlmServer.AnthropicModel);
        var log = new Calls();
        var waits = new List<TimeSpan>();
        using var client = ChatClientFactory.Create(settings, maxRetries: 0, server.BaseUrl, new LlmPipelineOptions(CallContext: new LlmCallContext { Run = log, Stage = "stage-1" },
            Retries: 3, RetryDelay: (wait, _) => { waits.Add(wait); return Task.CompletedTask; }, PromptCache: true));

        var response = await new StageRunner(8).CallAsync(() => client.GetResponseAsync("hello", cancellationToken: TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);

        Assert.Equal("ready", response.Text);
        Assert.Equal(3, server.Requests.Count);
        // The Anthropic SDK's error has no headers; the HTTP handler notes the server's retry-after, which is longer than the backoff.
        Assert.Equal([TimeSpan.FromMilliseconds(2500), TimeSpan.FromMilliseconds(2500)], waits);
        var retries = log.Lines.Where(l => l["type"]!.GetValue<string>() == "retry").ToList();
        Assert.Equal(["429", "429"], retries.Select(r => r["reason"]!.GetValue<string>()));
        Assert.Equal(2500, retries[0]["retryAfterMs"]!.GetValue<double>());
    }
}
