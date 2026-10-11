using TemporalGraph.Cli;
using TemporalGraph.Cli.Indexing;
using TemporalGraph.Cli.Runs;

namespace TemporalGraph.Data.Tests;

// A cross-check compares each source only over the chapters the run covered (LitBank covers ch. 1 only).
public class CrosscheckScopeTests
{
    private const string Hound = "hound-of-the-baskervilles";
    private static readonly Stage0Result Stage0 = IndexingShared.Stage0For(Hound);
    private static readonly Stage2Result NoEntities = new(Hound, [], [], [], [], [], new ResolveSettings());

    [Fact]
    public void A_run_outside_litbanks_chapter_gets_no_litbank_items()
    {
        var results = IndexCrosscheckCommand.Compare(RepoPaths.FindRoot(), NoEntities, (8, 8));

        var litBank = results.Single(r => r.Source == "LitBank");
        Assert.False(litBank.Available);
        Assert.Empty(litBank.Disagreements);
        Assert.Contains("outside the run's chapters", litBank.Note);
        // Every remaining item (BookNLP) is inside ch. 8.
        var ch8 = Stage0.Chapters.Single(c => c.Number == 8);
        Assert.All(results.SelectMany(r => r.Disagreements), d =>
        {
            Assert.Equal(8, d.Chapter);
            Assert.InRange(d.CharStart, ch8.CharStart, ch8.CharEnd - 1);
        });
    }

    [Fact]
    public void A_run_covering_chapter_1_is_compared_with_litbank_inside_its_chapters()
    {
        var results = IndexCrosscheckCommand.Compare(RepoPaths.FindRoot(), NoEntities, (1, 3));

        var litBank = results.Single(r => r.Source == "LitBank");
        Assert.True(litBank.Available);
        Assert.NotEmpty(litBank.Disagreements); // no entities of ours: every LitBank name is "not in ours"
        Assert.All(litBank.Disagreements, d => Assert.Equal(1, d.Chapter));
        Assert.All(results.SelectMany(r => r.Disagreements), d => Assert.InRange(d.Chapter, 1, 3));
    }

    [Fact]
    public void A_run_on_chapter_2_only_gets_no_litbank_items()
    {
        var litBank = IndexCrosscheckCommand.Compare(RepoPaths.FindRoot(), NoEntities, (2, 2)).Single(r => r.Source == "LitBank");
        Assert.False(litBank.Available);
        Assert.Empty(litBank.Disagreements);
    }
}
