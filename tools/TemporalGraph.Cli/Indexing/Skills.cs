using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using TemporalGraph.Cli.Runs;

namespace TemporalGraph.Cli.Indexing;

/// <summary>
/// A skill file: <c>skills/&lt;name&gt;.md</c> at the repo root. First line <c>version: N</c>, then the sections
/// <c>## Instructions</c> (fixed; changed only by hand), <c>## Learned rules</c> (the improvement loop edits only
/// this) and <c>## Examples</c> (verbatim quotes from #2852, at most 3: chapter 4 or later for extract-mentions and resolve-entities, chapters 5–7 for
/// extract-facts and assign-story-time). The whole file is the prompt.
/// </summary>
public sealed partial record SkillFile(string Name, int Version, string Sha256, string Text, string Instructions, string LearnedRules, string Examples)
{
    public const string ExtractMentions = "extract-mentions";
    public const string ResolveEntities = "resolve-entities";
    public const string ExtractFacts = "extract-facts";

    /// <summary>The one extraction skill (mentions, events, facts, perceptions), in place of extract-mentions and extract-facts.</summary>
    public const string ExtractGraph = "extract-graph";

    /// <summary>Entity resolution asked a block of name groups at a time.</summary>
    public const string ResolveBlocks = "resolve-blocks";
    public const string AssignStoryTime = "assign-story-time";
    /// <summary>Stage 4's one anchor date.</summary>
    public const string PickAnchorDate = "pick-anchor-date";
    /// <summary>Stage 5's chapter pass.</summary>
    public const string RevisePerceptions = "revise-perceptions";

    [GeneratedRegex(@"\Aversion:\s*(\d+)\s*$", RegexOptions.Multiline)]
    private static partial Regex VersionLine();

    public static string PathFor(string root, string name) => Path.Combine(root, "skills", name + ".md");

    public static SkillFile Load(string root, string name)
    {
        var path = PathFor(root, name);
        var bytes = File.ReadAllBytes(path);
        return Parse(name, bytes);
    }

    public static SkillFile Parse(string name, byte[] bytes)
    {
        var text = Encoding.UTF8.GetString(bytes).Replace("\r\n", "\n");
        var m = VersionLine().Match(text);
        if (!m.Success) throw new InvalidDataException($"skills/{name}.md: first line must be 'version: N'.");
        return new SkillFile(
            name,
            int.Parse(m.Groups[1].Value),
            // Hash of the LF-normalized text, so a CRLF checkout (core.autocrlf) records the same hash.
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text))),
            text,
            Section(text, "Instructions", name),
            Section(text, "Learned rules", name),
            Section(text, "Examples", name));
    }

    private static string Section(string text, string heading, string name)
    {
        var start = text.IndexOf($"\n## {heading}\n", StringComparison.Ordinal);
        if (start < 0) throw new InvalidDataException($"skills/{name}.md: missing '## {heading}'.");
        start += heading.Length + 5;
        var next = text.IndexOf("\n## ", start, StringComparison.Ordinal);
        return (next < 0 ? text[start..] : text[start..next]).Trim();
    }

    /// <summary>What run.json and spans record.</summary>
    public object Record() => new { name = Name, version = Version, sha256 = Sha256 };
}
