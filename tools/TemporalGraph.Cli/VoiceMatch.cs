using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using TemporalGraph.Query;

namespace TemporalGraph.Cli;

/// <summary>
/// The blind voice match in <c>chat vet</c>. Three characters from each book answer the same neutral question at one chapter;
/// names and places in the answers are masked; a judge model (<c>Indexing:Llm</c>) gets the nine answers shuffled and the nine names
/// and assigns each answer to a character. Accuracy: chance is 1/9 per answer, 1/3 for the right book.
/// </summary>
public static class VoiceMatch
{
    /// <param name="Book">The book id.</param>
    /// <param name="Chapter">The reader's chapter (spoilers off): every cast member of a book is known by then.</param>
    public sealed record CastMember(string Book, int Chapter, string Character);

    public static readonly CastMember[] Cast =
    [
        new("hound-of-the-baskervilles", 7, "Sherlock Holmes"),
        new("hound-of-the-baskervilles", 7, "Dr. Watson"),
        new("hound-of-the-baskervilles", 7, "Barrymore"),
        new("three-musketeers", 10, "d'Artagnan"),
        new("three-musketeers", 10, "Athos"),
        new("three-musketeers", 10, "Planchet"),
        new("treasure-island", 10, "Long John Silver"),
        new("treasure-island", 10, "Jim Hawkins"),
        new("treasure-island", 10, "Captain Smollett"),
    ];

    /// <summary>
    /// Opinions the characters can give from their own chapters. The first pair ("What do you think of the weather today?",
    /// "Tell me about yourself.") measured little: the grounding rules turned the first into "I have not heard of the weather", and the
    /// second was matched on content.
    /// </summary>
    public static readonly string[] Questions = ["What do you make of the last person you met?", "What worries you most right now?"];

    /// <summary>The judge's instructions. The answers follow, numbered.</summary>
    public const string JudgePrompt =
        """
        Below are {count} short answers. Each was written by a different one of these characters from classic novels, speaking in character:
        {names}
        Names of people and places in the answers have been replaced by [name] and [place]. Judge only by how each answer speaks: vocabulary, sentence rhythm, period, manner and tone.
        Each character wrote exactly one answer. Reply with one line per answer, in order, in the form "<answer number>: <character name>", and nothing else.
        """;

    /// <summary>
    /// The names a book's graph knows (every Character's and Location's canonical name and aliases, three letters or longer), each with its
    /// mask, longest first so "Sir Henry Baskerville" is masked before "Henry".
    /// </summary>
    public static IReadOnlyList<(string Name, string Mask)> Names(BookGraph graph, IEnumerable<string> extraPeople)
    {
        IEnumerable<string> Of(NodeValue n) =>
            new[] { n.Properties.GetValueOrDefault("name") as string, n.Properties.GetValueOrDefault("rosterName") as string, n.Properties.GetValueOrDefault("shortName") as string }
                .Concat(n.Properties.GetValueOrDefault("aliases") is IReadOnlyList<object?> l ? l.OfType<string>() : [])
                .OfType<string>();
        var people = graph.Nodes.Where(n => n.Labels.Contains("Character")).SelectMany(Of).Concat(extraPeople).Select(n => (n, "[name]"));
        var places = graph.Nodes.Where(n => n.Labels.Contains("Location")).SelectMany(Of).Select(n => (n, "[place]"));
        return people.Concat(places)
            .Select(p => (Name: p.Item1.Trim(), Mask: p.Item2))
            .Where(p => p.Name.Length >= 3)
            .DistinctBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(p => p.Name.Length)
            .ToList();
    }

    /// <summary>The answer with every known name masked (whole words, ignoring case; a possessive "'s" goes with the name).</summary>
    public static string Mask(string answer, IReadOnlyList<(string Name, string Mask)> names)
    {
        foreach (var (name, mask) in names)
            answer = Regex.Replace(answer, @"(?<![\p{L}\p{N}])" + Regex.Escape(name).Replace("'", "['’]") + @"(['’]s)?(?![\p{L}\p{N}])", mask,
                RegexOptions.IgnoreCase);
        return answer;
    }

    /// <summary>A fixed shuffle (the same order on every run, so runs compare): answer slot → cast index.</summary>
    public static int[] Order(int count, int seed)
    {
        var random = new Random(seed);   // a seeded Random gives the same sequence on every run
        return Enumerable.Range(0, count).Select(i => (i, key: random.Next())).OrderBy(x => x.key).Select(x => x.i).ToArray();
    }

    /// <summary>The judge's text in full (instructions, then the answers in <paramref name="order"/>).</summary>
    public static string JudgeText(IReadOnlyList<string> maskedAnswers, IReadOnlyList<string> names, IReadOnlyList<int> order) =>
        JudgePrompt.Replace("\r\n", "\n").Replace("{count}", maskedAnswers.Count.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Replace("{names}", string.Join("\n", names.Order(StringComparer.Ordinal).Select(n => "- " + n)))
        + "\n" + string.Join("\n", order.Select((cast, slot) => $"Answer {slot + 1}:\n\"\"\"\n{maskedAnswers[cast].Trim()}\n\"\"\"\n"));

    /// <summary>The judge's reply → answer slot (0-based) → the name it chose, matched to <paramref name="names"/>; unmatched slots are missing.</summary>
    public static IReadOnlyDictionary<int, string> ParseJudge(string reply, IReadOnlyList<string> names)
    {
        var picks = new Dictionary<int, string>();
        foreach (Match m in Regex.Matches(reply, @"^\s*(?:Answer\s*)?(\d+)\s*[:.)\-–]\s*(.+?)\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase))
        {
            var slot = int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) - 1;
            var said = m.Groups[2].Value.Trim().Trim('"', '*', '.', ' ');
            var name = names.FirstOrDefault(n => string.Equals(n, said, StringComparison.OrdinalIgnoreCase))
                ?? names.Where(n => said.Contains(n, StringComparison.OrdinalIgnoreCase) || n.Contains(said, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(n => n.Length).FirstOrDefault();
            if (name is not null && slot >= 0) picks.TryAdd(slot, name);
        }
        return picks;
    }

    /// <param name="Correct">Answers assigned to the character who wrote them.</param>
    /// <param name="SameBook">Answers assigned to a character of the right book.</param>
    /// <param name="Books">How many books the cast is from (three for the whole cast; fewer with <c>--book</c>).</param>
    public sealed record Score(int Correct, int SameBook, int Total, int Books = 3)
    {
        public string Detail => $"characters {Correct}/{Total} (chance 1/{Total}) · book {SameBook}/{Total} (chance 1/{Books})";
    }

    public static Score Grade(IReadOnlyList<CastMember> cast, IReadOnlyList<int> order, IReadOnlyDictionary<int, string> picks)
    {
        var (correct, sameBook) = (0, 0);
        for (var slot = 0; slot < order.Count; slot++)
        {
            if (!picks.TryGetValue(slot, out var picked)) continue;
            var truth = cast[order[slot]];
            if (picked == truth.Character) correct++;
            if (cast.FirstOrDefault(c => c.Character == picked)?.Book == truth.Book) sameBook++;
        }
        return new Score(correct, sameBook, order.Count, cast.Select(c => c.Book).Distinct().Count());
    }

    /// <summary>One judging call: the whole text as a single user message.</summary>
    public static async Task<string> JudgeAsync(IChatClient judge, string text, CancellationToken ct) =>
        (await judge.GetResponseAsync([new ChatMessage(ChatRole.User, text)], cancellationToken: ct)).Text;
}
