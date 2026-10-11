using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using TemporalGraph.Chat;
using TemporalGraph.Query;

namespace TemporalGraph.Cli.Indexing;

/// <summary>
/// <c>index voice</c>: the roster characters' own lines, mined from the text.
/// <list type="number">
/// <item><b>Utterances:</b> every paragraph's quoted speech (“…”). A quote still open at the end of a paragraph continues into the next
/// paragraph that opens with “ (the novels' convention for a long speech): the same speaker.</item>
/// <item><b>Speakers, by rule:</b> a speech tag in the paragraph's narration naming one character ("cried Silver", "Holmes said").</item>
/// <item><b>Speakers, by the model</b> (<c>Indexing:Llm</c>): the rest ("said he", alternating lines without tags), one call per chapter,
/// each answer with an evidence phrase that must be found near the paragraph (<see cref="EvidenceChecker"/>, the stages' threshold).</item>
/// <item><b>Selection</b> (no model): 8–40 words, scored for the character's own habits (words they use far more than everyone else),
/// exclamations and rhythm, and against names, places, numbers and plot words: the lines should teach how they speak, not what happened.</item>
/// </list>
/// </summary>
public static partial class VoiceLines
{
    /// <summary>One paragraph's quoted speech.</summary>
    /// <param name="Pos">The offset of the first quoted word.</param>
    /// <param name="Text">The quoted parts, whitespace collapsed, joined by " … " where narration was cut out.</param>
    /// <param name="Narration">The paragraph's text outside the quotes (where speech tags are).</param>
    /// <param name="Continues">The paragraph continues the previous paragraph's open quote.</param>
    public sealed record Utterance(int Chapter, int ParaStart, int ParaEnd, int Pos, string Text, string Narration, bool Continues);

    /// <param name="Speaker">The roster name, or null (not a roster character, or unknown).</param>
    /// <param name="Method"><c>tag</c>, <c>continued</c>, <c>model</c>, or why there is no speaker: <c>other</c> (someone not on the
    /// roster), <c>unknown</c>, <c>rejected</c> (the model's evidence was not found near the paragraph).</param>
    public sealed record Attributed(Utterance Utterance, string? Speaker, string Method, string? Evidence = null);

    public const int MinWords = 8, MaxWords = 40, PreSelected = 25, Listed = 40, MaxPerChapter = 4;

    // ── utterances ─────────────────────────────────────────────────────────────────────────────────────────────────────────────

    public static List<Utterance> Utterances(string text, Stage0Result stage0)
    {
        var result = new List<Utterance>();
        foreach (var chapter in stage0.Chapters)
        {
            var open = false;   // the previous paragraph ended inside a quote
            foreach (var p in chapter.Paragraphs)
            {
                var para = text[p.CharStart..p.CharEnd];
                var parts = new List<(int Start, int End)>();
                var continues = open && para.TrimStart().StartsWith('“');
                open = false;
                for (var i = 0; i < para.Length; i++)
                {
                    if (para[i] != '“') continue;
                    var close = para.IndexOf('”', i + 1);
                    if (close < 0)
                    {
                        parts.Add((i + 1, para.Length));
                        open = true;
                        break;
                    }
                    parts.Add((i + 1, close));
                    i = close;
                }
                if (parts.Count == 0) continue;
                var quoted = string.Join(" … ", parts.Select(q => Collapse(para[q.Start..q.End])).Where(s => s.Length > 0));
                if (quoted.Length == 0) continue;
                var narration = new StringBuilder();
                var at = 0;
                foreach (var q in parts)
                {
                    narration.Append(para[at..Math.Max(at, q.Start - 1)]).Append(" … ");
                    at = Math.Min(para.Length, q.End + 1);
                }
                narration.Append(para[Math.Min(at, para.Length)..]);
                result.Add(new Utterance(chapter.Number, p.CharStart, p.CharEnd, p.CharStart + parts[0].Start, quoted, Collapse(narration.ToString()), continues));
            }
        }
        return result;
    }

    public static string Collapse(string s) => WhitespaceRun().Replace(s, " ").Trim();

    // ── speakers ───────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The book's people: every Character node's names (canonical, aliases, roster spelling), each mapped to its roster name or to
    /// <c>""</c> for a character who is not on the roster.</summary>
    public sealed class Cast
    {
        private readonly List<(string Name, string Roster)> _names;
        private readonly Regex _tag;

        public Cast(BookGraph graph, IReadOnlyList<string> roster)
        {
            _names = [];
            foreach (var n in graph.Nodes.Where(n => n.Labels.Contains("Character")))
            {
                var rosterName = n.Properties.GetValueOrDefault("rosterName") as string ?? "";
                var names = new[] { n.Properties.GetValueOrDefault("name") as string, n.Properties.GetValueOrDefault("rosterName") as string }
                    .Concat(n.Properties.GetValueOrDefault("aliases") is IReadOnlyList<object?> l ? l.OfType<string>() : []).OfType<string>();
                foreach (var name in names.Select(Norm).Where(x => x.Length >= 3).Distinct()) _names.Add((name, rosterName));
            }
            foreach (var r in roster) _names.Add((Norm(r), r));
            _names = _names.OrderByDescending(x => x.Name.Length).ToList();
            var alternation = string.Join("|", _names.Select(x => Regex.Escape(x.Name)).Distinct());
            const string verbs = "said|says|cried|asked|answered|replied|exclaimed|returned|continued|observed|remarked|whispered|shouted|added|repeated|"
                + "growled|roared|muttered|interrupted|resumed|went on|called|began|murmured|sighed|laughed|cried out|broke in|rejoined|retorted|responded|"
                + "inquired|demanded|declared|protested|pursued|thundered|stammered|groaned|echoed|insisted|urged|explained";
            _tag = new Regex($@"(?:\b(?:{verbs})\s+(?<a>{alternation})(?![\p{{L}}])|(?<![\p{{L}}])(?<b>{alternation})\s*,?\s+(?:{verbs})\b)", RegexOptions.IgnoreCase);
        }

        public static string Norm(string s) => s.Trim().Replace('’', '\'').Replace('‘', '\'');

        /// <summary>The roster name for a name as the text or the model gives it: "" for a known non-roster character, null when unknown.</summary>
        public string? RosterOf(string name)
        {
            var n = Norm(name).Trim('"', '.', ' ');
            foreach (var x in _names) if (string.Equals(x.Name, n, StringComparison.OrdinalIgnoreCase)) return x.Roster;
            return null;
        }

        /// <summary>The roster names (or "" for others) of the people named in speech tags in <paramref name="narration"/>.</summary>
        public IReadOnlyList<string> Tagged(string narration) =>
            _tag.Matches(Norm(narration)).Select(m => RosterOf(m.Groups["a"].Success ? m.Groups["a"].Value : m.Groups["b"].Value)).OfType<string>().Distinct().ToList();
    }

    /// <summary>Speakers by rule: one person tagged → them; a continuation → the previous speaker; otherwise left for the model (null).</summary>
    public static List<Attributed?> ByRule(IReadOnlyList<Utterance> utterances, Cast cast)
    {
        var result = new List<Attributed?>();
        Attributed? previous = null;
        foreach (var u in utterances)
        {
            Attributed? a = null;
            if (u.Continues && previous is not null) a = new Attributed(u, previous.Speaker, previous.Speaker is null ? previous.Method : "continued");
            else if (cast.Tagged(u.Narration) is [var one]) a = new Attributed(u, one.Length == 0 ? null : one, one.Length == 0 ? "other" : "tag");
            result.Add(a);
            previous = a;
        }
        return result;
    }

    // ── the model, for the paragraphs without a usable tag ─────────────────────────────────────────────────────────────────────

    public sealed record SpeakerAnswer(
        [property: Description("The number of the marked paragraph, as in [Q12].")] int Q,
        [property: Description("Who speaks the quoted words in that paragraph, by the name the text uses for them; \"narrator\" if the narrator speaks and the text gives no name; \"unknown\" if you cannot tell.")] string Speaker,
        [property: Description("A phrase of 3 to 15 words copied exactly from the chapter that shows who is speaking (a speech tag, a form of address, the line before); empty if none.")] string Evidence);

    public sealed record SpeakerAnswers(List<SpeakerAnswer> Speakers);

    public const string Prompt =
        """
        Below is chapter {chapter} of "{title}" by {author}. Some paragraphs start with a marker like [Q1]. For each marked paragraph, say who speaks the quoted words in it.
        Give the speaker by the name the text uses for them (for example a surname or a title the text uses). If the speaker is the narrator, give the narrator's name if the text makes it clear, else "narrator". If you cannot tell, say "unknown". Do not guess.
        For each, copy as evidence a short phrase (3 to 15 words) exactly as it appears in the chapter that shows who is speaking: a speech tag, a form of address, or the line just before. Answer for every marked paragraph, in order.

        {text}
        """;

    /// <summary>The chapter's text with <c>[Qn] </c> before each paragraph in <paramref name="marked"/> (n = its index in that list + 1).</summary>
    public static string MarkedChapter(string text, ChapterInfo chapter, IReadOnlyList<Utterance> marked)
    {
        var sb = new StringBuilder();
        var at = chapter.CharStart;
        for (var i = 0; i < marked.Count; i++)
        {
            sb.Append(text[at..marked[i].ParaStart]).Append($"[Q{i + 1}] ");
            at = marked[i].ParaStart;
        }
        sb.Append(text[at..chapter.CharEnd]);
        return sb.ToString();
    }

    /// <summary>
    /// The model's answer for one marked paragraph, checked: the evidence must be found (score ≥ <paramref name="threshold"/>) in the text
    /// from 3,000 characters before the paragraph to 500 after it, and the name must be a character of the book.
    /// </summary>
    public static Attributed Check(Utterance u, SpeakerAnswer? answer, string text, Cast cast, double threshold, string? narrator)
    {
        if (answer is null || string.IsNullOrWhiteSpace(answer.Speaker) || answer.Speaker.Trim().Equals("unknown", StringComparison.OrdinalIgnoreCase))
            return new Attributed(u, null, "unknown");
        var from = Math.Max(0, u.ParaStart - 3000);
        var to = Math.Min(text.Length, u.ParaEnd + 500);
        var match = string.IsNullOrWhiteSpace(answer.Evidence) ? null : EvidenceChecker.Find(answer.Evidence, text[from..to], from);
        if (match is null || match.Score < threshold) return new Attributed(u, null, "rejected", answer.Evidence);
        var name = answer.Speaker.Trim().Equals("narrator", StringComparison.OrdinalIgnoreCase) ? narrator : answer.Speaker;
        var roster = name is null ? null : cast.RosterOf(name);
        return roster switch
        {
            null => new Attributed(u, null, "unknown", answer.Evidence),
            "" => new Attributed(u, null, "other", answer.Evidence),
            _ => new Attributed(u, roster, "model", answer.Evidence),
        };
    }

    // ── selection ──────────────────────────────────────────────────────────────────────────────────────────────────────────────

    public sealed record Candidate(VoiceLine Line, double Score);

    private static readonly HashSet<string> PlotWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "murder", "murdered", "murderer", "kill", "killed", "killing", "dead", "death", "die", "died", "dying", "corpse", "body", "poison", "poisoned",
        "secret", "secrets", "treasure", "map", "mutiny", "mutineers", "mutineer", "hanged", "hang", "blood", "bloody", "wound", "wounded", "betray",
        "betrayed", "traitor", "plot", "spy", "spies", "escape", "escaped", "convict", "inheritance", "will", "heir", "letter", "diamonds", "stolen", "theft",
    };

    private static readonly HashSet<string> NumberWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "eleven", "twelve", "twenty", "thirty", "forty", "fifty",
        "hundred", "thousand", "pounds", "guineas", "pistoles", "livres", "crowns", "miles", "leagues", "o'clock",
    };

    /// <summary>
    /// Every roster character's candidate lines, best first. A character's habit words are the words they use at least three times and
    /// at least 2.5 times as often as the book's dialogue as a whole (names excluded).
    /// </summary>
    public static Dictionary<string, List<Candidate>> Select(IReadOnlyList<Attributed> attributed, IReadOnlyList<string> roster, ISet<string> nameWords)
    {
        var all = Words(attributed.Select(a => a.Utterance.Text)).GroupBy(w => w).ToDictionary(g => g.Key, g => g.Count());
        var total = Math.Max(1, all.Values.Sum());
        var result = new Dictionary<string, List<Candidate>>();
        foreach (var name in roster)
        {
            var mine = attributed.Where(a => a.Speaker == name).ToList();
            var counts = Words(mine.Select(a => a.Utterance.Text)).GroupBy(w => w).ToDictionary(g => g.Key, g => g.Count());
            var n = Math.Max(1, counts.Values.Sum());
            var habits = counts.Where(c => c.Value >= 3 && !nameWords.Contains(c.Key) && (double)c.Value / n >= 2.5 * all[c.Key] / total)
                .Select(c => c.Key).ToHashSet();
            var scored = mine
                .Select(a => a.Utterance)
                .Where(u => WordCount(u.Text) is >= MinWords and <= MaxWords)
                .DistinctBy(u => u.Text)
                .Select(u => new Candidate(new VoiceLine(name, u.Chapter, u.Pos, u.Text), Score(u.Text, habits, nameWords)))
                .OrderByDescending(c => c.Score).ThenBy(c => c.Line.Pos)
                .ToList();
            // Variety: at most MaxPerChapter from one chapter among the pre-selected.
            var ordered = new List<Candidate>();
            var perChapter = new Dictionary<int, int>();
            foreach (var c in scored)
                if (ordered.Count < PreSelected && perChapter.GetValueOrDefault(c.Line.Chapter) < MaxPerChapter)
                {
                    ordered.Add(c);
                    perChapter[c.Line.Chapter] = perChapter.GetValueOrDefault(c.Line.Chapter) + 1;
                }
            ordered.AddRange(scored.Except(ordered).Take(Listed - ordered.Count));
            result[name] = ordered;
        }
        return result;
    }

    /// <summary>How well a line shows a manner of speech rather than the plot. See <see cref="Select"/>.</summary>
    public static double Score(string line, ISet<string> habits, ISet<string> nameWords)
    {
        var words = Words([line]).ToList();
        double score = Math.Min(3, words.Where(habits.Contains).Distinct().Count());
        if (line.Contains('!')) score += 0.5;
        if (line.Contains('?')) score += 0.3;
        if (line.Contains('—') || line.Contains(';')) score += 0.3;
        if (WordCount(line) is >= 12 and <= 30) score += 0.5;
        score -= 1.5 * Names(line, nameWords);
        score -= 1.0 * words.Count(w => NumberWords.Contains(w)) + 1.0 * DigitRun().Matches(line).Count;
        score -= 2.0 * words.Count(PlotWords.Contains);
        return Math.Round(score, 2);
    }

    /// <summary>Names in a line that aren't a form of address: a name word not set off by commas or at the start as a vocative.</summary>
    public static int Names(string line, ISet<string> nameWords)
    {
        var count = 0;
        foreach (Match m in CapitalWord().Matches(line))
        {
            var w = Cast.Norm(m.Value).ToLowerInvariant();
            if (!nameWords.Contains(w)) continue;
            var before = line[..m.Index].TrimEnd();
            var after = line[(m.Index + m.Length)..].TrimStart();
            var vocative = (before.Length == 0 || before.EndsWith(',') || before.EndsWith("my dear", StringComparison.OrdinalIgnoreCase) || before.EndsWith('…'))
                && (after.Length == 0 || after[0] is ',' or '!' or '?' or '.' or ';');
            if (!vocative) count++;
        }
        return count;
    }

    /// <summary>Titles and common words that start some names ("the captain", "Mr. Holmes", "Long John"): not names on their own.</summary>
    private static readonly HashSet<string> NotNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "of", "mr", "mrs", "miss", "dr", "doctor", "sir", "lady", "lord", "captain", "cap'n", "squire", "master", "mister", "monsieur",
        "madame", "mademoiselle", "milord", "milady", "monseigneur", "father", "mother", "old", "young", "little", "great", "long", "black", "blind",
        "good", "poor", "king", "queen", "duke", "cardinal", "abbe", "chevalier", "count", "countess", "baron", "comte", "your", "his", "her", "my",
    };

    /// <summary>The words of the book's proper names: capitalised words of three letters or more in Character and Location names and aliases,
    /// without titles and common words (<see cref="NotNames"/>), lower-cased.</summary>
    public static HashSet<string> NameWords(BookGraph graph) =>
        graph.Nodes.Where(n => n.Labels.Contains("Character") || n.Labels.Contains("Location"))
            .SelectMany(n => (n.Properties.GetValueOrDefault("aliases") is IReadOnlyList<object?> l ? l.OfType<string>() : [])
                .Append(n.Properties.GetValueOrDefault("name") as string ?? ""))
            .SelectMany(a => CapitalWord().Matches(a).Select(m => Cast.Norm(m.Value).ToLowerInvariant().Trim('\'')))
            .Where(w => w.Length >= 3 && !NotNames.Contains(w))
            .ToHashSet();

    /// <summary>An alias that is a proper name: it has a capitalised word that is not a title or a common word ("Monsieur" alone is not).</summary>
    public static bool IsName(string alias) =>
        CapitalWord().Matches(alias).Any(m => !NotNames.Contains(Cast.Norm(m.Value).Trim('\'')));

    public static IEnumerable<string> Words(IEnumerable<string> lines) =>
        lines.SelectMany(l => WordPattern().Matches(Cast.Norm(l)).Select(m => m.Value.ToLowerInvariant().Trim('\'')).Where(w => w.Length > 0));

    public static int WordCount(string s) => WordPattern().Matches(s).Count;

    // ── the review file ────────────────────────────────────────────────────────────────────────────────────────────────────────

    public static string Review(string title, string bookId, string runId, IReadOnlyList<string> roster, IReadOnlyDictionary<string, List<Candidate>> candidates,
        IReadOnlyDictionary<string, int> attributedCounts)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {title} · voice lines · review");
        sb.AppendLine();
        sb.AppendLine($"Book `{bookId}` · run `{runId}` · generated {DateTime.UtcNow:yyyy-MM-dd} by `index voice`. The chat uses only the lines marked `[x]`; until this file is reviewed, the pre-selected ones.");
        sb.AppendLine();
        sb.AppendLine("Mark every line: `[x]` keep · `[-]` drop. Keep lines that show **how** the character speaks; drop lines that mostly tell what happens. The lines are quoted from the text; keep them word for word.");
        sb.AppendLine($"Then: `dotnet run --project tools/TemporalGraph.Cli -- index voice --book {bookId} --apply-review`.");
        foreach (var name in roster)
        {
            var list = candidates.GetValueOrDefault(name) ?? [];
            sb.AppendLine();
            sb.AppendLine($"## {name}");
            sb.AppendLine();
            sb.AppendLine($"<!-- {attributedCounts.GetValueOrDefault(name)} utterances attributed; {list.Count} candidates listed (8–40 words, best first); the first {Math.Min(PreSelected, list.Count)} pre-selected. -->");
            for (var i = 0; i < list.Count; i++)
                sb.AppendLine($"- [{(i < PreSelected ? "x" : " ")}] ch{list[i].Line.Chapter}{ReviewSep}@{list[i].Line.Pos}{ReviewSep}score {list[i].Score.ToString("0.0#", CultureInfo.InvariantCulture)}{ReviewSep}“{list[i].Line.Text}”");
        }
        return sb.ToString();
    }

    public const string ReviewSep = " · ";

    /// <summary>The kept lines of a review file; throws <see cref="InvalidDataException"/> on a line it can't read.</summary>
    public static List<VoiceLine> ParseReview(string review)
    {
        var kept = new List<VoiceLine>();
        string? character = null;
        var n = 0;
        foreach (var raw in review.Replace("\r\n", "\n").Split('\n'))
        {
            n++;
            var line = raw.TrimEnd();
            if (line.StartsWith("## ", StringComparison.Ordinal)) { character = line[3..].Trim(); continue; }
            if (!line.StartsWith("- [", StringComparison.Ordinal)) continue;
            var m = ReviewLine().Match(line);
            if (!m.Success || character is null) throw new InvalidDataException($"Line {n}: not a review line: {line}");
            if (m.Groups["mark"].Value != "x") continue;
            kept.Add(new VoiceLine(character, int.Parse(m.Groups["ch"].Value, CultureInfo.InvariantCulture), long.Parse(m.Groups["pos"].Value, CultureInfo.InvariantCulture), m.Groups["text"].Value));
        }
        return kept;
    }

    [GeneratedRegex(@"^- \[(?<mark>[ x\-])\] ch(?<ch>\d+) · @(?<pos>\d+) · score -?[\d.]+ · “(?<text>.*)”$")]
    private static partial Regex ReviewLine();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRun();

    [GeneratedRegex(@"[\p{L}][\p{L}']*")]
    private static partial Regex WordPattern();

    [GeneratedRegex(@"(?<![\p{L}])\p{Lu}[\p{L}'’]+")]
    private static partial Regex CapitalWord();

    [GeneratedRegex(@"\d+")]
    private static partial Regex DigitRun();
}
