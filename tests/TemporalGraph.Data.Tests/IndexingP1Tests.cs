using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using TemporalGraph.Cli;
using TemporalGraph.Cli.Crosscheck;
using TemporalGraph.Cli.Gold;
using TemporalGraph.Cli.Indexing;
using TemporalGraph.Llm;
using TemporalGraph.Cli.Runs;

namespace TemporalGraph.Data.Tests;

// Phase P1: skill files, evidence checker, Stage 1 dedupe, Stage 2 grouping/merging, LitBank alignment,
// review file and gold import. No network (StubChatClient in memory, FakeLlmServer on localhost).
// These tests listen to the process-wide indexing ActivitySource; run alone, so spans from other test classes running in
// parallel can't reach the listener (Stage1_emits_one_span_per_chunk_with_the_run_tags failed intermittently with another class's "chunk 0").
[Collection(nameof(SpanListenerCollection))]
public class IndexingP1Tests
{
    private const string Hound = "hound-of-the-baskervilles";
    private static readonly string Text = IndexingShared.BookText(Hound);
    // These stages are the ones of before the one-pass pipeline: 1,000-token windows with an overlap.
    private static readonly Stage0Result Stage0 = TemporalGraph.Cli.Indexing.Stage0.Prepare(BookProfiles.For(Hound), SourceTexts.Read(Hound), SourceTexts.Sha256(Hound), ChunkSettings.Windows);

    private static string Root => RepoPaths.FindRoot();

    // ---- Skill files ------------------------------------------------------------------------------

    [Theory]
    [InlineData(SkillFile.ExtractGraph, 5, 7)]
    [InlineData(SkillFile.ResolveEntities, 4, null)]
    // The Stage 3/4 skills take examples from ch. 5–7 only (not ch. 1–3 tuning gold, ch. 4 or held-out ch. 8).
    [InlineData(SkillFile.AssignStoryTime, 5, 7)]
    // The anchor date and the perception pass follow the same rule.
    [InlineData(SkillFile.PickAnchorDate, 5, 7)]
    [InlineData(SkillFile.RevisePerceptions, 5, 7)]
    public void Skill_file_has_version_sections_and_verbatim_examples_from_its_allowed_chapters(string name, int firstChapter, int? lastChapter)
    {
        var skill = SkillFile.Load(Root, name);
        var start = Stage0.Chapters.Single(c => c.Number == firstChapter).CharStart;
        var end = lastChapter is { } last ? Stage0.Chapters.Single(c => c.Number == last).CharEnd : Text.Length;
        // Whitespace collapsed on both sides, so a quote may span the text's line breaks; quote marks are kept as written.
        static string Collapse(string s) => string.Join(' ', s.Split((char[])[' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries));
        var allowed = Collapse(Text[start..end]);
        var where = lastChapter is null ? $"ch. {firstChapter}+" : $"ch. {firstChapter}–{lastChapter}";

        Assert.True(skill.Version >= 1);
        Assert.NotEmpty(skill.Instructions);
        // Learned rules are written by the improvement loop; they may be empty.
        Assert.DoesNotContain("## ", skill.LearnedRules);
        Assert.InRange(Regex.Matches(skill.Examples, @"^### Example", RegexOptions.Multiline).Count, 1, 3);

        // Every quoted line, and every JSON "evidence" or "reasoning" string, is copied exactly from #2852 in the allowed chapters.
        var quotes = Regex.Matches(skill.Examples, @"^> (.+)$", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToList();
        Assert.NotEmpty(quotes);
        foreach (var q in quotes) Assert.True(allowed.Contains(Collapse(q), StringComparison.Ordinal), $"not verbatim from {where}: {q}");
        var blocks = Regex.Matches(skill.Examples, "```json\n(.*?)\n```", RegexOptions.Singleline);
        Assert.NotEmpty(blocks);
        foreach (Match block in blocks)
        {
            var json = JsonNode.Parse(block.Groups[1].Value)!;
            foreach (var quote in QuoteStrings(json))
                Assert.True(allowed.Contains(Collapse(quote), StringComparison.Ordinal), $"evidence not verbatim from {where}: {quote}");
            foreach (var m in json["mentions"]?.AsArray() ?? [])
            {
                if (m!["text"] is { } text) Assert.Contains(text.GetValue<string>(), m["evidence"]!.GetValue<string>());
                // A name, found in the example's own quoted lines; or the narrator the example states ("Narrator: Dr. Watson.").
                else Assert.Contains(m["name"]!.GetValue<string>(), string.Join(' ', quotes) + " " + string.Join(' ', Regex.Matches(skill.Examples, @"^Narrator: (.+?)\.?$", RegexOptions.Multiline).Select(n => n.Groups[1].Value)));
            }
            // Compact evidence "at:len:q". q is the first 80 characters of a quote that stands in the example's quoted lines at
            // the offset given, counted as the model counts it (lines joined by a line feed).
            var shown = string.Join('\n', quotes);
            foreach (var ev in Strings(json, "ev"))
            {
                var parsed = AEv.Parse(ev)!;
                Assert.True(parsed.At >= 0 && parsed.At + parsed.Len <= shown.Length, $"offset outside the example: {ev}");
                var quote = shown.Substring(parsed.At, parsed.Len);
                Assert.Equal(quote.Length > 80 ? quote[..80] : quote, parsed.Q);
                Assert.True(allowed.Contains(Collapse(quote.Replace('\n', ' ')), StringComparison.Ordinal), $"evidence not verbatim from {where}: {quote}");
            }
        }
    }

    private static IEnumerable<string> Strings(JsonNode? node, string key) => node switch
    {
        JsonObject o => o.SelectMany(kv => kv.Key == key && kv.Value is JsonValue v && v.TryGetValue<string>(out var s) ? [s] : Strings(kv.Value, key)),
        JsonArray a => a.SelectMany(n => Strings(n, key)),
        _ => [],
    };

    /// <summary>Every non-null string under an "evidence" or "reasoning" key, at any depth.</summary>
    private static IEnumerable<string> QuoteStrings(JsonNode? node) => node switch
    {
        JsonObject o => o.SelectMany(kv => kv.Key is "evidence" or "reasoning" && kv.Value is JsonValue v && v.TryGetValue<string>(out var s)
            ? [s] : QuoteStrings(kv.Value)),
        JsonArray a => a.SelectMany(QuoteStrings),
        _ => [],
    };

    // ---- Evidence checker -------------------------------------------------------------------------

    private static ChunkInfo FirstChunk => Stage0.Chunks[0];
    private static string FirstChunkText => Text[FirstChunk.CharStart..FirstChunk.CharEnd];

    [Fact]
    public void Evidence_exact_match_ignores_line_breaks_and_curly_quotes()
    {
        // In the book this sentence wraps across lines (CRLF + 6-space indent) and uses curly quotes.
        const string quote = "It was a fine, thick piece of wood, bulbous-headed, of the sort which is known as a \"Penang lawyer.\"";
        var match = EvidenceChecker.Find(quote, FirstChunkText, FirstChunk.CharStart)!;

        Assert.Equal("exact", match.Method);
        Assert.Equal(1.0, match.Score);
        Assert.StartsWith("It was a fine, thick piece of wood,", Text[match.CharStart..match.CharEnd]);
        Assert.EndsWith("lawyer.”", Text[match.CharStart..match.CharEnd]);
    }

    [Fact]
    public void Evidence_fuzzy_match_scores_a_small_typo_above_the_threshold()
    {
        const string quote = "Mr. Sherlock Holmes, who was usualy very late in the mornings"; // "usualy"
        var match = EvidenceChecker.Find(quote, FirstChunkText, FirstChunk.CharStart)!;

        Assert.Equal("fuzzy", match.Method);
        Assert.InRange(match.Score, 0.9, 0.999);
        Assert.StartsWith("Mr. Sherlock Holmes, who was usually", Text[match.CharStart..match.CharEnd]);
    }

    [Fact]
    public void Evidence_that_is_not_in_the_chunk_is_rejected()
    {
        var invented = new ExtractedMention("Sir Henry", MentionType.Character, "Sir Henry stood at the door of the Hall and waved.");
        var outcome = Stage1.Check(invented, FirstChunk, FirstChunkText, Text, 0.9);

        Assert.Null(outcome.Mention);
        Assert.StartsWith("evidence not in chunk", outcome.Rejected!.Reason);
        Assert.True(outcome.Rejected.BestMatch!.Score < 0.9);
    }

    [Fact]
    public void Mention_text_must_be_inside_its_evidence()
    {
        var wrong = new ExtractedMention("Watson", MentionType.Character, "I stood upon the hearth-rug and picked up the stick");
        var outcome = Stage1.Check(wrong, FirstChunk, FirstChunkText, Text, 0.9);

        Assert.Equal("mention text not found inside the matched evidence", outcome.Rejected!.Reason);
    }

    [Fact]
    public void Accepted_mention_offsets_point_at_the_name_in_the_book_text()
    {
        var m = new ExtractedMention("Sherlock Holmes", MentionType.Character, "Mr. Sherlock Holmes, who was usually very late in the mornings");
        var outcome = Stage1.Check(m, FirstChunk, FirstChunkText, Text, 0.9);

        Assert.Equal("Sherlock Holmes", Text[outcome.Mention!.CharStart..outcome.Mention.CharEnd]);
    }

    // ---- Stage 1 end to end with an in-memory client (overlap dedupe, rejects, glean) --------------

    [Fact]
    public async Task Stage1_dedupes_the_chunk_overlap_logs_rejects_and_marks_gleaned_mentions()
    {
        using var root = new TempFolder();
        var run = RunFolder.Create(Path.Combine(root.Path, "runs"));
        var chapter1 = Stage0.Chunks.Where(c => c.Chapter == 1).Take(2).ToList();
        var overlap = Text[chapter1[1].CharStart..chapter1[0].CharEnd];
        // A whole line of the overlap, and a capitalized word in it, as the mention both chunks return.
        var sentence = overlap.Split("\r\n").Select(l => l.Trim()).First(l => l.Length > 30 && Regex.IsMatch(l, @"\b[A-Z][a-z]+\b"));
        var name = Regex.Match(sentence, @"\b[A-Z][a-z]+\b").Value;

        var client = new StubChatClient(prompt =>
        {
            if (prompt.Contains(Stage1.GleanPrompt))
                return Json(("Baker Street", "Location", "Baker Street, London, W.C.")); // invented quote → rejected
            return Json((name, "Character", sentence)); // same overlap mention from both chunks
        });
        var skill = OldSkills.Load(SkillFile.ExtractMentions);
        var stage0 = Stage0 with { Chunks = chapter1 };

        var r = await Stage1.RunAsync(client, null, skill, stage0, Text, 1, 1, glean: true, threshold: 0.9, run, TestContext.Current.CancellationToken);

        Assert.Equal(2, r.Chunks.Count);
        Assert.Single(r.Mentions);               // one mention, found by both chunks
        Assert.Equal(1, r.DuplicatesDropped);
        Assert.Equal(2, r.Rejected.Count);        // the invented glean quote, once per chunk
        Assert.All(r.Rejected, x => Assert.Equal("glean", x.Pass));
        var failures = File.ReadAllLines(run.File(RunFolder.FailuresJsonl)).Select(l => JsonNode.Parse(l)!).ToList();
        Assert.Equal(2, failures.Count);
        Assert.All(failures, f => Assert.Equal("quote mismatch", f["type"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Stage1_emits_one_span_per_chunk_with_the_run_tags()
    {
        var spans = new List<System.Diagnostics.Activity>();
        using var listener = new System.Diagnostics.ActivityListener
        {
            ShouldListenTo = s => s.Name == TemporalGraph.Cli.Telemetry.IndexingTelemetry.SourceName,
            Sample = (ref System.Diagnostics.ActivityCreationOptions<System.Diagnostics.ActivityContext> _) => System.Diagnostics.ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a => { lock (spans) spans.Add(a); },
        };
        System.Diagnostics.ActivitySource.AddActivityListener(listener);

        var client = new StubChatClient(_ => Json());
        var skill = OldSkills.Load(SkillFile.ExtractMentions);
        var chunks = Stage0.Chunks.Where(c => c.Chapter == 2).Take(2).ToList();
        await Stage1.RunAsync(client, null, skill, Stage0 with { Chunks = chunks }, Text, 2, 2, glean: false, threshold: 0.9, run: null, TestContext.Current.CancellationToken);

        var chunkSpans = spans.Where(s => s.OperationName.StartsWith("chunk ")).ToList();
        Assert.Equal(chunks.Select(c => $"chunk {c.Index}"), chunkSpans.Select(s => s.OperationName));
        Assert.All(chunkSpans, s =>
        {
            Assert.Equal("stage-1", s.GetTagItem("pipeline.stage"));
            Assert.Equal(2, s.GetTagItem("book.chapter"));
            Assert.Equal("extract-mentions", s.GetTagItem("skill.name"));
            Assert.Equal(1, s.GetTagItem("skill.version"));
            Assert.NotNull(s.GetTagItem("chunk.index"));
        });
    }

    [Fact]
    public void Dedupe_keeps_the_first_of_identical_offsets_and_type()
    {
        var match = new EvidenceMatch("exact", 1, 0, 10);
        Mention M(int chunk, int start, MentionType type = MentionType.Character, string pass = "first") =>
            new(0, "Holmes", type, chunk, 1, pass, start, start + 6, "", match);

        var (kept, dropped) = Stage1.Dedupe([M(1, 100), M(2, 100), M(2, 100, pass: "glean"), M(2, 100, MentionType.Location), M(2, 200)]);

        Assert.Equal(2, dropped);
        Assert.Equal([(1, 100, MentionType.Character), (2, 100, MentionType.Location), (2, 200, MentionType.Character)],
            kept.Select(m => (m.Chunk, m.CharStart, m.Type)));
        Assert.Equal([1, 2, 3], kept.Select(m => m.Id));
    }

    // ---- Structured output: strict JSON schema on the wire ------------------------------------------

    [Fact]
    public async Task Structured_output_request_is_strict_json_schema()
    {
        using var server = new FakeLlmServer { OpenAIReply = """{"mentions":[]}""" };
        var settings = LlmSettings.From(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Llm:Type"] = "OpenAI", ["Llm:Model"] = "m", ["Llm:Endpoint"] = server.BaseUrl + "/openai/v1/", ["Llm:ApiKey"] = "fake-key-for-tests-0123456789",
        }).Build());
        using var client = ChatClientFactory.Create(settings, maxRetries: 0);

        var response = await client.GetResponseAsync<MentionExtraction>("x", Stage1.Json, Stage1.StrictOptions(), useJsonSchemaResponseFormat: true, TestContext.Current.CancellationToken);

        Assert.Empty(response.Result.Mentions);
        var body = JsonNode.Parse(Assert.Single(server.Requests).Body)!;
        var format = body["response_format"]!;
        Assert.Equal("json_schema", format["type"]!.GetValue<string>());
        Assert.True(format["json_schema"]!["strict"]!.GetValue<bool>());
        var schema = format["json_schema"]!["schema"]!;
        Assert.False(schema["additionalProperties"]!.GetValue<bool>());
        var item = schema["properties"]!["mentions"]!["items"]!;
        Assert.False(item["additionalProperties"]!.GetValue<bool>());
        Assert.Equal(["evidence", "text", "type"], item["required"]!.AsArray().Select(x => x!.GetValue<string>()).Order());
    }

    // ---- Stage 2 ----------------------------------------------------------------------------------

    private static Mention Men(int id, string text, int start, MentionType type = MentionType.Character) =>
        new(id, text, type, 0, 1, "first", start, start + text.Length, text, new EvidenceMatch("exact", 1, start, start + text.Length));

    [Fact]
    public void Stage2_groups_by_normalized_name_and_finds_candidates()
    {
        var groups = Stage2.Group([Men(1, "Holmes", 10), Men(2, "Sherlock Holmes", 20), Men(3, "Holmes’s", 30), Men(4, "Mrs. Barrymore", 40),
            Men(5, "Barrymore", 50), Men(6, "London", 60, MentionType.Location), Men(7, "Dr. Watson", 70)]);

        Assert.Equal(["holmes", "sherlock holmes", "mrs. barrymore", "barrymore", "london", "dr. watson"], groups.Select(g => g.Key));
        var candidates = Stage2.Candidates(groups, new ResolveSettings());
        string Name(int id) => groups.Single(g => g.Id == id).Key;
        var pairs = candidates.Select(c => $"{Name(c.A)}|{Name(c.B)}|{c.Method}").ToList();
        Assert.Contains("holmes|sherlock holmes|token-containment", pairs);
        Assert.Contains("mrs. barrymore|barrymore|token-containment", pairs); // the LLM decides; the skill says Mrs. is a different person
        Assert.DoesNotContain(pairs, p => p.Contains("london"));             // no cross-type pairs
    }

    [Fact]
    public void Stage2_merge_uses_union_find_and_reports_conflicts()
    {
        var groups = Stage2.Group([Men(1, "Holmes", 10), Men(2, "Sherlock Holmes", 20), Men(3, "Mr. Holmes", 30)]);
        PairDecision D(int a, int b, bool same, string? canonical) =>
            new(new CandidatePair(a, b, "token-containment", 1), groups[a - 1].Key, groups[b - 1].Key, new MergeDecision(same, canonical, "r"), [], []);

        var (entities, conflicts) = Stage2.Merge(groups, [D(1, 2, true, "Sherlock Holmes"), D(2, 3, true, "Sherlock Holmes"), D(1, 3, false, null)]);

        var e = Assert.Single(entities);
        Assert.Equal("Sherlock Holmes", e.CanonicalName);
        Assert.Equal(["Holmes", "Mr. Holmes", "Sherlock Holmes"], e.Aliases);
        Assert.Equal(10, e.FirstMention);
        Assert.Single(conflicts);
    }

    // ---- LitBank ----------------------------------------------------------------------------------

    [Fact]
    public void LitBank_tokens_align_with_chapter_1_of_our_text()
    {
        var (entities, alignment) = LitBank.Load(Root, Text, Stage0.Chapters[0].CharStart);

        // LitBank's text is the #3070 edition: one word differs ("gray" vs #2852 "grey"); everything else aligns.
        Assert.Equal(["\"gray\" at LitBank offset 9249"], alignment.Unaligned);
        Assert.Equal(alignment.Tokens - 1, alignment.Aligned);
        Assert.Equivalent(new[] { "-- = —", "_ (italics markup in #2852) skipped" }, alignment.Substituted.Keys); // LitBank: "--" for "—", no "_" italics
        Assert.Equal(Stage0.Chapters[0].CharStart, alignment.FirstCharStart); // "Chapter 1." heading
        Assert.True(alignment.LastCharEnd < Stage0.Chapters[0].CharEnd);
        var mortimer = entities.First(e => e.Text.StartsWith("James Mortimer"));
        Assert.Equal("James Mortimer, M.R.C.S.", Text[mortimer.CharStart!.Value..mortimer.CharEnd!.Value]);
        Assert.True(mortimer.Proper);
        Assert.False(entities.First(e => e.Text == "our visitor").Proper);
        Assert.False(entities.First(e => e.Text == "his friends of the C.C.H.").Proper);
        Assert.False(entities.First(e => e.Text == "Mr.").Proper); // nested honorific
    }

    // ---- Review file and gold import --------------------------------------------------------------

    private const string Reviewed = """
        # Hound · ch. 1–3 · draft gold for review

        ## Entities

        - [x] Character · Sherlock Holmes · aliases: Holmes, Mr. Sherlock Holmes · first: ch1 "Mr. Sherlock Holmes, who was usually very late"
        - [~] Character · James Mortimer · aliases: Dr. Mortimer, "James Mortimer, M.R.C.S." · first: ch1 "To James Mortimer, M.R.C.S., from his friends"
        - [-] Location · the hearth-rug · aliases: — · first: ch1 "I stood upon the hearth-rug"

        ## Merges

        - [x] merge · Holmes + Mr. Sherlock Holmes → Sherlock Holmes · reason: same man
        - [-] merge · Watson + Dr. Watson → Dr. Watson · reason: same man

        ## Cross-check disagreements

        _BookNLP: not run._
        - [x] LitBank · not in ours · Character · C.C.H. · ch1 "his friends of the C.C.H." · LitBank PER ×1

        ## Missing (add here)

        <!-- Add one line per missing entity -->
        - [x] Location · Charing Cross Hospital · aliases: — · first: ch1 "Charing Cross Hospital"
        """;

    [Fact]
    public void Review_parser_reads_all_three_marks_and_the_missing_section()
    {
        var parsed = ReviewFile.Parse(Reviewed);

        Assert.Empty(parsed.Errors);
        Assert.Equal(["Sherlock Holmes:accepted", "James Mortimer:edited", "Charing Cross Hospital:added"], parsed.Entities.Select(e => $"{e.Name}:{e.Status}"));
        Assert.Equal(["Dr. Mortimer", "James Mortimer, M.R.C.S."], parsed.Entities[1].Aliases);
        Assert.Empty(parsed.Entities[2].Aliases);
        Assert.Equal("the hearth-rug", Assert.Single(parsed.Rejected).Name);
        Assert.Equal([Mark.Accept, Mark.Reject], parsed.Merges.Select(m => m.Mark));
        Assert.Equal(Mark.Accept, Assert.Single(parsed.CrossChecks).Mark);
        Assert.Equal(1, parsed.Entities[0].FirstChapter);
    }

    [Fact]
    public void Review_parser_ignores_html_comments_trailing_own_line_and_multi_line()
    {
        const string content = """
            ## Entities

            - [x] Character · Sherlock Holmes · aliases: Holmes · first: ch1 "Mr. Sherlock Holmes" <!-- review: checked ch1 -->
            <!-- review: a note on its own line -->
            - [-] Character · Jove · aliases: — · first: ch1 "Jove" <!-- review: "By Jove" is an exclamation -->

            ## Merges

            - [-] merge · Hall + Lafter Hall → Lafter Hall · reason: r <!-- review: WRONG MERGE -->
            <!-- review: a note
                 spanning lines -->

            ## Missing (add here)

            - [x] Location · Baker Street · aliases: — · first: ch3 "Baker Street until evening"
            """;

        var parsed = ReviewFile.Parse(content);

        Assert.Empty(parsed.Errors);
        Assert.Equal(["Sherlock Holmes", "Baker Street"], parsed.Entities.Select(e => e.Name));
        Assert.Equal("Mr. Sherlock Holmes", parsed.Entities[0].FirstQuote);
        Assert.Equal("Jove", Assert.Single(parsed.Rejected).Name);
        var merge = Assert.Single(parsed.Merges);
        Assert.Equal("merge · Hall + Lafter Hall → Lafter Hall · reason: r", merge.Text);
        Assert.DoesNotContain("<!--", merge.Text);
        Assert.Equal(15, parsed.Entities[1].Line); // line numbers survive comment removal (incl. the 2-line comment)
    }

    [Fact]
    public void Review_parser_reports_an_unclosed_comment()
    {
        var parsed = ReviewFile.Parse("## Entities\n\n- [x] Character · A · aliases: — · first: ch1 \"q\" <!-- never closed\n- [x] Character · B · aliases: — · first: ch1 \"q\"\n");

        var error = Assert.Single(parsed.Errors);
        Assert.Equal(3, error.Line);
        Assert.Contains("never closed", error.Problem);
    }

    [Theory]
    [InlineData("- [ ] Character · Sherlock Holmes · aliases: Holmes · first: ch1 \"q\"", "not reviewed")]
    [InlineData("- [x] Person · Sherlock Holmes · aliases: Holmes · first: ch1 \"q\"", "type must be")]
    [InlineData("- [x] Character · Sherlock Holmes · first: ch1 \"q\"", "expected 4 fields")]
    [InlineData("- [x] Character · Sherlock Holmes · aliases: Holmes · first: chapter one", "first must look like")]
    [InlineData("- [x] Character - Sherlock Holmes - aliases: Holmes - first: ch1 \"q\"", "expected 4 fields")]
    [InlineData("* [x] Character · Sherlock Holmes · aliases: Holmes · first: ch1 \"q\"", "not a checklist line")]
    [InlineData("- [?] Character · Sherlock Holmes · aliases: Holmes · first: ch1 \"q\"", "expected '- [x]")]
    public void Gold_import_refuses_malformed_lines_and_writes_nothing(string badLine, string problem)
    {
        using var folder = new TempFolder();
        var review = Path.Combine(folder.Path, "ch01-03.review.md");
        var gold = Path.Combine(folder.Path, "ch01-03.json");
        File.WriteAllText(review, Reviewed.Replace("- [x] Character · Sherlock Holmes · aliases: Holmes, Mr. Sherlock Holmes · first: ch1 \"Mr. Sherlock Holmes, who was usually very late\"", badLine));

        var outcome = GoldImportCommand.Import(Hound, review, gold);

        var error = Assert.Single(outcome.Errors);
        Assert.Contains(problem, error.Problem);
        Assert.Equal(5, error.Line);
        Assert.False(File.Exists(gold));
    }

    [Fact]
    public void Gold_import_writes_gold_json_from_a_reviewed_file()
    {
        using var folder = new TempFolder();
        var review = Path.Combine(folder.Path, "ch01-03.review.md");
        var gold = Path.Combine(folder.Path, "ch01-03.json");
        File.WriteAllText(review, Reviewed);

        var outcome = GoldImportCommand.Import(Hound, review, gold);

        Assert.Empty(outcome.Errors);
        var json = JsonNode.Parse(File.ReadAllText(gold))!;
        Assert.Equal(3, json["entities"]!.AsArray().Count);
        Assert.Equal("ch01-03.review.md", json["source"]!["file"]!.GetValue<string>());
        Assert.Equal("reject", json["mergeReview"]![1]!["mark"]!.GetValue<string>());
    }

    [Fact]
    public void Generated_review_file_parses_back_once_marked()
    {
        var mentions = new[] { Men(1, "Sherlock Holmes", 1900), Men(2, "Holmes", 2000), Men(3, "Watson", 2100) };
        var groups = Stage2.Group(mentions);
        var decision = new PairDecision(new CandidatePair(1, 2, "token-containment", 1), "Sherlock Holmes", "Holmes",
            new MergeDecision(true, "Sherlock Holmes", "Holmes is the surname of Sherlock Holmes."), [], []);
        var (entities, conflicts) = Stage2.Merge(groups, [decision]);
        var stage2 = new Stage2Result(Hound, groups, [decision.Pair], [decision], entities, conflicts, new ResolveSettings());

        var content = ReviewFile.Generate("The Hound of the Baskervilles", Hound, "1–3", "run-1", [], stage2,
            [Comparison.BookNlpUnavailable("data/crosscheck/booknlp/hound")], Text, IndexingShared.ChapterOf(Stage0));

        Assert.Contains("- [ ] Character · Sherlock Holmes · aliases: Holmes · first: ch1 \"", content);
        var unmarked = ReviewFile.Parse(content);
        Assert.Equal(3, unmarked.Errors.Count); // 2 entities + 1 merge, all unreviewed
        var marked = ReviewFile.Parse(content.Replace("- [ ] ", "- [x] "));
        Assert.Empty(marked.Errors);
        Assert.Equal(["Sherlock Holmes", "Watson"], marked.Entities.Select(e => e.Name));
    }

    // ---- Helpers ----------------------------------------------------------------------------------

    private static string Json(params (string Text, string Type, string Evidence)[] mentions) =>
        JsonSerializer.Serialize(new { mentions = mentions.Select(m => new { text = m.Text, type = m.Type, evidence = m.Evidence }) });

    /// <summary>In-memory chat client: answers each call with <paramref name="reply"/>(last user message).</summary>
    private sealed class StubChatClient(Func<string, string> reply) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply(messages.Last(m => m.Role == ChatRole.User).Text))));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class TempFolder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "TemporalGraph.Data.Tests", Guid.NewGuid().ToString("N"));
        public TempFolder() => Directory.CreateDirectory(Path);
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
    }
}

[CollectionDefinition(nameof(SpanListenerCollection), DisableParallelization = true)]
public sealed class SpanListenerCollection;
