using System.Text;

namespace TemporalGraph.Cli.Indexing;

/// <summary>Where an evidence quote sits in the book text, and how well it matched.</summary>
/// <param name="Method">"exact" (after normalization) or "fuzzy" (Smith-Waterman).</param>
/// <param name="Score">1.0 for exact; for fuzzy, the alignment score ÷ the best possible score.</param>
public sealed record EvidenceMatch(string Method, double Score, int CharStart, int CharEnd);

/// <summary>
/// Checks that an evidence quote really is in the chunk. Both sides are normalized first: curly quotes become
/// straight (‘ ’ → ', “ ” → ") and every run of whitespace becomes one space. Then:
/// 1. exact substring match; else
/// 2. Smith-Waterman local alignment (match +2, mismatch −1, gap −1), as SafePassage
///    (https://arxiv.org/html/2510.00276v1) used. Score = best local score ÷ (2 × quote length).
/// Offsets are mapped back to the original book text (UTF-16 code units, [start, end)).
/// </summary>
public static class EvidenceChecker
{
    public const int Match = 2, Mismatch = -1, Gap = -1;

    /// <summary>Normalized text plus, for each normalized character, its offset in the original.</summary>
    public sealed record Normalized(string Text, int[] Map);

    public static Normalized Normalize(string original, int baseOffset = 0)
    {
        var sb = new StringBuilder(original.Length);
        var map = new List<int>(original.Length);
        var inSpace = false;
        for (var i = 0; i < original.Length; i++)
        {
            var c = original[i];
            if (char.IsWhiteSpace(c))
            {
                if (!inSpace && sb.Length > 0) { sb.Append(' '); map.Add(baseOffset + i); }
                inSpace = true;
                continue;
            }
            inSpace = false;
            sb.Append(c switch { '‘' or '’' => '\'', '“' or '”' => '"', _ => c });
            map.Add(baseOffset + i);
        }
        // Drop a trailing space.
        if (sb.Length > 0 && sb[^1] == ' ') { sb.Length--; map.RemoveAt(map.Count - 1); }
        return new Normalized(sb.ToString(), [.. map]);
    }

    /// <param name="chunkText">The chunk's text, which starts at <paramref name="chunkStart"/> in the book.</param>
    /// <returns>Null when the quote is empty.</returns>
    public static EvidenceMatch? Find(string quote, string chunkText, int chunkStart)
    {
        var q = Normalize(quote).Text;
        if (q.Length == 0) return null;
        var c = Normalize(chunkText, chunkStart);

        var exact = c.Text.IndexOf(q, StringComparison.Ordinal);
        if (exact >= 0)
            return new EvidenceMatch("exact", 1.0, c.Map[exact], c.Map[exact + q.Length - 1] + 1);

        var (score, start, end) = SmithWaterman(q, c.Text);
        if (end <= start) return new EvidenceMatch("fuzzy", 0, chunkStart, chunkStart);
        return new EvidenceMatch("fuzzy", Math.Round((double)score / (Match * q.Length), 4), c.Map[start], c.Map[end - 1] + 1);
    }

    /// <summary>Best local alignment of the whole of <paramref name="query"/> against part of <paramref name="target"/>; returns the target span [start, end).</summary>
    public static (int Score, int Start, int End) SmithWaterman(string query, string target)
    {
        int n = query.Length, m = target.Length;
        // Rolling rows for the score and the target start index of the best path into each cell.
        var prev = new int[m + 1];
        var cur = new int[m + 1];
        var prevStart = new int[m + 1];
        var curStart = new int[m + 1];
        int best = 0, bestStart = 0, bestEnd = 0;
        for (var i = 1; i <= n; i++)
        {
            cur[0] = 0;
            curStart[0] = 0;
            for (var j = 1; j <= m; j++)
            {
                var diag = prev[j - 1] + (query[i - 1] == target[j - 1] ? Match : Mismatch);
                var up = prev[j] + Gap;
                var left = cur[j - 1] + Gap;
                int s, st;
                if (diag >= up && diag >= left) { s = diag; st = prev[j - 1] == 0 ? j - 1 : prevStart[j - 1]; }
                else if (up >= left) { s = up; st = prev[j] == 0 ? j : prevStart[j]; }
                else { s = left; st = cur[j - 1] == 0 ? j - 1 : curStart[j - 1]; }
                if (s <= 0) { s = 0; st = j; }
                cur[j] = s;
                curStart[j] = st;
                if (s > best) { best = s; bestStart = st; bestEnd = j; }
            }
            (prev, cur) = (cur, prev);
            (prevStart, curStart) = (curStart, prevStart);
        }
        return (best, bestStart, bestEnd);
    }

    /// <summary>Finds <paramref name="surface"/> (normalized) inside the evidence span of the book text; null if absent.</summary>
    public static (int Start, int End)? LocateSurface(string surface, string bookText, EvidenceMatch evidence)
    {
        var s = Normalize(surface).Text;
        if (s.Length == 0 || evidence.CharEnd <= evidence.CharStart) return null;
        var span = Normalize(bookText[evidence.CharStart..evidence.CharEnd], evidence.CharStart);
        var i = span.Text.IndexOf(s, StringComparison.Ordinal);
        if (i < 0) return null;
        return (span.Map[i], span.Map[i + s.Length - 1] + 1);
    }
}
