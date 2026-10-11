using TemporalGraph.Cli.Indexing;
using TemporalGraph.Cli.Runs;

namespace TemporalGraph.Data.Tests;

/// <summary>
/// The extract-mentions and extract-facts skills were removed with their commands. The stage code they drove (Stage1, GraphStage3's
/// voted run) is still in the CLI to read earlier runs, and its tests still need a skill text: the two files as they last were, kept
/// here as fixtures.
/// </summary>
public static class OldSkills
{
    public static SkillFile Load(string name) =>
        SkillFile.Parse(name, File.ReadAllBytes(Path.Combine(RepoPaths.FindRoot(), "tests", "TemporalGraph.Data.Tests", "fixtures", "skills", name + ".md")));
}
