namespace TemporalGraph.Api;

/// <summary>
/// The committed query files, embedded in this assembly (see TemporalGraph.Api.csproj): the chat tools' queries
/// <c>queries/&lt;bookId&gt;/&lt;name&gt;.cypher</c> and the book's own samples
/// <c>queries/&lt;bookId&gt;/samples/&lt;NN&gt;-&lt;slug&gt;.cypher</c> (literal names from the book; <c>$pos</c> and <c>$spoilers</c> still gate them).
/// </summary>
public static class SampleQueries
{
    public const string Sample = "sample";
    public const string Tool = "tool";

    /// <summary>A book's queries for the Graph Query tab: its samples first (by file name, so by NN), then the tool queries (by file name).</summary>
    public static IReadOnlyList<SampleQuery> For(string bookId)
    {
        var prefix = $"queries/{bookId}/";
        var samples = $"{prefix}samples/";
        return BookResources.Assembly.GetManifestResourceNames()
            .Select(n => (Resource: n, Name: n.Replace('\\', '/')))
            .Where(x => x.Name.StartsWith(prefix, StringComparison.Ordinal) && x.Name.EndsWith(".cypher", StringComparison.Ordinal))
            .Select(x => (x.Resource, x.Name, Group: x.Name.StartsWith(samples, StringComparison.Ordinal) ? Sample : x.Name.IndexOf('/', prefix.Length) < 0 ? Tool : null))
            .Where(x => x.Group is not null)
            .OrderBy(x => x.Group == Sample ? 0 : 1).ThenBy(x => x.Name, StringComparer.Ordinal)
            .Select(x =>
            {
                using var stream = BookResources.Assembly.GetManifestResourceStream(x.Resource)!;
                using var reader = new StreamReader(stream);
                return Parse(System.IO.Path.GetFileNameWithoutExtension(x.Name), reader.ReadToEnd()) with { Group = x.Group! };
            })
            .ToList();
    }

    /// <summary>
    /// The chat tools' queries of a book: tool name → query text without its header, from the embedded files named in
    /// <see cref="TemporalGraph.Chat.ChatTools.Specs"/>; null when a file is missing.
    /// </summary>
    public static IReadOnlyDictionary<string, string>? ChatTools(string bookId)
    {
        // Resource names keep the folder separator of the build machine (queries/<bookId>\<file> on Windows), as in For.
        var resources = BookResources.Assembly.GetManifestResourceNames().ToDictionary(n => n.Replace('\\', '/'), n => n, StringComparer.Ordinal);
        var texts = new Dictionary<string, string>();
        foreach (var spec in TemporalGraph.Chat.ChatTools.Specs)
            foreach (var (file, key) in spec.Files.Select((f, i) => (f, i == 0 ? spec.Name : spec.Name + TemporalGraph.Chat.ChatToolSpec.SecondSuffix)))
            {
                if (!resources.TryGetValue($"queries/{bookId}/{file}", out var resource)) return null;
                using var stream = BookResources.Assembly.GetManifestResourceStream(resource)!;
                using var reader = new StreamReader(stream);
                texts[key] = TemporalGraph.Chat.ChatTools.StripHeader(reader.ReadToEnd());
            }
        // The extra tools, for a book whose folder has their files.
        foreach (var spec in TemporalGraph.Chat.ChatTools.ExtraSpecs)
        {
            if (!resources.TryGetValue($"queries/{bookId}/{spec.File}", out var resource)) continue;
            using var stream = BookResources.Assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            texts[spec.Name] = TemporalGraph.Chat.ChatTools.StripHeader(reader.ReadToEnd());
        }
        return texts;
    }

    /// <summary>
    /// <c>name</c> = the file name without extension; <c>question</c> = the first comment line without <c>//</c>;
    /// <c>text</c> = the query without its comment header (the leading <c>//</c> lines).
    /// </summary>
    public static SampleQuery Parse(string name, string file)
    {
        var lines = file.ReplaceLineEndings("\n").Split('\n');
        var header = lines.TakeWhile(l => l.TrimStart().StartsWith("//", StringComparison.Ordinal)).ToList();
        var question = header.Count > 0 ? header[0].TrimStart()[2..].Trim() : name;
        var text = string.Join("\n", lines.Skip(header.Count)).Trim();
        return new SampleQuery(name, question, text);
    }
}
