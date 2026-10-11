using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using TemporalGraph.Chat;
using TemporalGraph.Cli;
using TemporalGraph.Cli.Indexing;
using TemporalGraph.Cli.Runs;
using TemporalGraph.Data;
using TemporalGraph.Query;

namespace TemporalGraph.Data.Tests;

// The voice layer — the committed voice files, the example lines and their gate, the prompt, and `index voice`'s pieces.
public sealed class VoiceTests
{
    private static readonly string Root = RepoPaths.FindRoot();
    private static readonly string Books = RepoPaths.Books(Root);
    private static readonly IReadOnlyList<BookEntry> Manifest = BookManifest.Read(Path.Combine(Books, BookManifest.FileName));
    public static readonly TheoryData<string> BookIds = new(Manifest.Select(b => b.Id));

    private static BookGraph Graph(BookEntry book) => ChatCharacters.WithRosterNames(BookGraph.Load(Path.Combine(Books, book.DatabaseFileName)), book.Chat);

    [Theory]
    [MemberData(nameof(BookIds))]
    public void Every_book_has_a_guide_and_a_card_per_roster_character_that_name_no_one_and_nothing(string bookId)
    {
        var book = Manifest.Single(b => b.Id == bookId);
        var voice = BookVoice.LoadFolder(book, Books)!;
        Assert.NotNull(voice.Guide);
        Assert.Equal(book.Chat!.Select(ChatCharacters.Slug).Order(), voice.Cards.Keys.Order());

        // Manner only: no card or guide names a character or place of the book (as the one-line voices before them).
        var graph = Graph(book);
        var names = graph.Nodes.Where(n => n.Labels.Contains("Character") || n.Labels.Contains("Location"))
            .SelectMany(n => ((IReadOnlyList<object?>?)n.Properties.GetValueOrDefault("aliases") ?? []).OfType<string>()
                .Append(n.Properties.GetValueOrDefault("name") as string ?? "").Append(n.Properties.GetValueOrDefault("rosterName") as string ?? ""))
            .Where(n => n.Length > 3 && char.IsUpper(n[0]) && VoiceLines.IsName(n)).Distinct().ToList();   // proper names: not "the parlour", not "Monsieur"
        foreach (var (file, body) in voice.Cards.Append(new("book.md", voice.Guide!)))
            Assert.All(names, name => Assert.False(Regex.IsMatch(body, @"(?<![\p{L}])" + Regex.Escape(name) + @"(?![\p{L}])"), $"{bookId}/{file} names '{name}'"));

        // Sizes the prompt can afford (under 700 added tokens a call on Haiku): the guide ≤ 110 words, a card ≤ 95.
        Assert.InRange(VoiceLines.WordCount(voice.Guide!), 60, 110);
        Assert.All(voice.Cards, c => Assert.InRange(VoiceLines.WordCount(c.Value), 50, 95));
    }

    [Theory]
    [MemberData(nameof(BookIds))]
    public void Every_example_line_is_the_characters_words_where_the_text_has_them(string bookId)
    {
        var book = Manifest.Single(b => b.Id == bookId);
        var voice = BookVoice.LoadFolder(book, Books)!;
        Assert.NotEmpty(voice.Lines);
        var text = IndexingShared.BookText(bookId);
        var stage0 = IndexingShared.Stage0For(bookId);
        foreach (var line in voice.Lines)
        {
            Assert.Contains(line.Character, book.Chat!);
            Assert.Equal(line.Chapter, IndexingShared.ChapterOf(stage0)((int)line.Pos));
            Assert.InRange(VoiceLines.WordCount(line.Text), VoiceLines.MinWords, VoiceLines.MaxWords);
            // Word for word: each part, whitespace collapsed, is in the text at or after the line's position.
            var near = VoiceLines.Collapse(text[(int)line.Pos..Math.Min(text.Length, (int)line.Pos + 4000)]);
            foreach (var part in line.Text.Split(" … ")) Assert.Contains(part, near);
        }
    }

    [Fact]
    public void Silver_at_chapter_8_gets_no_line_from_after_it_and_none_naming_the_unmet()
    {
        var book = Manifest.Single(b => b.Id == "treasure-island");
        var graph = Graph(book);
        var voice = BookVoice.LoadFolder(book, Books)!;
        var pos = graph.Chapters[8].CharEnd;
        var lines = voice.Examples("Long John Silver", 8, pos, spoilers: false, count: 5, graph);
        Assert.NotEmpty(lines);
        Assert.All(lines, l => Assert.True(l.Chapter <= 8 && l.Chapter < 11));
        var unmet = BookVoice.UnrevealedNames(graph, pos);
        Assert.All(lines, l => Assert.DoesNotContain(unmet, n => Regex.IsMatch(l.Text, @"\b" + Regex.Escape(n) + @"\b", RegexOptions.IgnoreCase)));
        // Spoilers on: any chapter.
        Assert.Contains(voice.Examples("Long John Silver", 8, pos, spoilers: true, count: 40, graph), l => l.Chapter > 8);
    }

    [Fact]
    public void Examples_are_recent_one_per_chapter_first_and_none_naming_someone_not_yet_met()
    {
        var graph = new BookGraph(
            [new NodeValue(1, ["Character"], new Dictionary<string, object?> { ["name"] = "Flint", ["aliases"] = new List<object?> { "Flint" },
                ["aliasFirstPos"] = new Dictionary<string, object?> { ["Flint"] = 5000.0 } })],
            [], new Dictionary<int, (int, int)> { [1] = (0, 1000), [2] = (1000, 2000), [3] = (2000, 3000) }, []);
        var voice = new BookVoice(null, new Dictionary<string, string>(),
        [
            new("Silver", 1, 100, "one"), new("Silver", 1, 200, "two"), new("Silver", 2, 1100, "three"), new("Silver", 3, 2100, "four, Flint"),
            new("Silver", 3, 2200, "five"), new("Jim", 2, 1200, "not mine"),
        ]);
        Assert.Equal(["two", "three", "five"], voice.Examples("Silver", 3, 3000, false, 3, graph).Select(l => l.Text));   // no "four, Flint"
        Assert.Equal(["one", "two", "three", "five"], voice.Examples("Silver", 3, 3000, false, 9, graph).Select(l => l.Text));
        Assert.Equal(["one", "two"], voice.Examples("Silver", 1, 1000, false, 9, graph).Select(l => l.Text));
        Assert.Empty(voice.Examples("Silver", 3, 3000, false, 0, graph));
        Assert.Contains("four, Flint", voice.Examples("Silver", 1, 1000, true, 9, graph).Select(l => l.Text));
    }

    [Fact]
    public void The_prompt_puts_the_voice_layer_after_the_rules_and_the_length_rule_last()
    {
        var book = new BookEntry("b", "A Book", "An Author");
        var layer = new VoicePrompt("Long sentences.", "You speak warmly.", ["Ahoy there, shipmate.", "By the powers!"]);
        var prompt = ChatPrompt.Fill("Silver", book, 8, "x", knowledgePack: true, voice: "Warm (the old one-liner).", layer: layer);

        var guide = prompt.IndexOf("How people speak in this book: Long sentences.", StringComparison.Ordinal);
        var card = prompt.IndexOf("Your way of speaking: You speak warmly.\nKeep this manner in every answer.", StringComparison.Ordinal);
        var lines = prompt.IndexOf(ChatPrompt.LinesHeading + "\n\"Ahoy there, shipmate.\"\n\"By the powers!\"", StringComparison.Ordinal);
        Assert.True(prompt.IndexOf("When a row names you", StringComparison.Ordinal) < guide && guide < card && card < lines);
        Assert.EndsWith("\n\n" + ChatPrompt.LengthRule, prompt);
        Assert.DoesNotContain("the old one-liner", prompt);   // a card replaces the voices line

        // Without a card the voices line is the last paragraph before the length rule; without anything, only the length rule follows the rules.
        Assert.EndsWith("\n\nManner of speaking: Warm. Keep this manner in every answer.\n\n" + ChatPrompt.LengthRule,
            ChatPrompt.Fill("Silver", book, 8, "x", voice: "Warm.", layer: new VoicePrompt("G", null, [])));
        Assert.Single(Regex.Matches(ChatPrompt.Fill("Silver", book, 8, "x"), Regex.Escape(ChatPrompt.LengthRule)));
    }

    [Fact]
    public void The_voice_files_lose_their_comment_and_title_and_load_by_roster_slug()
    {
        Assert.Equal("Body line.\n\nSecond.", BookVoice.Body("<!-- Sources: ch. 1 -->\n# Title\n\nBody line.\n\nSecond.\n"));
        var book = new BookEntry("b", "T", "A", ["Dr. Watson", "Sherlock Holmes"]);
        var files = new Dictionary<string, string>
        {
            ["book.md"] = "<!-- x -->\n# Voice guide\n\nGuide.", ["dr-watson.md"] = "# Dr. Watson\n\nCard.",
            ["lines.json"] = """[{ "character": "Dr. Watson", "chapter": 2, "pos": 77, "text": "A line." }]""",
        };
        var voice = BookVoice.Load(book, f => files.GetValueOrDefault(f))!;
        Assert.Equal("Guide.", voice.Guide);
        Assert.Equal(["dr-watson"], voice.Cards.Keys);
        Assert.Equal(new VoiceLine("Dr. Watson", 2, 77, "A line."), voice.Lines.Single());
        Assert.Null(BookVoice.Load(book, _ => null));
    }

    [Fact]
    public void The_chat_command_sends_the_voice_layer_with_gated_lines()
    {
        var book = Manifest.Single(b => b.Id == "treasure-island");
        var graph = Graph(book);
        var characters = ChatCharacters.Load(Path.Combine(Books, book.DatabaseFileName), graph);
        var silver = BookVoice.LoadFolder(book, Books)!;
        var character = characters.First(c => c.Node?.Properties.GetValueOrDefault("rosterName") as string == "Long John Silver").Name;
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Chat:VoiceExamples"] = "3" }).Build();

        var setup = ChatSetup.Create(book, graph, characters, character, ChatTools.LoadQueries(Path.Combine(Root, "queries", book.Id)),
            ChatCharacters.LoadLocations(Path.Combine(Books, book.DatabaseFileName)), 8, false, config, voice: silver);
        Assert.InRange(setup.VoiceLines!.Count, 1, 3);   // he first speaks in ch. 8
        Assert.All(setup.VoiceLines, l => Assert.Contains($"\"{l.Text}\"", setup.SystemPrompt));
        Assert.Contains("How people speak in this book:", setup.SystemPrompt);
        Assert.Contains("Your way of speaking:", setup.SystemPrompt);
        Assert.EndsWith(ChatPrompt.LengthRule, setup.SystemPrompt);

        var none = ChatSetup.Create(book, graph, characters, character, ChatTools.LoadQueries(Path.Combine(Root, "queries", book.Id)),
            ChatCharacters.LoadLocations(Path.Combine(Books, book.DatabaseFileName)), 8, false,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Chat:VoiceExamples"] = "0" }).Build(), voice: silver);
        Assert.Empty(none.VoiceLines!);
        Assert.DoesNotContain(ChatPrompt.LinesHeading, none.SystemPrompt);
    }

    // ── index voice ────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Utterances_join_a_paragraphs_quotes_and_follow_a_speech_across_paragraphs()
    {
        var stage0 = IndexingShared.Stage0For("treasure-island");
        var text = IndexingShared.BookText("treasure-island");
        var utterances = VoiceLines.Utterances(text, stage0);
        var first = utterances.First(u => u.Text.StartsWith("I don’t care two coppers who he is", StringComparison.Ordinal));
        Assert.Equal("I don’t care two coppers who he is, … But he hasn’t paid his score. Harry, run and catch him.", first.Text);
        Assert.Contains("cried Silver", first.Narration);
        Assert.Equal(8, first.Chapter);
        Assert.Equal(text.IndexOf("I don’t care two coppers", StringComparison.Ordinal), first.Pos);
        Assert.Contains(utterances, u => u.Continues);
    }

    [Fact]
    public void A_speech_tag_naming_one_character_attributes_the_paragraph()
    {
        var book = Manifest.Single(b => b.Id == "treasure-island");
        var cast = new VoiceLines.Cast(Graph(book), book.Chat!);
        Assert.Equal(["Long John Silver"], cast.Tagged("… cried Silver. …"));
        Assert.Equal(["Captain Smollett"], cast.Tagged("… the others said nothing, but Captain Smollett said, … "));
        // The promoted graph has "the captain" (Billy Bones at the inn) as a character of its own, outside the roster: "".
        Assert.Equal(["", "Captain Smollett"], cast.Tagged("… the captain said nothing, but Captain Smollett said, … "));
        Assert.Empty(cast.Tagged("… said he. …"));
        Assert.Equal("Long John Silver", cast.RosterOf("Silver"));
        Assert.Null(cast.RosterOf("Nobody At All"));
    }

    [Fact]
    public void The_models_speaker_counts_only_with_its_evidence_near_the_paragraph()
    {
        var book = Manifest.Single(b => b.Id == "treasure-island");
        var cast = new VoiceLines.Cast(Graph(book), book.Chat!);
        var text = IndexingShared.BookText("treasure-island");
        var u = VoiceLines.Utterances(text, IndexingShared.Stage0For("treasure-island")).First(x => x.Text.StartsWith("I don’t care two coppers", StringComparison.Ordinal));

        Assert.Equal(("Long John Silver", "model"), Pick(VoiceLines.Check(u, new(1, "Silver", "cried Silver"), text, cast, 0.9, null)));
        Assert.Equal((null, "rejected"), Pick(VoiceLines.Check(u, new(1, "Silver", "said the man from the moon"), text, cast, 0.9, null)));
        Assert.Equal((null, "unknown"), Pick(VoiceLines.Check(u, new(1, "unknown", ""), text, cast, 0.9, null)));
        Assert.Equal(("Jim Hawkins", "model"), Pick(VoiceLines.Check(u, new(1, "narrator", "cried Silver"), text, cast, 0.9, "Jim Hawkins")));
        static (string?, string) Pick(VoiceLines.Attributed a) => (a.Speaker, a.Method);
    }

    [Fact]
    public void Selection_prefers_habits_and_manner_over_names_numbers_and_plot()
    {
        var habits = new HashSet<string> { "matey", "powers" };
        var names = new HashSet<string> { "flint", "bristol" };
        var manner = VoiceLines.Score("Ah, matey, by the powers, you're a lad after my own heart, you are!", habits, names);
        var plot = VoiceLines.Score("Flint buried the treasure at Bristol with two hundred pounds and a map.", habits, names);
        Assert.True(manner > plot, $"{manner} vs {plot}");
        Assert.Equal(0, VoiceLines.Names("Flint, come here at once and sit.", names));    // a form of address
        Assert.Equal(1, VoiceLines.Names("I sailed with Flint for many a year.", names));
    }

    [Fact]
    public void The_review_file_round_trips_its_marks()
    {
        var candidates = new Dictionary<string, List<VoiceLines.Candidate>>
        {
            ["Jim Hawkins"] = [new(new VoiceLine("Jim Hawkins", 3, 900, "Well, sir, I said I would and I did, for all that."), 1.5),
                new(new VoiceLine("Jim Hawkins", 4, 1200, "Second line here, which nobody will keep, I think."), 0.5)],
        };
        var review = VoiceLines.Review("Treasure Island", "treasure-island", "run", ["Jim Hawkins"], candidates, new Dictionary<string, int> { ["Jim Hawkins"] = 2 });
        Assert.Equal(candidates["Jim Hawkins"].Select(c => c.Line), VoiceLines.ParseReview(review));   // both pre-selected
        var marked = review.Replace("- [x] ch4", "- [-] ch4");
        Assert.Equal([candidates["Jim Hawkins"][0].Line], VoiceLines.ParseReview(marked));
        Assert.Throws<InvalidDataException>(() => VoiceLines.ParseReview(review.Replace("- [x] ch3 · @900", "- [x] chapter three")));
    }

    // ── never-say ────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_never_say_list_is_the_guides_checkable_words()
    {
        var words = BookVoice.NeverSayList("""
            Address follows rank.

            Never say: okay, awesome, process (feelings), share (confide), gun (say pistol or musket), "process" for feelings; nor stage-pirate cliches the book never uses: arr, ahoy me hearties, savvy.
            """);
        Assert.Equal(["okay", "awesome", "gun", "arr", "ahoy me hearties", "savvy"], words);
        Assert.Empty(BookVoice.NeverSayList("No list here."));
        Assert.Empty(BookVoice.NeverSayList(null));

        Assert.Equal(["okay", "ahoy me hearties"], BookVoice.NeverSayHits("Okay, I said; then AHOY me hearties! But sparrow and arrow are fine.", ["okay", "ahoy me hearties", "arr"]));

        // Every book's guide has a list, and the words a model is likeliest to slip into are on it.
        foreach (var book in Manifest)
        {
            var list = BookVoice.LoadFolder(book, Books)!.NeverSay;
            Assert.Contains("okay", list);
            Assert.Contains("awesome", list);
            Assert.DoesNotContain(list, w => w.Contains('(') || w.Contains(':'));
        }
        Assert.Contains("savvy", BookVoice.LoadFolder(Manifest.Single(b => b.Id == "treasure-island"), Books)!.NeverSay);
        Assert.Contains("gun", BookVoice.LoadFolder(Manifest.Single(b => b.Id == "three-musketeers"), Books)!.NeverSay);
    }

    [Fact]
    public async Task An_answer_with_a_never_say_word_is_asked_once_more_with_a_reminder()
    {
        var root = Path.Combine(Path.GetTempPath(), "TemporalGraph.Data.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            const string hound = "hound-of-the-baskervilles";
            var books = Directory.CreateDirectory(Path.Combine(root, "data", "books")).FullName;
            BookManifest.Write(Path.Combine(books, BookManifest.FileName), [new BookEntry(hound, "The Hound of the Baskervilles", "Arthur Conan Doyle", ["Barrymore"])]);
            File.Copy(Path.Combine(Books, hound + ".sqlite"), Path.Combine(books, hound + ".sqlite"));
            var queries = Directory.CreateDirectory(Path.Combine(root, "queries", hound)).FullName;
            foreach (var f in Directory.GetFiles(Path.Combine(Root, "queries", hound), "*.cypher")) File.Copy(f, Path.Combine(queries, Path.GetFileName(f)));
            var voice = Directory.CreateDirectory(Path.Combine(books, hound, BookVoice.Folder)).FullName;
            File.WriteAllText(Path.Combine(voice, BookVoice.GuideFile), "# Voice guide\n\nFormal.\n\nNever say: okay, awesome.\n");

            using var server = new FakeLlmServer();
            server.OpenAIMessages.Enqueue(FakeLlmServer.Text("Okay, sir, that is all I know."));
            server.OpenAIMessages.Enqueue(FakeLlmServer.Text("Very good, sir; that is all I know."));
            server.OpenAIMessages.Enqueue(FakeLlmServer.Text("Awesome, sir."));
            server.OpenAIMessages.Enqueue(FakeLlmServer.Text("Awesome again, sir."));
            var config = new ConfigurationBuilder()
                .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "templates", "cli.appsettings.json"))
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Llm:Type"] = "OpenAI", ["Llm:Model"] = "my-deployment", ["Llm:Endpoint"] = server.BaseUrl + "/openai/v1/",
                    ["Llm:ApiKey"] = "fake-key-for-tests-0123456789", ["Chat:RequireToolFirst"] = "false",
                })
                .Build();
            var script = Path.Combine(root, "questions.txt");
            File.WriteAllText(script, "Who are you?\nAnd now?\n");
            var output = new StringWriter();
            var settings = new ChatCommand.Settings { Book = hound, Chapter = 7, Character = "Barrymore", Script = script, Json = true };

            Assert.Equal(0, await ChatCommand.RunAsync(settings, config, root, output, TestContext.Current.CancellationToken));

            Assert.Equal(4, server.Requests.Count);   // two turns, each asked twice
            Assert.Contains("Say that again without \\\"okay\\\": no one in your book speaks that way.", server.Requests[1].Body);
            var turns = File.ReadAllLines(Path.Combine(RepoPaths.Runs(root), Directory.GetDirectories(RepoPaths.Runs(root)).Select(Path.GetFileName).Single()!, ChatJsonl.FileName))
                .Select(l => JsonNode.Parse(l)!).Where(j => j["type"]!.GetValue<string>() == "turn").ToList();
            Assert.Equal("Very good, sir; that is all I know.", turns[0]["answer"]!.GetValue<string>());
            Assert.Equal(["okay"], turns[0]["neverSaid"]!.AsArray().Select(w => w!.GetValue<string>()));
            Assert.Empty(turns[0]["neverSayRemaining"]!.AsArray());
            Assert.Equal(["awesome"], turns[1]["neverSayRemaining"]!.AsArray().Select(w => w!.GetValue<string>()));   // once only: kept
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void The_voice_layer_drops_its_longest_lines_to_fit_the_budget()
    {
        var layer = new VoicePrompt(new string('g', 100), new string('c', 100), ["short one", new string('x', 300), "medium line here", new string('y', 200)]);
        var full = ChatPrompt.VoiceLength(layer, null);
        Assert.Equal(layer.Lines, ChatPrompt.Fit(layer, null, full).Lines);
        Assert.Equal(["short one", "medium line here", new string('y', 200)], ChatPrompt.Fit(layer, null, full - 250).Lines);
        Assert.Equal(["short one", "medium line here"], ChatPrompt.Fit(layer, null, full - 450).Lines);
        Assert.Empty(ChatPrompt.Fit(layer, null, 10).Lines);
    }

    [Theory]
    [MemberData(nameof(BookIds))]
    public void Every_character_at_every_chapter_stays_within_the_voice_budget(string bookId)
    {
        var book = Manifest.Single(b => b.Id == bookId);
        var graph = Graph(book);
        var characters = ChatCharacters.Load(Path.Combine(Books, book.DatabaseFileName), graph);
        var voice = BookVoice.LoadFolder(book, Books)!;
        foreach (var roster in book.Chat!)
        {
            var character = characters.FirstOrDefault(c => c.Node?.Properties.GetValueOrDefault("rosterName") as string == roster)?.Name ?? roster;
            var oneLine = ChatCharacters.Voice(book, characters, character);
            foreach (var ch in graph.Chapters.Keys)
            {
                var lines = voice.Examples(roster, ch, graph.Chapters[ch].CharEnd, false, ChatSettings.DefaultVoiceExamples, graph).Select(l => l.Text).ToList();
                var fitted = ChatPrompt.Fit(new VoicePrompt(voice.Guide, voice.Card(book, characters, character), lines), oneLine);
                Assert.True(ChatPrompt.VoiceLength(fitted, oneLine) <= ChatPrompt.MaxVoiceChars, $"{roster} ch. {ch}");
                Assert.True(fitted.Lines.Count >= Math.Min(2, lines.Count), $"{roster} ch. {ch}: {fitted.Lines.Count} of {lines.Count} lines kept");
            }
        }
    }
}
