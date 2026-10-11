using TemporalGraph.Cli;
using TemporalGraph.Cli.Runs;

namespace TemporalGraph.Data.Tests;

public class SourceTextTests
{
    /// <summary>The source texts are read from data/books/source under the repo root; nothing is embedded in the CLI.</summary>
    [Fact]
    public void Cli_reads_every_source_text_from_disk_and_embeds_none()
    {
        var root = RepoPaths.FindRoot();
        Assert.DoesNotContain(typeof(CreateCommand).Assembly.GetManifestResourceNames(), n => n.Contains("books/source"));

        foreach (var id in new[] { "hound-of-the-baskervilles", "three-musketeers", "treasure-island" })
        {
            var text = System.Text.Encoding.UTF8.GetString(SourceTexts.Read(root, id));
            Assert.StartsWith("The Project Gutenberg eBook of", text);
            Assert.Contains("*** START OF THE PROJECT GUTENBERG EBOOK", text);
            Assert.Contains("*** END OF THE PROJECT GUTENBERG EBOOK", text);
            Assert.Equal(SourceTexts.Sha256(root, id), TemporalGraph.Cli.Indexing.Stage0.Sha256(SourceTexts.Read(root, id)));
        }
        Assert.Throws<FileNotFoundException>(() => SourceTexts.Read(root, "no-such-book"));
    }
}
