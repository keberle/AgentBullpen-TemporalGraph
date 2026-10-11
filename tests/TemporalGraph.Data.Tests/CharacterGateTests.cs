using Microsoft.Extensions.Configuration;
using TemporalGraph.Chat;
using TemporalGraph.Cli;
using TemporalGraph.Cli.Runs;
using TemporalGraph.Query;

namespace TemporalGraph.Data.Tests;

// The character gate on everything the model is shown, on the promoted Hound graph. Chapter 7 is Watson's alone (the
// Stapletons of Merripit House); Holmes learns of it from Watson's first report (chapter 8).
public sealed class CharacterGateTests
{
    private const string Hound = "hound-of-the-baskervilles";
    private static readonly string Root = RepoPaths.FindRoot();
    private static readonly string Db = Path.Combine(RepoPaths.Books(Root), Hound + ".sqlite");
    private static readonly BookEntry Book = BookManifest.Read(Path.Combine(RepoPaths.Books(Root), BookManifest.FileName)).Single(b => b.Id == Hound);
    private static readonly BookGraph Graph = ChatCharacters.WithRosterNames(BookGraph.Load(Db), Book.Chat);
    private static readonly IReadOnlyList<ChatCharacter> Characters = ChatCharacters.Load(Db, Graph);

    private static ChatSetup Setup(string character, int chapter, bool spoilers = false, bool gate = true, BookGraph? graph = null, IReadOnlyList<ChatCharacter>? characters = null)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Chat:CharacterGate"] = gate ? "true" : "false" }).Build();
        characters ??= Characters;
        return ChatSetup.Create(Book, graph ?? Graph, characters, ChatCharacters.Resolve(characters, character)!, ChatTools.LoadQueries(Path.Combine(Root, "queries", Hound)),
            ChatCharacters.LoadLocations(Db), chapter, spoilers, config, voice: BookVoice.LoadFolder(Book, RepoPaths.Books(Root)));
    }

    /// <summary>The texts of the Facts first told in a chapter.</summary>
    private static List<string> FactsOf(int chapter)
    {
        var (start, end) = Graph.Chapters[chapter];
        return Graph.Nodes.Where(n => n.Labels.Contains("Fact") && n.Properties.GetValueOrDefault("revealedPos") is long p && p >= start && p < end)
            .Select(n => (string)n.Properties["text"]!).ToList();
    }

    /// <summary>Everything a setup can show the model: the pack and every tool, asked about the people of chapter 7.</summary>
    private static string Everything(ChatSetup setup)
    {
        var t = setup.Tools;
        var shown = new List<string?> { setup.PackText };
        foreach (var name in new[] { "Stapleton", "Miss Stapleton", "Dr. Watson", "Sir Henry", "Holmes", "Merripit House" })
        {
            shown.Add(t.Run("facts_about", name));
            shown.Add(t.Run("who_met", name));
            shown.Add(t.Run("events_for", name));
            shown.Add(t.Run("relations", name));
            shown.Add(t.Run("facts_discovered_by", name));
            shown.Add(t.Run("perceptions_of", name));
            shown.Add(t.Run("at_location", name));
        }
        foreach (var tool in new[] { "where_placed", "characters_so_far", "most_discussed", "referenced_events" }) shown.Add(t.Run(tool, null));
        foreach (var words in new[] { "moor", "Stapleton sister", "Merripit House", "butterfly naturalist", "warning" }) shown.Add(t.Run("search_facts", words));
        return string.Join("\n", shown);
    }

    [Fact]
    public void Holmes_at_chapter_7_is_shown_nothing_of_chapter_7()
    {
        var seven = FactsOf(7);
        Assert.True(seven.Count > 20);
        var holmes = Setup("Sherlock Holmes", 7);
        Assert.NotNull(holmes.Tools.Gate);
        var about = holmes.Tools.Run("facts_about", "Stapleton");
        var moor = holmes.Tools.Run("search_facts", "moor");
        Assert.DoesNotContain(seven, f => about.Contains(f));
        Assert.DoesNotContain(seven, f => moor.Contains(f));
        Assert.DoesNotContain(seven, f => holmes.PackText!.Contains(f));
        var shown = Everything(holmes);
        Assert.DoesNotContain(seven, f => shown.Contains(f));
        Assert.DoesNotContain("ch. 7", shown);
        Assert.DoesNotContain("Beryl", shown);
        // Holmes still has what he was told in Baker Street.
        Assert.NotEqual(ChatTools.Nothing, moor);
        Assert.Contains("ch. 2", holmes.PackText);
    }

    [Fact]
    public void Watson_at_chapter_7_is_shown_what_he_saw_at_Merripit_House()
    {
        var seven = FactsOf(7);
        var watson = Setup("Dr. Watson", 7);
        var about = watson.Tools.Run("facts_about", "Stapleton");
        var moor = watson.Tools.Run("search_facts", "moor");
        Assert.Contains(seven, f => about.Contains(f));
        Assert.Contains(seven, f => moor.Contains(f));
        Assert.Contains("ch. 7", watson.PackText);
        var shown = Everything(watson);
        Assert.Contains("Merripit House", shown);
        // Stapleton calls his sister "Beryl" in front of Watson in chapter 7 ("Halloa, Beryl!"): Watson has the name, and his own
        // chapter is not held back from him for it. Holmes, in London, has not heard it (the tests above).
        Assert.Contains("Beryl Stapleton tells Watson to go back to London at once and never return to the moor.", shown);
        Assert.DoesNotContain("Beryl", watson.NeverSay!);
        Assert.DoesNotContain(watson.NeverSay!, n => new[] { "Mrs. Barrymore", "Merripit House", "Grimpen Mire", "Stapleton", "Sir Henry" }.Contains(n));
        // The reader's gate alone shows Holmes the same chapter: that was the leak.
        Assert.Contains(seven, f => Setup("Sherlock Holmes", 7, gate: false).Tools.Run("facts_about", "Stapleton").Contains(f));
        Assert.Null(Setup("Sherlock Holmes", 7, gate: false).Tools.Gate);
    }

    [Fact]
    public void Holmes_at_chapter_9_has_chapter_7_as_the_report_tells_it()
    {
        // The report (chapter 8) is where Holmes learns of Merripit House: its facts are his, discovered "told". The Fact nodes of
        // chapter 7 itself stay Watson's (Holmes has no PERCEIVES edge to them), so what he has is the report's account.
        var holmes = Setup("Sherlock Holmes", 9);
        var report = FactsOf(8);
        var known = Graph.Nodes.Where(n => holmes.Tools.Gate!.Facts.Contains(n.Id)).Select(n => (string)n.Properties["text"]!).ToHashSet();
        Assert.True(report.Count(known.Contains) > 20);
        var about = holmes.Tools.Run("facts_about", "Stapleton");
        Assert.Contains("The Stapleton household consists of one maid, an old manservant, the sister and the brother.", about);
        Assert.Contains("ch. 8", about);
        Assert.Contains("Miss Stapleton is a very fascinating and beautiful woman.", holmes.Tools.Run("search_facts", "Stapleton sister beautiful"));
        var discovered = holmes.Tools.Run("facts_discovered_by", "Holmes");
        Assert.Contains("The Stapletons live miles from any help. · told", discovered);
        // None of it at chapter 7, and no later name at either chapter.
        var seven = Everything(Setup("Sherlock Holmes", 7));
        Assert.DoesNotContain(report, f => seven.Contains(f));
        Assert.DoesNotContain("Beryl", Everything(holmes));
    }

    [Fact]
    public void Spoilers_on_moves_the_clock_and_not_who_knows_what()
    {
        var seven = FactsOf(7);
        var holmes = Setup("Sherlock Holmes", 7, spoilers: true);
        Assert.NotNull(holmes.Tools.Gate);
        var shown = Everything(holmes);
        Assert.DoesNotContain(seven, f => shown.Contains(f));
        Assert.DoesNotContain("Beryl", shown);
    }

    [Fact]
    public void A_name_the_character_has_not_met_is_never_said()
    {
        var holmes = Setup("Sherlock Holmes", 7);
        Assert.Contains("Beryl", holmes.NeverSay!);
        Assert.Contains("Beryl Garcia", holmes.NeverSay!);
        // Names Holmes has heard in Baker Street stay free; so do ordinary words.
        Assert.DoesNotContain(holmes.NeverSay!, n => new[] { "Holmes", "Watson", "Dr. Watson", "Sir Henry", "Dr. Mortimer", "Stapleton", "Baskerville", "Barrymore", "House", "Hall", "Moor" }.Contains(n, StringComparer.OrdinalIgnoreCase));
        // Without the gate only the reader's rule hides names.
        var reader = Setup("Sherlock Holmes", 7, gate: false);
        Assert.True(reader.NeverSay!.Count < holmes.NeverSay!.Count);
        Assert.DoesNotContain("Beryl", reader.NeverSay!);
        // A title, a nationality or a plural of a free name is not a name to hide.
        Assert.DoesNotContain(holmes.NeverSay!, n => new[] { "Colonel", "Admiral", "Rear", "Mlle", "German", "Baskervilles" }.Contains(n));
        Assert.Contains("Upwood", holmes.NeverSay!);
    }

    [Fact]
    public void A_character_with_no_edges_knows_nothing_yet()
    {
        // A character node no edge touches, added to the promoted graph.
        var id = Graph.Nodes.Max(n => n.Id) + 1;
        var node = new NodeValue(id, ["Character"], new Dictionary<string, object?>
        {
            ["name"] = "Zebediah Quill", ["aliases"] = new List<object?> { "Zebediah Quill" }, ["revealedPos"] = 100L,
            ["aliasFirstPos"] = new Dictionary<string, object?> { ["Zebediah Quill"] = 100L },
        });
        var graph = new BookGraph(Graph.Nodes.Append(node), Graph.Relationships, Graph.Chapters.ToDictionary(c => c.Key, c => c.Value), Graph.Collisions, Graph.Timeline);
        var characters = Characters.Append(new ChatCharacter("Zebediah Quill", ["Zebediah Quill"], node)).ToList();
        var stranger = Setup("Zebediah Quill", 7, graph: graph, characters: characters);
        Assert.EndsWith(ChatTools.PackKnowsNothing, stranger.PackText);
        Assert.All(stranger.PackSections, s => Assert.Equal(0, s.Rows));
        Assert.Equal(ChatTools.Nothing, stranger.Tools.Run("search_facts", "moor hound"));
        Assert.Equal(ChatTools.Nothing, stranger.Tools.Run("where_placed", null));
        Assert.Equal("Zebediah Quill", stranger.Tools.Run("characters_so_far", null));
        Assert.StartsWith(ChatTools.Nothing + "\nNo character goes by \"Stapleton\". Known names: Zebediah Quill.", stranger.Tools.Run("facts_about", "Stapleton"));
        Assert.Contains(stranger.Tools.Calls, c => c.UnknownRowsSuppressed > 0);
    }
}
