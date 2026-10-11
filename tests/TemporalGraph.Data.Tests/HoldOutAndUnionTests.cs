using System.Text.Json;
using System.Text.Json.Nodes;
using Spectre.Console;
using TemporalGraph.Cli;
using TemporalGraph.Cli.Crosscheck;
using TemporalGraph.Cli.Gold;
using TemporalGraph.Cli.Indexing;
using TemporalGraph.Cli.Runs;
using TemporalGraph.Cli.Scoring;

namespace TemporalGraph.Data.Tests;

// Review drafts from the union of several runs, and the held-out chapter guard in index score.
public class HoldOutAndUnionTests
{
    private const string Hound = "hound-of-the-baskervilles";
    private static readonly string Text = IndexingShared.BookText(Hound);
    private static readonly Stage0Result Stage0 = IndexingShared.Stage0For(Hound);

    // ---- Union of runs ----------------------------------------------------------------------------

    private static Entity E(int id, string canonical, MentionType type, int first, params string[] aliases) =>
        new(id, canonical, type, [canonical, .. aliases], [], first, [id]);

    private static Stage2Result Run(params Entity[] entities) => new(Hound, [], [], [], entities, [], new ResolveSettings());

    private static readonly Stage2Result[] Runs =
    [
        // Run 0 wrongly merged Lafter Hall into Baskerville Hall.
        Run(E(1, "Sherlock Holmes", MentionType.Character, 1900, "Holmes"), E(2, "Baskerville Hall", MentionType.Location, 20000, "Lafter Hall")),
        Run(E(1, "Mr. Holmes", MentionType.Character, 1900, "Holmes"), E(2, "Baskerville Hall", MentionType.Location, 20000), E(3, "Lafter Hall", MentionType.Location, 30000)),
        Run(E(1, "Sherlock Holmes", MentionType.Character, 1950)),
    ];

    [Fact]
    public void Union_matches_entities_across_runs_one_to_one_and_counts_runs()
    {
        var union = ReviewFile.Union(Runs);

        Assert.Equal(["Sherlock Holmes", "Baskerville Hall", "Lafter Hall"], union.Select(u => u.CanonicalName));
        Assert.Equal([3, 2, 1], union.Select(u => u.Runs));
        // Canonical name: the most common one; aliases: the other names, most runs first.
        Assert.Equal(["Holmes", "Mr. Holmes"], union[0].Aliases);
        Assert.Equal(2, union[0].NameRuns["Holmes"]);
        Assert.Equal(1900, union[0].FirstMention);
        // Run 0's wrong merge doesn't swallow run 1's separate Lafter Hall.
        Assert.Equal(["Lafter Hall"], union[1].Aliases);
        Assert.Equal([1], union[2].Members.Select(m => m.Run));
    }

    [Fact]
    public void Union_never_joins_different_types()
    {
        var union = ReviewFile.Union([Run(E(1, "Baskerville", MentionType.Character, 1900)), Run(E(1, "Baskerville", MentionType.Location, 1900))]);
        Assert.Equal(2, union.Count);
        Assert.All(union, u => Assert.Equal(1, u.Runs));
    }

    [Fact]
    public void Union_draft_says_how_many_runs_found_each_line_and_parses_back_once_marked()
    {
        var unavailable = new[] { Comparison.BookNlpUnavailable("data/crosscheck/booknlp/hound") };
        var sources = Runs.Select((r, i) => new ReviewSource($"run-{i}", r, unavailable)).ToList();
        var content = ReviewFile.Generate("The Hound of the Baskervilles", Hound, "1–3", sources, Text, IndexingShared.ChapterOf(Stage0));

        Assert.Contains("union of 3 runs: `run-0`, `run-1`, `run-2`", content);
        Assert.Contains("- [ ] Character · Sherlock Holmes · aliases: Holmes, Mr. Holmes · first: ch1 \"", content);
        Assert.Contains("<!-- 3/3 runs · canonical: Sherlock Holmes 2/3, Mr. Holmes 1/3 · fewer runs: Holmes 2/3, Mr. Holmes 1/3 -->", content);
        Assert.Contains("· aliases: — · first: ch2 \"", content.Split('\n').Single(l => l.Contains("Location · Lafter Hall")));
        Assert.Contains("<!-- 1/3 runs -->", content.Split('\n').Single(l => l.Contains("Location · Lafter Hall")));
        Assert.Single(content.Split('\n'), l => l.StartsWith("_BookNLP: "));

        var marked = ReviewFile.Parse(content.Replace("- [ ] ", "- [x] "));
        Assert.Empty(marked.Errors);
        Assert.Equal(["Sherlock Holmes", "Baskerville Hall", "Lafter Hall"], marked.Entities.Select(e => e.Name));
        Assert.Equal(["Holmes", "Mr. Holmes"], marked.Entities[0].Aliases);
    }

    [Fact]
    public void A_single_run_draft_has_no_run_counts()
    {
        var content = ReviewFile.Generate("The Hound of the Baskervilles", Hound, "1–3", [new ReviewSource("run-1", Runs[0], [])], Text, IndexingShared.ChapterOf(Stage0));
        Assert.Contains("· run `run-1` ·", content);
        Assert.DoesNotContain("runs -->", content);
    }

    [Fact]
    public void Chapter_slug_is_a_range_or_one_chapter()
    {
        Assert.Equal("ch01-03", IndexingShared.ChapterSlug(1, 3));
        Assert.Equal("ch04", IndexingShared.ChapterSlug(4, 4));
    }

    // ---- Held-out chapters ------------------------------------------------------------------------

    [Fact]
    public void Hound_chapter_8_is_held_out()
    {
        Assert.Equal([8], HoldOut.Chapters[Hound]);
        Assert.True(HoldOut.Touches(Hound, 8, 8));
        Assert.True(HoldOut.Touches(Hound, 7, 9));
        Assert.False(HoldOut.Touches(Hound, 1, 3));
        Assert.False(HoldOut.Touches(Hound, 4, 4));
        Assert.False(HoldOut.Touches("some-other-book", 8, 8));
    }

    /// <summary>A run folder on chapters [first, last] with two entities: "Sir Henry" (in gold) and "the cabman" (not).</summary>
    private static (RunFolder Run, string Gold) HeldOutFixture(TempFolder folder, int first, int last)
    {
        var path = Path.Combine(folder.Path, "runs", "20261007T000000Z-abcdef");
        Directory.CreateDirectory(path);
        var start = Stage0.Chapters.Single(c => c.Number == first).CharStart;
        var henry = Text.IndexOf("Sir Henry", start, StringComparison.Ordinal);
        var cab = Text.IndexOf("cabman", start, StringComparison.Ordinal);
        var chapter = IndexingShared.ChapterOf(Stage0);
        File.WriteAllText(Path.Combine(path, RunFolder.RunJson), JsonSerializer.Serialize(new { runId = "20261007T000000Z-abcdef", book = Hound, chapters = new { first, last } }));
        File.WriteAllText(Path.Combine(path, RunFolder.Stage2Json), JsonSerializer.Serialize(new
        {
            book = Hound,
            entities = new object[]
            {
                new { id = 1, canonicalName = "Sir Henry", type = "Character", aliases = new[] { "Sir Henry" }, mentions = new[] { new { charStart = henry, charEnd = henry + 9, chapter = chapter(henry), text = "Sir Henry" } }, groups = new[] { 1 } },
                new { id = 2, canonicalName = "the cabman", type = "Character", aliases = new[] { "the cabman" }, mentions = new[] { new { charStart = cab, charEnd = cab + 6, chapter = chapter(cab), text = "cabman" } }, groups = new[] { 2 } },
            },
            merges = Array.Empty<object>(), notMerged = Array.Empty<object>(), conflicts = Array.Empty<string>(),
        }));
        // An earlier, unhidden scoring line that must not survive a hidden re-score.
        File.WriteAllText(Path.Combine(path, RunFolder.FailuresJsonl), JsonSerializer.Serialize(new { type = FailureTags.SpuriousEntity, stage = ScoreCommand.Stage, name = "the cabman" }) + "\n");
        var gold = Path.Combine(folder.Path, "ch08.json");
        File.WriteAllText(gold, JsonSerializer.Serialize(new
        {
            entities = new[]
            {
                new { type = "Character", name = "Sir Henry Baskerville", aliases = new[] { "Sir Henry" }, first = new { chapter = first, quote = "q" } },
                new { type = "Character", name = "Dr. Mortimer", aliases = Array.Empty<string>(), first = new { chapter = first, quote = "q" } },
            },
            rejected = Array.Empty<object>(),
        }));
        return (RunFolder.Open(Path.Combine(folder.Path, "runs"), path), gold);
    }

    private static string Rendered(IReadOnlyList<ScoreCommand.Outcome> outcomes, bool hide)
    {
        var writer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(writer), Ansi = AnsiSupport.No, ColorSystem = ColorSystemSupport.NoColors });
        console.Profile.Width = 300;
        ScoreCommand.Render(console, outcomes, hide);
        return writer.ToString();
    }

    [Fact]
    public void Hide_holdout_details_prints_and_writes_only_the_f1_of_a_held_out_run()
    {
        using var folder = new TempFolder();
        var (run, gold) = HeldOutFixture(folder, 8, 8);

        var outcome = ScoreCommand.ScoreRun(folder.Path, run, gold, hideHoldoutDetails: true);
        Assert.True(outcome.HeldOut);
        Assert.True(outcome.DetailsHidden);
        Assert.Equal(0.5, outcome.Result.Overall.F1); // 1 of 2 run entities, 1 of 2 gold

        var printed = Rendered([outcome], hide: true);
        Assert.Contains("held-out (details hidden)", printed);
        Assert.Contains("0.500", printed);
        foreach (var detail in new[] { "cabman", "Mortimer", "Sir Henry", "Spurious", "Missed" })
            Assert.DoesNotContain(detail, printed);

        var scores = JsonNode.Parse(File.ReadAllText(run.File(ScoreCommand.ScoresJson)))!.AsObject();
        Assert.Equal(["runId", "book", "gold", "scoredAt", "heldOut", "detailsHidden", "overall"], scores.Select(kv => kv.Key));
        Assert.Equal(["f1"], scores["overall"]!.AsObject().Select(kv => kv.Key));
        var written = File.ReadAllText(run.File(ScoreCommand.ScoresJson)) + File.ReadAllText(run.File(ScoreCommand.FailuresMd)) + File.ReadAllText(run.File(RunFolder.FailuresJsonl));
        foreach (var detail in new[] { "cabman", "Mortimer", "Sir Henry" })
            Assert.DoesNotContain(detail, written);
    }

    [Fact]
    public void Without_the_flag_a_held_out_run_is_reported_in_full_in_its_own_held_out_table()
    {
        using var folder = new TempFolder();
        var (run, gold) = HeldOutFixture(folder, 8, 8);

        var outcome = ScoreCommand.ScoreRun(folder.Path, run, gold);
        Assert.True(outcome.HeldOut);
        Assert.False(outcome.DetailsHidden);
        var printed = Rendered([outcome], hide: false);
        Assert.Contains("held-out", printed);
        Assert.DoesNotContain("scored", printed);
        Assert.Contains("Spurious", printed);
        Assert.Contains("the cabman", File.ReadAllText(run.File(ScoreCommand.FailuresMd)));
    }

    [Fact]
    public void The_flag_changes_nothing_for_a_run_outside_the_held_out_chapters()
    {
        using var folder = new TempFolder();
        var (run, gold) = HeldOutFixture(folder, 3, 3);

        var outcome = ScoreCommand.ScoreRun(folder.Path, run, gold, hideHoldoutDetails: true);
        Assert.False(outcome.HeldOut);
        Assert.False(outcome.DetailsHidden);
        var printed = Rendered([outcome], hide: true);
        Assert.Contains("scored", printed);
        Assert.DoesNotContain("held-out", printed);
        Assert.Contains("the cabman", File.ReadAllText(run.File(ScoreCommand.FailuresMd)));
    }

    [Fact]
    public void Chapters_option_scores_a_slice_and_applies_the_held_out_rule_to_the_scored_chapters()
    {
        // A ch. 1-15 run (it covers held-out ch. 8). The fixture's two mentions are the first "Sir Henry" and "cabman" in the book.
        using var folder = new TempFolder();
        var (run, gold) = HeldOutFixture(folder, 1, 15);
        var chapter = IndexingShared.ChapterOf(Stage0);
        var inSlice = new[] { Text.IndexOf("Sir Henry", StringComparison.Ordinal), Text.IndexOf("cabman", StringComparison.Ordinal) }.Count(p => chapter(p) <= 3);

        var slice = ScoreCommand.ScoreRun(folder.Path, run, gold, hideHoldoutDetails: true, range: (1, 3));
        Assert.False(slice.HeldOut);                       // ch. 1-3 scored: not held-out, though the run covers ch. 8
        Assert.False(slice.DetailsHidden);
        Assert.Equal(inSlice, slice.Result.Overall.Predicted);
        Assert.True(File.Exists(run.File("scores-ch01-03.json")));
        Assert.True(File.Exists(run.File("failures-ch01-03.md")));
        Assert.False(File.Exists(run.File(ScoreCommand.ScoresJson)));

        var whole = ScoreCommand.ScoreRun(folder.Path, run, gold, hideHoldoutDetails: true, range: (1, 15));
        Assert.True(whole.HeldOut);                        // a range with ch. 8: held-out, details hidden
        Assert.True(whole.DetailsHidden);
        Assert.Equal(["f1"], JsonNode.Parse(File.ReadAllText(run.File("scores-ch01-15.json")))!["overall"]!.AsObject().Select(kv => kv.Key));

        Assert.Equal("scores.json", ScoreCommand.SliceFile("scores.json", null));
        Assert.Equal("facts-scores-ch04.json", ScoreCommand.SliceFile("facts-scores.json", (4, 4)));
        Assert.Null(ScoreCommand.ParseRange("3-1"));
    }

    [Fact]
    public void The_loop_lists_run_folders_without_held_out_or_unidentifiable_runs()
    {
        using var folder = new TempFolder();
        var runs = Path.Combine(folder.Path, "runs");
        void RunJson(string id, string? json)
        {
            Directory.CreateDirectory(Path.Combine(runs, id));
            if (json is not null) File.WriteAllText(Path.Combine(runs, id, RunFolder.RunJson), json);
        }
        RunJson("a-ch01-03", JsonSerializer.Serialize(new { book = Hound, chapters = new { first = 1, last = 3 } }));
        RunJson("b-ch08", JsonSerializer.Serialize(new { book = Hound, chapters = new { first = 8, last = 8 } }));
        RunJson("c-ch07-09", JsonSerializer.Serialize(new { book = Hound, chapters = new { first = 7, last = 9 } }));
        RunJson("d-other-book-ch08", JsonSerializer.Serialize(new { book = "another-book", chapters = new { first = 8, last = 8 } }));
        RunJson("e-no-run-json", null);
        RunJson("f-no-chapters", JsonSerializer.Serialize(new { book = Hound }));

        var (included, skipped) = HoldOut.LoopRunFolders(runs);

        Assert.Equal(["a-ch01-03", "d-other-book-ch08"], included.Select(r => r.RunId));
        Assert.Equal([("b-ch08", "held-out"), ("c-ch07-09", "held-out")], skipped.Where(s => s.Reason == HoldOut.Label).Select(s => (s.RunId, s.Reason)));
        Assert.Equal(["e-no-run-json", "f-no-chapters"], skipped.Where(s => s.Reason != HoldOut.Label).Select(s => s.RunId));
        Assert.Empty(HoldOut.LoopRunFolders(Path.Combine(folder.Path, "missing")).Runs);
    }

    [Theory]
    [InlineData(SkillFile.ExtractGraph)]
    [InlineData(SkillFile.ResolveEntities)]
    public void No_skill_example_quote_comes_from_a_held_out_chapter(string name)
    {
        var skill = SkillFile.Load(RepoPaths.FindRoot(), name);
        var held = HoldOut.Chapters[Hound].Select(n => Stage0.Chapters.Single(c => c.Number == n)).ToList();
        var quotes = System.Text.RegularExpressions.Regex.Matches(skill.Examples, @"^> (.+)$", System.Text.RegularExpressions.RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToList();
        Assert.NotEmpty(quotes);
        // A quoted paragraph runs over the book's line breaks: both sides are compared with their whitespace as single spaces.
        var flat = new System.Text.StringBuilder();
        var origin = new List<int>();
        for (var i = 0; i < Text.Length; i++)
        {
            if (!char.IsWhiteSpace(Text[i])) { flat.Append(Text[i]); origin.Add(i); }
            else if (flat.Length > 0 && flat[^1] != ' ') { flat.Append(' '); origin.Add(i); }
        }
        var book = flat.ToString();
        foreach (var q in quotes.Select(q => string.Join(' ', q.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries))))
        {
            // Every occurrence in the book, not only the first.
            var found = false;
            for (var hit = book.IndexOf(q, StringComparison.Ordinal); hit >= 0; hit = book.IndexOf(q, hit + 1, StringComparison.Ordinal))
            {
                found = true;
                var at = origin[hit];
                Assert.DoesNotContain(held, c => at >= c.CharStart && at < c.CharEnd);
            }
            Assert.True(found, $"not in the book: {q}");
        }
    }

    private sealed class TempFolder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "TemporalGraph.Data.Tests", Guid.NewGuid().ToString("N"));
        public TempFolder() => Directory.CreateDirectory(Path);
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
    }
}
