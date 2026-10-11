using TemporalGraph.Chat;
using TemporalGraph.Cli.Indexing;
using TemporalGraph.Query;

namespace TemporalGraph.Data.Tests;

// The chat tools on a graph of the full schema, with The Hound's query files. The graph is the small run of
// FullSchemaPipelineTests: in chapter 1 Stapleton tells Watson he is a harmless naturalist; in chapter 2 the lie is exposed,
// Stapleton turns out to be Vandeleur (an identity reveal), and Vandeleur's school of 1884 is a referenced event.
public sealed class FullSchemaChatTests : IDisposable
{
    private static readonly string HoundQueries = Path.Combine(AppContext.BaseDirectory, "queries", "hound-of-the-baskervilles");
    private readonly FullSchemaPipelineTests.TempFolder _temp = new();
    private readonly BookGraph _graph;

    public FullSchemaChatTests() => _graph = BookGraph.Load(FullSchemaPipelineTests.BuildSmallRun(_temp.Path, out _));

    public void Dispose() => _temp.Dispose();

    private ChatTools Tools(int chapter, bool spoilers = false, List<ToolCallRecord>? log = null) =>
        new(_graph, ChatTools.LoadQueries(HoundQueries), chapter, spoilers, 80, log is null ? null : log.Add);

    private long Id(string name) => _graph.Nodes.Single(n => n.Properties.GetValueOrDefault("name") as string == name).Id;
    private long Fact(string stage3Id) => _graph.Nodes.Single(n => n.Properties.GetValueOrDefault("stage3Id") as string == stage3Id && n.Labels.Contains("Fact")).Id;

    [Fact]
    public void A_book_on_the_full_schema_has_the_three_extra_tools_and_the_others_do_not()
    {
        Assert.Equal(["facts_about", "who_met", "where_placed", "events_for", "relations", "characters_so_far", "most_discussed", "at_location", "search_facts",
            "facts_discovered_by", "perceptions_of", "referenced_events"], Tools(1).AsAITools().Select(t => t.Name));
        var earlier = new ChatTools(_graph, ChatTools.LoadQueries(Path.Combine(AppContext.BaseDirectory, "queries-earlier")), 1, false, 80);
        Assert.Equal(9, earlier.AsAITools().Count);
    }

    [Fact]
    public void Spoilers_off_shows_what_the_reader_has_been_told_and_a_retracted_fact_goes()
    {
        var one = Tools(1);
        Assert.Equal("Sherlock Holmes\nDr. Watson\nStapleton", one.Run("characters_so_far", null));
        Assert.Contains("Stapleton is a harmless naturalist.", one.Run("facts_about", "Stapleton"));
        Assert.StartsWith(ChatTools.Nothing, one.Run("facts_about", "Vandeleur"));
        Assert.Equal(ChatTools.Nothing, one.Run("referenced_events", null));

        var two = Tools(2);
        Assert.DoesNotContain("harmless naturalist", two.Run("facts_about", "Stapleton"));   // exposed in chapter 2
        Assert.Contains("mentioned", string.Join(",", ChatTools.ExtraSpecs.Select(s => s.Description)));
        Assert.Contains("Event E4. · 1884~ · Event E3.", two.Run("referenced_events", null));
    }

    [Fact]
    public void A_visible_identity_makes_the_two_names_one_character_and_a_hidden_one_keeps_them_apart()
    {
        // Before the reveal: nothing joins them, and the later name is a name not to say.
        var one = Tools(1);
        Assert.Equal([Id("Stapleton")], one.Union(Id("Stapleton")));
        Assert.Contains("Vandeleur", one.HiddenNames());
        Assert.DoesNotContain("Stapleton", one.HiddenNames());
        Assert.DoesNotContain("Yorkshire", one.Run("facts_about", "Stapleton"));

        // After it: one character. Asking about either name gives what is known under both, and both are shown by the name the reader met first.
        var two = Tools(2);
        Assert.Equal(new[] { Id("Stapleton"), Id("Vandeleur") }.Order(), two.Union(Id("Vandeleur")).Order());
        Assert.Empty(two.HiddenNames());
        Assert.Equal("Vandeleur kept a school in Yorkshire. · Stapleton · ch. 2", two.Run("facts_about", "Stapleton"));
        Assert.Equal(two.Run("facts_about", "Stapleton"), two.Run("facts_about", "Vandeleur"));
        Assert.Equal(("Stapleton", false), two.DisplayNameOf(Id("Vandeleur")));
        Assert.Equal(("Stapleton", false), two.DisplayNameOf(Id("Stapleton")));

        // Spoilers on: the identity holds in story time from the start, so the two are collapsed already at chapter 1.
        var on = Tools(1, spoilers: true);
        Assert.Equal(2, on.Union(Id("Stapleton")).Count);
        Assert.Contains("Vandeleur kept a school in Yorkshire.", on.Run("facts_about", "Stapleton"));
    }

    [Fact]
    public void The_character_gate_is_the_facts_a_character_perceives_except_those_it_disbelieves()
    {
        Assert.Equal(new[] { Fact("F1"), Fact("F2") }.Order(), Tools(1).KnownFacts(Id("Dr. Watson")).Order());
        Assert.Equal(new[] { Fact("F1"), Fact("F3") }.Order(), Tools(2).KnownFacts(Id("Dr. Watson")).Order());   // the lie is retracted
        Assert.Equal([Fact("F3")], Tools(2).KnownFacts(Id("Stapleton")));   // Vandeleur recalls his school: the union knows it; his own lie is gone
        Assert.Empty(Tools(1).KnownFacts(Id("Vandeleur")));                  // not met yet

        var two = Tools(2);
        Assert.Equal("Holmes is late in the mornings. · witnessed · Event E1. · ch. 1\nVandeleur kept a school in Yorkshire. · told · Event E3. · ch. 2", two.Run("facts_discovered_by", "Dr. Watson"));
        var perceptions = two.Run("perceptions_of", "Dr. Watson");
        Assert.Contains("suspects · Stapleton", perceptions);
        Assert.Contains("believes · Holmes is late in the mornings.", perceptions);
        Assert.DoesNotContain("harmless", perceptions);
        Assert.Equal("suspects · Stapleton ·  · 1889-10-02 · ch. 2", two.Run("perceptions_of", "Dr. Watson", "Stapleton"));
        Assert.Equal("Stapleton", Assert.Single(two.Calls, c => c.Args.ContainsKey("target")).Args["target"]);
    }

    [Fact]
    public void Leak_test_no_tool_row_carries_an_element_the_rule_hides_in_either_mode()
    {
        var nodes = _graph.Nodes.ToDictionary(n => n.Id);
        var edges = _graph.Relationships.ToDictionary(r => r.Id);
        string[] names = ["Dr. Watson", "Holmes", "Stapleton", "Vandeleur", "Baker Street", "naturalist school stick"];
        foreach (var chapter in new[] { 1, 2, 15 })
            foreach (var spoilers in new[] { false, true })
            {
                var log = new List<ToolCallRecord>();
                var tools = Tools(chapter, spoilers, log);
                foreach (var spec in ChatTools.AllSpecs)
                    foreach (var name in spec.Parameter == ToolParameter.None ? [null] : names)
                        tools.Run(spec.Name, name);
                tools.KnowledgePack("Dr. Watson", "Dr. Watson");
                var story = spoilers ? _graph.StoryAt(tools.Pos) : null;
                Assert.NotEmpty(log.SelectMany(c => c.NodeIdList));
                Assert.All(log.SelectMany(c => c.NodeIdList), id => Assert.True(SpoilerGate.Visible(nodes[id].Properties, tools.Pos, spoilers, story), $"node {id} at ch. {chapter}, spoilers {spoilers}"));
                Assert.All(log.SelectMany(c => c.EdgeIdList), id => Assert.True(SpoilerGate.Visible(edges[id].Properties, tools.Pos, spoilers, story), $"edge {id} at ch. {chapter}, spoilers {spoilers}"));
            }
    }
}
