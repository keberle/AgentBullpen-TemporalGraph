using System.Text.RegularExpressions;
using TemporalGraph.Cli.Indexing;

namespace TemporalGraph.Cli.Crosscheck;

/// <summary>One LitBank entity annotation (brat), mapped onto our book text.</summary>
/// <param name="Category">LitBank's ACE category: PER, LOC, GPE, FAC, ORG, VEH.</param>
/// <param name="Proper">True when the first word is capitalized and isn't a determiner/possessive, and some capitalized
/// word isn't an honorific — a name ("James Mortimer, M.R.C.S."), not a common phrase ("the man", "his friends of the
/// C.C.H.") or a lone title (LitBank annotates the "Mr." inside "Mr. Sherlock Holmes" as its own PER).</param>
public sealed record LitBankEntity(string Id, string Category, string Text, int LitBankStart, int LitBankEnd, int? CharStart, int? CharEnd, bool Proper);

/// <param name="Substituted">Tokens matched through a known spelling difference (LitBank "--" = our "—"), counted per pair.</param>
public sealed record LitBankAlignment(int Tokens, int Aligned, int FirstCharStart, int LastCharEnd, IReadOnlyList<string> Unaligned,
    IReadOnlyDictionary<string, int> Substituted);

/// <summary>
/// LitBank (CC BY 4.0, https://github.com/dbamman/litbank) entity annotations for #2852. Their text is tokenized
/// (tokens separated by spaces, one sentence per line) and starts with the chapter heading and title, so offsets
/// don't align with ours: each LitBank token is found in our text in order, skipping whitespace and #2852's "_" italics markup (which LitBank dropped). LitBank writes "--"
/// for #2852's em dash "—"; that substitution is allowed and counted. Any other mismatch is reported as unaligned, and
/// the aligner resynchronizes by searching up to 40 characters ahead for the next token.
/// </summary>
public static partial class LitBank
{
    public const string BookFile = "2852_the_hound_of_the_baskervilles_brat";

    [GeneratedRegex(@"^(T\d+)\t(\S+) (\d+) (\d+)\t(.*)$")]
    private static partial Regex AnnLine();

    private static readonly HashSet<string> Determiners =
        ["the", "a", "an", "his", "her", "my", "our", "their", "your", "its", "this", "that", "these", "those", "some", "one", "every", "no", "several", "only", "any"];

    private static readonly HashSet<string> HonorificWords = ["mr.", "mrs.", "miss", "ms.", "dr.", "sir", "lady", "lord"];

    private static readonly Dictionary<string, string> Substitutions = new() { ["--"] = "—" };

    public static string Folder(string root) => Path.Combine(root, "data", "crosscheck", "litbank");

    public static (List<LitBankEntity> Entities, LitBankAlignment Alignment) Load(string root, string bookText, int searchFrom)
    {
        var (ann, tokens, map, unaligned, substituted) = Read(root, bookText, searchFrom);
        return Annotations(ann, tokens, map, unaligned, substituted);
    }

    private static (string[] Ann, List<(int Index, string Value)> Tokens, Dictionary<int, (int Start, int End)> Map, List<string> Unaligned, Dictionary<string, int> Substituted)
        Read(string root, string bookText, int searchFrom)
    {
        var folder = Folder(root);
        var lbText = File.ReadAllText(Path.Combine(folder, BookFile + ".txt"));
        var ann = File.ReadAllLines(Path.Combine(folder, BookFile + ".ann"));
        var (tokens, map, unaligned, substituted) = Align(lbText, bookText, searchFrom);
        return (ann, tokens, map, unaligned, substituted);
    }

    /// <summary>
    /// LitBank's tokens (with their offsets in the LitBank text) found in our text, in order: token start → our [start, end).
    /// Used for the entity file and for the coreference file, which annotate the same text.
    /// </summary>
    public static (List<(int Index, string Value)> Tokens, Dictionary<int, (int Start, int End)> Map, List<string> Unaligned, Dictionary<string, int> Substituted)
        Align(string lbText, string bookText, int searchFrom)
    {
        // LitBank tokens with their offsets in the LitBank text.
        var tokens = Regex.Matches(lbText, @"\S+").Select(m => (m.Index, m.Value)).ToList();

        // Align each token to our text, in order.
        var map = new Dictionary<int, (int Start, int End)>(); // LitBank token start -> our [start, end)
        var unaligned = new List<string>();
        var substituted = new Dictionary<string, int>();
        var pos = searchFrom;
        foreach (var (index, value) in tokens)
        {
            // #2852 marks italics with underscores (_Lancet_); LitBank's text has none. Skip them, counted.
            var italics = 0;
            while (pos < bookText.Length && (char.IsWhiteSpace(bookText[pos]) || bookText[pos] == '_'))
                if (bookText[pos++] == '_') italics++;
            if (italics > 0) substituted["_ (italics markup in #2852) skipped"] = substituted.GetValueOrDefault("_ (italics markup in #2852) skipped") + italics;

            var ours = value;
            var at = FindToken(bookText, ours, pos);
            if (at < 0 && Substitutions.TryGetValue(value, out var sub) && (at = FindToken(bookText, sub, pos)) >= 0)
            {
                ours = sub;
                var key = $"{value} = {sub}";
                substituted[key] = substituted.GetValueOrDefault(key) + 1;
            }
            if (at < 0)
            {
                unaligned.Add($"\"{value}\" at LitBank offset {index}");
                var ahead = bookText.IndexOf(value, pos, Math.Min(40, bookText.Length - pos), StringComparison.Ordinal);
                if (ahead >= 0) pos = ahead + value.Length; // resync on the same token a little further on
                else
                {
                    // A different word (LitBank "gray", #2852 "grey"): skip our next word.
                    var start = pos;
                    while (start < bookText.Length && char.IsWhiteSpace(bookText[start])) start++;
                    var stop = start;
                    while (stop < bookText.Length && char.IsLetterOrDigit(bookText[stop])) stop++;
                    pos = stop;
                }
                continue;
            }
            map[index] = (at, at + ours.Length);
            pos = at + ours.Length;
        }
        return (tokens, map, unaligned, substituted);
    }

    /// <summary>The entity annotations of data/crosscheck/litbank, mapped onto our text.</summary>
    private static (List<LitBankEntity> Entities, LitBankAlignment Alignment) Annotations(string[] ann, List<(int Index, string Value)> tokens,
        Dictionary<int, (int Start, int End)> map, List<string> unaligned, Dictionary<string, int> substituted)
    {

        var entities = new List<LitBankEntity>();
        foreach (var line in ann)
        {
            var m = AnnLine().Match(line);
            if (!m.Success) continue;
            var (start, end) = (int.Parse(m.Groups[3].Value), int.Parse(m.Groups[4].Value));
            var first = tokens.FindIndex(t => t.Index >= start);
            var last = tokens.FindLastIndex(t => t.Index < end);
            int? cs = first >= 0 && map.TryGetValue(tokens[first].Index, out var f) ? f.Start : null;
            int? ce = last >= 0 && map.TryGetValue(tokens[last].Index, out var l) ? l.End : null;
            var text = m.Groups[5].Value;
            entities.Add(new LitBankEntity(m.Groups[1].Value, m.Groups[2].Value, text, start, end, cs, ce, IsProper(text)));
        }

        var mapped = map.Values.ToList();
        return (entities, new LitBankAlignment(tokens.Count, map.Count,
            mapped.Count > 0 ? mapped.Min(v => v.Start) : -1, mapped.Count > 0 ? mapped.Max(v => v.End) : -1, unaligned, substituted));
    }

    /// <summary>The next occurrence of <paramref name="token"/> at or after <paramref name="from"/>, allowing only whitespace in between.</summary>
    private static int FindToken(string text, string token, int from)
    {
        var i = from;
        while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
        return string.CompareOrdinal(text, i, token, 0, token.Length) == 0 ? i : -1;
    }

    public static bool IsProper(string text)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Any(char.IsLetter)).ToList();
        if (words.Count == 0) return false;
        if (!char.IsUpper(words[0][0]) || Determiners.Contains(words[0].ToLowerInvariant())) return false;
        return words.Any(w => char.IsUpper(w[0]) && !HonorificWords.Contains(w.ToLowerInvariant()));
    }

    /// <summary>LitBank category → our type; ORG and VEH have no counterpart in this pass.</summary>
    public static MentionType? TypeOf(string category) => category switch
    {
        "PER" => MentionType.Character,
        "LOC" or "GPE" or "FAC" => MentionType.Location,
        _ => null,
    };

    /// <summary>LitBank's tokenization puts spaces before punctuation ("James Mortimer , M.R.C.S."); undo that for names.</summary>
    public static string Detokenize(string text) =>
        Regex.Replace(text, @"\s+([,.;:!?’”)])", "$1").Replace("“ ", "“").Replace("( ", "(");
}
