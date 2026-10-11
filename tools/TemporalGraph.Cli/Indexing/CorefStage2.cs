using System.Text;
using Microsoft.Extensions.AI;
using TemporalGraph.Cli.Runs;
using TemporalGraph.Cli.Scoring;
using TemporalGraph.Cli.Telemetry;
using TemporalGraph.Llm;

namespace TemporalGraph.Cli.Indexing;

// Stage 2 — entity resolution on a BookNLP backbone (skills/resolve-entities.md).
//
// 1. BookNLP's chains (BookNlpChains) are the skeleton. What BookNLP 1.0.8 gives, measured on #2852: it clusters names by
//    their spelling, links pronouns to those name chains, and puts every noun phrase with the same words in a chain of its
//    own ("the baronet" ×41 is one chain; no noun phrase is in a name chain). Some name chains mix two people
//    ("Barrymore" with "Mrs. Barrymore").
// 2. A chain with at least one Stage 1 character mention is a candidate Character. Stage 1 mentions are aligned to chains by
//    text offset. A chain whose names carry a woman's title next to a bare surname or a man's title is BookNLP-flagged
//    ambiguity and is split by title before anything else.
// 3. Mentions no chain covers are grouped by name into leftover clusters. Locations are resolved as before (Stage 1 name
//    groups, token containment or Jaro-Winkler), with the same merge rule as characters.
// 4. The LLM decides only the residue, one decision per pair of clusters, voted (k samples, m votes): pairs that share a
//    name word, an honorific, or a spelling; and every noun-phrase chain against the named characters it appears with.
//    There is no union-find: two clusters end up in one entity only when every pair between their groups was itself voted
//    "same" (A = B and B = C does not make A = C). A pair that a merge needs and that was never asked is asked then.
// 5. An identity reveal is never a merge: when the text itself states that two names are one person, the pair is
//    recorded (with the quote, evidence-checked) and becomes an IDENTICAL_TO edge with both clocks.

/// <summary>Structured output of one cluster-pair call. Strict mode: every field required; nullable fields are string|null.</summary>
/// <param name="Relation"><c>same</c> (the narrator uses the names interchangeably), <c>identity-reveal</c> (the text states that the
/// two are one person; <paramref name="Quote"/> is that statement) or <c>different</c>.</param>
/// <param name="SameChains">The ids of the chains that are one person: both ids for <c>same</c>, empty otherwise.</param>
/// <param name="CanonicalName">For <c>same</c>: the fullest proper name used; else null.</param>
/// <param name="Quote">For <c>identity-reveal</c>: the verbatim statement of the identity, from the passages; else null.</param>
public sealed record ClusterAnswer(string Relation, List<int> SameChains, string? CanonicalName, string? Quote, string Reason);

public static class ClusterRelations
{
    public const string Same = "same";
    public const string IdentityReveal = "identity-reveal";
    public const string Different = "different";
    public static readonly IReadOnlyList<string> All = [Same, IdentityReveal, Different];
}

/// <param name="Source">Where the cluster came from: <c>booknlp:131</c> (a chain), <c>booknlp:131/mrs</c> (a flagged chain split by
/// title), <c>booknlp-nominal:228</c> (a noun-phrase chain), <c>leftover</c> (Stage 1 mentions no chain covers) or <c>stage-1</c> (a location).</param>
public sealed record Cluster(int Id, MentionType Type, string Source, IReadOnlyList<Mention> Mentions, bool Nominal = false)
{
    /// <summary>Surface forms, most frequent first.</summary>
    public IReadOnlyList<string> Surfaces => Mentions.GroupBy(m => m.Text).OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key.Length)
        .ThenBy(g => g.Key, StringComparer.Ordinal).Select(g => g.Key).ToList();
}

/// <param name="Why">Why the pair was asked: <c>name-word</c>, <c>honorific</c>, <c>spelling</c>, <c>noun-phrase</c> or <c>closure</c>
/// (a merge needed this pair).</param>
public sealed record ClusterPair(int A, int B, string Why);

/// <param name="Relation">The voted relation: one with at least m votes, else <c>different</c>.</param>
/// <param name="VotesByRelation">How many readable samples gave each relation.</param>
public sealed record ClusterDecision(ClusterPair Pair, string NameA, string NameB, string Relation, IReadOnlyDictionary<string, int> VotesByRelation,
    string? CanonicalName, string? Quote, EvidenceMatch? QuoteMatch, string Reason);

/// <summary>Two entities the text states are one person (never merged). <paramref name="Source"/>: stage-2 or stage-3.</summary>
public sealed record IdentityReveal(int A, int B, string Quote, int CharStart, int CharEnd, int Votes, string Source = CorefStage2.StageName);

public sealed record CorefSettings(double JaroWinklerThreshold = 0.88, int EvidencePerSide = 3, int Samples = 5, int MinVotes = 4,
    int NominalMinMentions = 3, int NominalCandidates = 3, int Parallel = 4);

public sealed record CorefResult(string BookId, IReadOnlyList<Cluster> Clusters, IReadOnlyList<ClusterDecision> Decisions, IReadOnlyList<Entity> Entities,
    IReadOnlyList<IdentityReveal> Identities, IReadOnlyList<string> Notes, CorefSettings Settings, int ChainMentions, int AlignedMentions, int FlaggedChains);

public static class CorefStage2
{
    public const string StageName = "stage-2";

    private static readonly HashSet<string> MaleTitles = ["mr", "sir", "lord", "monsieur", "m"];
    private static readonly HashSet<string> FemaleTitles = ["mrs", "miss", "ms", "lady", "madame", "mme", "mademoiselle", "mlle"];
    private static readonly HashSet<string> OtherTitles = ["dr", "doctor", "captain", "colonel", "major", "professor", "inspector", "father", "rev", "reverend"];
    // Words that alone don't tie two names together.
    private static readonly HashSet<string> NameStopWords = ["the", "of", "and", "a", "an", "de", "la", "le", "du", "family", "old", "young", "poor", "late", "house"];
    private static readonly string[] DefiniteStarts = ["the ", "my ", "our ", "his ", "her ", "their ", "this ", "that "];

    private static string[] Words(string name) =>
        Stage2.NormalizeName(name).Split([' ', '.', ',', '-'], StringSplitOptions.RemoveEmptyEntries);

    private static bool IsTitle(string word) => MaleTitles.Contains(word) || FemaleTitles.Contains(word) || OtherTitles.Contains(word);

    /// <summary>The title a name starts with ("mrs" for "Mrs. Barrymore"), or null.</summary>
    public static string? TitleOf(string name) => Words(name) is [var first, _, ..] && IsTitle(first) ? first : null;

    /// <summary>The words of a name that can tie it to another: no titles, no stop words.</summary>
    public static HashSet<string> NameWords(string name) => Words(name).Where(w => !IsTitle(w) && !NameStopWords.Contains(w) && w.Length > 1).ToHashSet();

    /// <summary>
    /// BookNLP-flagged ambiguity inside one name chain: its names carry a woman's title, and also a man's title, a bare name or a
    /// second woman's title ("Barrymore" with "Mrs. Barrymore"; "Miss Stapleton" with "Mrs. Stapleton"). Such a chain is split.
    /// </summary>
    public static bool Flagged(IEnumerable<string> names)
    {
        var titles = names.Select(TitleOf).Distinct().ToList();
        var women = titles.Where(t => t is not null && FemaleTitles.Contains(t)).ToList();
        return women.Count > 1 || (women.Count == 1 && titles.Count > 1);
    }

    /// <summary>The key a flagged chain is split by: the woman's title, or "other" (bare names and every other title together).</summary>
    public static string SplitKey(string name) => TitleOf(name) is { } t && FemaleTitles.Contains(t) ? t : "other";

    /// <summary>A noun-phrase chain worth asking about: a chain with no name in it (its pronouns don't count) whose most used noun
    /// phrase is definite or possessive ("the baronet", "my friend") and which has at least <paramref name="minMentions"/> noun-phrase
    /// mentions. Indefinite phrases ("a man", "someone") and forms of address ("sir") name no one.</summary>
    public static bool NominalCandidate(IReadOnlyList<ChainMention> chain, int minMentions)
    {
        if (chain.Any(m => m.Prop == "PROP")) return false;
        var phrases = chain.Where(m => m.Prop == "NOM").ToList();
        if (phrases.Count < minMentions) return false;
        var main = phrases.GroupBy(m => m.Text.ToLowerInvariant()).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).First().Key;
        return DefiniteStarts.Any(d => main.StartsWith(d, StringComparison.Ordinal)) && main.Count(c => c == ' ') is >= 1 and <= 4;
    }

    /// <summary>
    /// The clusters before any LLM call (steps 2–3 above). Character mentions: each Stage 1 mention joins the chain of the BookNLP
    /// PER name mention it overlaps most; a flagged chain is split by <see cref="SplitKey"/>; the rest are grouped by normalized
    /// name. Location mentions: grouped by normalized name. Noun-phrase chains (<see cref="NominalCandidate"/>) come last, marked
    /// <see cref="Cluster.Nominal"/>; their mentions are BookNLP's, with the text as the book has it.
    /// </summary>
    public static (List<Cluster> Clusters, int Aligned, int FlaggedChains) BuildClusters(IReadOnlyList<Mention> stage1, IReadOnlyList<ChainMention> chains,
        string bookText, Func<int, int> chapterOf, CorefSettings settings)
    {
        var names = chains.Where(c => c.Cat == "PER" && c.Prop == "PROP").OrderBy(c => c.CharStart).ToList();
        var starts = names.Select(n => n.CharStart).ToArray();
        int? ChainOf(Mention m)
        {
            // The BookNLP name mentions that overlap [CharStart, CharEnd): a short scan around the insertion point.
            var i = Array.BinarySearch(starts, m.CharStart);
            if (i < 0) i = ~i;
            (int Chain, int Overlap)? best = null;
            for (var k = Math.Max(0, i - 4); k < Math.Min(names.Count, i + 4); k++)
            {
                var overlap = Math.Min(m.CharEnd, names[k].CharEnd) - Math.Max(m.CharStart, names[k].CharStart);
                if (overlap > 0 && (best is null || overlap > best.Value.Overlap)) best = (names[k].Chain, overlap);
            }
            return best?.Chain;
        }

        var characters = stage1.Where(m => m.Type == MentionType.Character).ToList();
        var byChain = characters.Select(m => (Mention: m, Chain: ChainOf(m))).ToList();
        var clusters = new List<(string Source, MentionType Type, List<Mention> Mentions, bool Nominal)>();
        var flagged = 0;
        foreach (var chain in byChain.Where(x => x.Chain is not null).GroupBy(x => x.Chain!.Value))
        {
            var mentions = chain.Select(x => x.Mention).ToList();
            if (!Flagged(mentions.Select(m => m.Text)))
            {
                clusters.Add(($"booknlp:{chain.Key}", MentionType.Character, mentions, false));
                continue;
            }
            flagged++;
            foreach (var part in mentions.GroupBy(m => SplitKey(m.Text)))
                clusters.Add(($"booknlp:{chain.Key}/{part.Key}", MentionType.Character, part.ToList(), false));
        }
        foreach (var g in byChain.Where(x => x.Chain is null).Select(x => x.Mention).GroupBy(m => Stage2.NormalizeName(m.Text)))
            clusters.Add(("leftover", MentionType.Character, g.ToList(), false));
        foreach (var g in stage1.Where(m => m.Type == MentionType.Location).GroupBy(m => Stage2.NormalizeName(m.Text)))
            clusters.Add(("stage-1", MentionType.Location, g.ToList(), false));

        foreach (var chain in chains.Where(c => c.Cat == "PER").GroupBy(c => c.Chain))
        {
            var list = chain.OrderBy(c => c.CharStart).ToList();
            if (!NominalCandidate(list, settings.NominalMinMentions)) continue;
            var mentions = list.Where(c => c.Prop == "NOM").Select(c => new Mention(0, Flat(bookText[c.CharStart..c.CharEnd]), MentionType.Character, 0, chapterOf(c.CharStart), "booknlp",
                c.CharStart, c.CharEnd, "", new EvidenceMatch("booknlp", 1, c.CharStart, c.CharEnd), 0)).ToList();
            clusters.Add(($"booknlp-nominal:{chain.Key}", MentionType.Character, mentions, true));
        }

        var numbered = clusters.Select(c => (c, First: c.Mentions.Min(m => m.CharStart))).OrderBy(x => x.c.Nominal).ThenBy(x => x.First)
            .Select((x, i) => new Cluster(i + 1, x.c.Type, x.c.Source, x.c.Mentions.OrderBy(m => m.CharStart).ToList(), x.c.Nominal)).ToList();
        return (numbered, byChain.Count(x => x.Chain is not null), flagged);
    }

    /// <summary>
    /// The pairs of named clusters the LLM is asked about. Characters: the two share a name word (titles and stop words left
    /// out), or a title, or their closest names are spelled alike (Jaro-Winkler at or above the threshold). Locations: one
    /// name's words are all in the other's, or Jaro-Winkler, as before the full schema.
    /// </summary>
    public static List<ClusterPair> Candidates(IReadOnlyList<Cluster> clusters, CorefSettings s)
    {
        var named = clusters.Where(c => !c.Nominal).ToList();
        var words = named.ToDictionary(c => c.Id, c => c.Surfaces.SelectMany(NameWords).ToHashSet());
        var titles = named.ToDictionary(c => c.Id, c => c.Surfaces.Select(TitleOf).OfType<string>().ToHashSet());
        var keys = named.ToDictionary(c => c.Id, c => c.Surfaces.Select(Stage2.NormalizeName).Distinct().ToList());
        var pairs = new List<ClusterPair>();
        for (var i = 0; i < named.Count; i++)
            for (var j = i + 1; j < named.Count; j++)
            {
                var (a, b) = (named[i], named[j]);
                if (a.Type != b.Type) continue;
                bool Spelled() => keys[a.Id].Any(x => keys[b.Id].Any(y => Stage2.JaroWinkler(x, y) >= s.JaroWinklerThreshold));
                if (a.Type == MentionType.Location)
                {
                    var (ta, tb) = (LocationWords(keys[a.Id]), LocationWords(keys[b.Id]));
                    if (ta.Count > 0 && tb.Count > 0 && (ta.IsSubsetOf(tb) || tb.IsSubsetOf(ta))) pairs.Add(new ClusterPair(a.Id, b.Id, "name-word"));
                    else if (Spelled()) pairs.Add(new ClusterPair(a.Id, b.Id, "spelling"));
                    continue;
                }
                if (words[a.Id].Overlaps(words[b.Id])) pairs.Add(new ClusterPair(a.Id, b.Id, "name-word"));
                else if (Spelled()) pairs.Add(new ClusterPair(a.Id, b.Id, "spelling"));
                else if (titles[a.Id].Overlaps(titles[b.Id])) pairs.Add(new ClusterPair(a.Id, b.Id, "honorific"));
            }
        return pairs;
    }

    private static HashSet<string> LocationWords(IEnumerable<string> keys) =>
        keys.SelectMany(k => k.Split([' ', '.', ',', '-'], StringSplitOptions.RemoveEmptyEntries)).Where(w => w is not ("the" or "of")).ToHashSet();

    private static string Flat(string s) => string.Join(' ', s.Split((char[])[' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Up to <paramref name="n"/> items spread evenly over the list, the first and the last among them.</summary>
    public static List<T> Spread<T>(IReadOnlyList<T> items, int n)
    {
        if (items.Count <= n) return items.ToList();
        if (n == 1) return [items[0]];
        return Enumerable.Range(0, n).Select(i => items[(int)Math.Round(i * (items.Count - 1) / (double)(n - 1))]).Distinct().ToList();
    }

    /// <summary>A passage shown to the model: the paragraph around a mention (cut to about 400 characters), with its span in the book.</summary>
    public sealed record Passage(int Chapter, int CharStart, int CharEnd, string Text);

    public sealed class Passages(Stage0Result stage0, string bookText)
    {
        private readonly List<ParagraphInfo> _paragraphs = stage0.Chapters.SelectMany(c => c.Paragraphs).OrderBy(p => p.CharStart).ToList();

        public ParagraphInfo? ParagraphOf(int offset)
        {
            int lo = 0, hi = _paragraphs.Count - 1;
            while (lo <= hi)
            {
                var mid = (lo + hi) / 2;
                if (offset < _paragraphs[mid].CharStart) hi = mid - 1;
                else if (offset >= _paragraphs[mid].CharEnd) lo = mid + 1;
                else return _paragraphs[mid];
            }
            return null;
        }

        public Passage Around(Mention m, int width = 400)
        {
            var p = ParagraphOf(m.CharStart);
            var (start, end) = p is null ? (Math.Max(0, m.CharStart - width / 2), Math.Min(bookText.Length, m.CharEnd + width / 2)) : (p.CharStart, p.CharEnd);
            if (end - start > width)
            {
                start = Math.Max(start, m.CharStart - width / 2);
                end = Math.Min(end, start + width);
                while (start > 0 && start < m.CharStart && !char.IsWhiteSpace(bookText[start - 1])) start++;
                while (end < bookText.Length && end > m.CharEnd && !char.IsWhiteSpace(bookText[end - 1])) end--;
            }
            return new Passage(m.Chapter, start, end, Flat(bookText[start..end]));
        }
    }

    /// <summary>The prompt for one pair: each side's names and evidence, then the passages where both appear (the first and the last two).</summary>
    public static (string Prompt, List<Passage> Shown) PairPrompt(Cluster a, Cluster b, Passages passages, int evidencePerSide)
    {
        var shown = new List<Passage>();
        var sb = new StringBuilder();
        void Side(string label, Cluster c)
        {
            var names = string.Join(", ", c.Mentions.GroupBy(m => m.Text).OrderByDescending(g => g.Count()).Take(8).Select(g => $"\"{g.Key}\" ×{g.Count()}"));
            var what = c.Nominal ? "a noun phrase" : c.Type.ToString();
            sb.AppendLine($"Chain {label} (id {c.Id}): {what}; names used: {names}; {c.Mentions.Count} mention(s), chapters {c.Mentions.Min(m => m.Chapter)}–{c.Mentions.Max(m => m.Chapter)}.");
            foreach (var m in Spread(c.Mentions, c.Nominal ? evidencePerSide + 2 : evidencePerSide))
            {
                var p = passages.Around(m);
                shown.Add(p);
                sb.AppendLine($"  - chapter {p.Chapter}: \"{p.Text}\"");
            }
        }
        Side("A", a);
        Side("B", b);
        var paragraphsA = a.Mentions.Select(m => passages.ParagraphOf(m.CharStart)).OfType<ParagraphInfo>().Select(p => p.CharStart).ToHashSet();
        var both = b.Mentions.Where(m => passages.ParagraphOf(m.CharStart) is { } p && paragraphsA.Contains(p.CharStart))
            .GroupBy(m => passages.ParagraphOf(m.CharStart)!.CharStart).Select(g => g.First()).OrderBy(m => m.CharStart).ToList();
        var picked = both.Count <= 3 ? both : [both[0], both[^2], both[^1]];
        if (picked.Count > 0)
        {
            sb.AppendLine("Passages where both appear:");
            foreach (var m in picked)
            {
                var p = passages.Around(m, 700);
                shown.Add(p);
                sb.AppendLine($"  - chapter {p.Chapter}: \"{p.Text}\"");
            }
        }
        sb.Append($"Are chain A and chain B the same person{(a.Type == MentionType.Location ? " or place" : "")}? Answer with the chain ids ({a.Id} and {b.Id}).");
        return (sb.ToString(), shown);
    }

    /// <summary>
    /// The vote over one pair's readable samples. A relation needs <paramref name="minVotes"/> votes, else the pair is
    /// <c>different</c>. An answer that says <c>same</c> without naming both ids counts as <c>different</c>. An identity reveal also
    /// needs a quote found in the passages shown (the evidence check); without one it is <c>different</c> too.
    /// </summary>
    public static ClusterDecision Vote(ClusterPair pair, Cluster a, Cluster b, IReadOnlyList<ClusterAnswer> answers, int minVotes,
        IReadOnlyList<Passage> shown, string bookText, double threshold)
    {
        string Relation(ClusterAnswer x) => x.Relation switch
        {
            ClusterRelations.Same when x.SameChains.Contains(a.Id) && x.SameChains.Contains(b.Id) => ClusterRelations.Same,
            ClusterRelations.IdentityReveal => ClusterRelations.IdentityReveal,
            _ => ClusterRelations.Different,
        };
        var votes = answers.GroupBy(Relation).ToDictionary(g => g.Key, g => g.Count());
        var winner = votes.Where(v => v.Value >= minVotes).OrderByDescending(v => v.Value).Select(v => v.Key).FirstOrDefault() ?? ClusterRelations.Different;
        var winners = answers.Where(x => Relation(x) == winner).ToList();
        var reason = winners.FirstOrDefault()?.Reason ?? (answers.Count == 0 ? "no readable answer" : $"no relation had {minVotes} votes");
        string? canonical = null, quote = null;
        EvidenceMatch? match = null;
        if (winner == ClusterRelations.Same)
            canonical = winners.Select(x => x.CanonicalName?.Trim()).Where(n => !string.IsNullOrEmpty(n)).GroupBy(n => n!).OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key.Length).Select(g => g.Key).FirstOrDefault();
        if (winner == ClusterRelations.IdentityReveal)
        {
            foreach (var q in winners.Select(x => x.Quote).Where(q => !string.IsNullOrWhiteSpace(q)).GroupBy(q => q!).OrderByDescending(g => g.Count()).Select(g => g.Key))
            {
                var best = shown.Select(p => EvidenceChecker.Find(q, bookText[p.CharStart..p.CharEnd], p.CharStart)).OfType<EvidenceMatch>().OrderByDescending(m => m.Score).FirstOrDefault();
                if (best is null || best.Score < threshold) continue;
                (quote, match) = (q, best);
                break;
            }
            if (match is null) (winner, reason) = (ClusterRelations.Different, "identity reveal without a quote found in the passages: " + reason);
        }
        return new ClusterDecision(pair, a.Surfaces[0], b.Surfaces[0], winner, votes, canonical, quote, match, reason);
    }

    /// <summary>
    /// No transitive merging. Groups start as single clusters; the <c>same</c> pairs are taken by votes (then ids), and two groups
    /// are joined only when every pair between them is voted <c>same</c>. <paramref name="decide"/> is called for a pair between
    /// them that was never asked (a closure pair). Returns the groups and a note for every <c>same</c> pair that did not merge.
    /// </summary>
    public static async Task<(List<List<int>> Groups, List<string> Notes)> MergeAsync(IReadOnlyList<Cluster> clusters, List<ClusterDecision> decisions,
        Func<ClusterPair, Task<ClusterDecision>> decide)
    {
        var byId = clusters.ToDictionary(c => c.Id);
        var groupOf = clusters.ToDictionary(c => c.Id, c => new List<int> { c.Id });
        var decided = decisions.ToDictionary(d => Key(d.Pair.A, d.Pair.B));
        var notes = new List<string>();
        foreach (var same in decisions.Where(d => d.Relation == ClusterRelations.Same)
                     .OrderByDescending(d => d.VotesByRelation.GetValueOrDefault(ClusterRelations.Same)).ThenBy(d => d.Pair.A).ThenBy(d => d.Pair.B).ToList())
        {
            var (ga, gb) = (groupOf[same.Pair.A], groupOf[same.Pair.B]);
            if (ReferenceEquals(ga, gb)) continue;
            string? blocked = null;
            foreach (var x in ga)
            {
                foreach (var y in gb)
                {
                    if (!decided.TryGetValue(Key(x, y), out var d))
                    {
                        d = await decide(new ClusterPair(Math.Min(x, y), Math.Max(x, y), "closure"));
                        decided[Key(x, y)] = d;
                        decisions.Add(d);
                    }
                    if (d.Relation != ClusterRelations.Same) { blocked = $"\"{byId[x].Surfaces[0]}\" and \"{byId[y].Surfaces[0]}\" are {d.Relation}"; break; }
                }
                if (blocked is not null) break;
            }
            if (blocked is not null)
            {
                notes.Add($"\"{same.NameA}\" and \"{same.NameB}\" were voted the same but not merged: {blocked} (no transitive merging)");
                continue;
            }
            ga.AddRange(gb);
            foreach (var y in gb) groupOf[y] = ga;
        }
        return (groupOf.Values.Distinct().Select(g => g.Order().ToList()).OrderBy(g => g[0]).ToList(), notes);

        static (int, int) Key(int a, int b) => a < b ? (a, b) : (b, a);
    }

    /// <summary>
    /// The canonical name of a merged group: the name the model gave most often for its <c>same</c> pairs when the group uses that
    /// name; else the fullest proper name among its mentions (most words, then most used, then longest), possessives left out.
    /// </summary>
    public static string CanonicalName(IReadOnlyList<Mention> mentions, IEnumerable<string?> voted)
    {
        var used = mentions.Where(m => m.Pass != "booknlp").Select(m => m.Text).ToList();
        if (used.Count == 0) used = mentions.Select(m => m.Text).ToList();
        var surfaces = used.ToHashSet(StringComparer.Ordinal);
        var byVote = voted.Where(n => n is not null && surfaces.Contains(n)).GroupBy(n => n!).OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key.Length).Select(g => g.Key).FirstOrDefault();
        if (byVote is not null) return byVote;
        static bool Possessive(string t) => t.EndsWith("’s", StringComparison.Ordinal) || t.EndsWith("'s", StringComparison.Ordinal);
        var proper = used.Where(t => t.Length > 0 && char.IsUpper(t[0]) && t.Any(char.IsLower) && !Possessive(t) && !t.Contains(',')).ToList();
        var pool = proper.Count > 0 ? proper : used;
        return pool.GroupBy(t => t).OrderByDescending(g => Words(g.Key).Length).ThenByDescending(g => g.Count()).ThenByDescending(g => g.Key.Length)
            .ThenBy(g => g.Key, StringComparer.Ordinal).First().Key;
    }

    public static async Task<CorefResult> RunAsync(IChatClient client, LlmCallContext? context, SkillFile skill, Stage0Result stage0, string bookText,
        IReadOnlyList<Mention> stage1, IReadOnlyList<ChainMention> chains, CorefSettings settings, double evidenceThreshold, RunFolder? run, CancellationToken ct)
    {
        if (context is not null)
        {
            context.BookId = stage0.BookId;
            context.Chapter = null;
            context.ChunkIndex = null;
            context.SkillName = skill.Name;
            context.SkillVersion = skill.Version.ToString();
        }
        var chapterOf = (int offset) => stage0.Chapters.FirstOrDefault(c => offset >= c.CharStart && offset < c.CharEnd)?.Number ?? 0;
        var (clusters, aligned, flagged) = BuildClusters(stage1, chains, bookText, chapterOf, settings);
        var byId = clusters.ToDictionary(c => c.Id);
        var passages = new Passages(stage0, bookText);

        async Task<ClusterDecision> Decide(ClusterPair pair, Cluster a, Cluster b)
        {
            using var span = IndexingTelemetry.Source.StartActivity($"pair {a.Surfaces[0]} / {b.Surfaces[0]}");
            span?.SetTag(IndexingTelemetry.RunId, run?.RunId);
            span?.SetTag(IndexingTelemetry.Stage, StageName);
            span?.SetTag(IndexingTelemetry.BookId, stage0.BookId);
            span?.SetTag("candidate.why", pair.Why);
            var (prompt, shown) = PairPrompt(a, b, passages, settings.EvidencePerSide);
            List<ChatMessage> messages = [new(ChatRole.System, skill.Text), new(ChatRole.User, prompt)];
            var replies = await Task.WhenAll(Enumerable.Range(0, settings.Samples).Select(async _ =>
            {
                try { return (await client.GetResponseAsync<ClusterAnswer>(messages, Stage1.Json, Stage1.StrictOptions(), useJsonSchemaResponseFormat: true, ct)).Result; }
                catch (System.Text.Json.JsonException) { return null; }
            }));
            var unreadable = replies.Count(r => r is null);
            if (unreadable > 0)
                run?.AppendFailure(new { type = FailureTags.UnreadableReply, stage = StageName, runId = run.RunId, bookId = stage0.BookId, pair = new[] { a.Surfaces[0], b.Surfaces[0] }, samples = unreadable });
            var decision = Vote(pair, a, b, replies.OfType<ClusterAnswer>().ToList(), settings.MinVotes, shown, bookText, evidenceThreshold);
            span?.SetTag("decision.relation", decision.Relation);
            return decision;
        }

        async Task<List<ClusterDecision>> DecideAll(IReadOnlyList<(ClusterPair Pair, Cluster A, Cluster B)> pairs)
        {
            using var gate = new SemaphoreSlim(Math.Max(1, settings.Parallel));
            return (await Task.WhenAll(pairs.Select(async p =>
            {
                await gate.WaitAsync(ct);
                try { return await Decide(p.Pair, p.A, p.B); }
                finally { gate.Release(); }
            }))).ToList();
        }

        // Named clusters: the candidate pairs, then the merge (closure pairs are asked as a merge needs them).
        var decisions = await DecideAll(Candidates(clusters, settings).Select(p => (p, byId[p.A], byId[p.B])).ToList());
        var named = clusters.Where(c => !c.Nominal).ToList();
        var (groups, notes) = await MergeAsync(named, decisions, pair => Decide(pair, byId[pair.A], byId[pair.B]));

        // Noun-phrase chains: each against the named groups it shares the most paragraphs with. A group stands as one cluster
        // (its mentions together) for the prompt; the decision is recorded against the group's first cluster.
        var groupMentions = groups.Select(g => (Group: g, Mentions: g.SelectMany(id => byId[id].Mentions).OrderBy(m => m.CharStart).ToList())).ToList();
        var paragraphGroups = new Dictionary<int, HashSet<int>>();
        for (var gi = 0; gi < groupMentions.Count; gi++)
        {
            if (byId[groupMentions[gi].Group[0]].Type != MentionType.Character) continue;
            foreach (var m in groupMentions[gi].Mentions)
                if (passages.ParagraphOf(m.CharStart) is { } p)
                    (paragraphGroups.TryGetValue(p.CharStart, out var set) ? set : paragraphGroups[p.CharStart] = []).Add(gi);
        }
        var nominalPairs = new List<(ClusterPair Pair, Cluster A, Cluster B, int Group)>();
        foreach (var nominal in clusters.Where(c => c.Nominal))
        {
            var counts = new Dictionary<int, int>();
            foreach (var m in nominal.Mentions)
                if (passages.ParagraphOf(m.CharStart) is { } p && paragraphGroups.TryGetValue(p.CharStart, out var here))
                    foreach (var gi in here) counts[gi] = counts.GetValueOrDefault(gi) + 1;
            foreach (var gi in counts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).Take(settings.NominalCandidates).Select(kv => kv.Key))
            {
                var first = byId[groupMentions[gi].Group[0]];
                nominalPairs.Add((new ClusterPair(first.Id, nominal.Id, "noun-phrase"), first with { Mentions = groupMentions[gi].Mentions }, nominal, gi));
            }
        }
        var nominalDecisions = await DecideAll(nominalPairs.Select(p => (p.Pair, p.A, p.B)).ToList());
        decisions.AddRange(nominalDecisions);
        var attached = new Dictionary<int, List<Cluster>>();
        foreach (var nominal in clusters.Where(c => c.Nominal))
        {
            var same = nominalPairs.Zip(nominalDecisions).Where(x => x.First.B.Id == nominal.Id && x.Second.Relation == ClusterRelations.Same).ToList();
            if (same.Count == 1) (attached.TryGetValue(same[0].First.Group, out var list) ? list : attached[same[0].First.Group] = []).Add(nominal);
            else if (same.Count > 1)
                notes.Add($"\"{nominal.Surfaces[0]}\" was voted the same as {same.Count} characters ({string.Join(", ", same.Select(x => $"\"{x.First.A.Surfaces[0]}\""))}); attached to none");
        }

        // Entities, in the order of their first named mention.
        var built = groupMentions.Select((g, gi) =>
        {
            var extra = attached.TryGetValue(gi, out var list) ? list : [];
            var mentions = g.Mentions.Concat(extra.SelectMany(c => c.Mentions)).OrderBy(m => m.CharStart).ToList();
            var ids = g.Group.ToHashSet();
            var voted = decisions.Where(d => d.Relation == ClusterRelations.Same && ids.Contains(d.Pair.A) && ids.Contains(d.Pair.B)).Select(d => d.CanonicalName);
            return (Clusters: g.Group.Concat(extra.Select(c => c.Id)).Order().ToArray(), Type: byId[g.Group[0]].Type, Mentions: mentions,
                Canonical: CanonicalName(g.Mentions, voted), First: g.Mentions[0].CharStart);
        }).OrderBy(e => e.First).ToList();
        var entities = UniqueNames(built.Select((e, i) => new Entity(i + 1, e.Canonical, e.Type,
            e.Mentions.Select(m => m.Text).Distinct().OrderBy(a => a, StringComparer.Ordinal).ToList(), e.Mentions, e.First, e.Clusters)).ToList());

        var entityOf = entities.SelectMany(e => e.Groups.Select(c => (Cluster: c, e.Id))).ToDictionary(x => x.Cluster, x => x.Id);
        var identities = decisions.Where(d => d.Relation == ClusterRelations.IdentityReveal && d.QuoteMatch is not null)
            .Select(d => (A: entityOf[d.Pair.A], B: entityOf[d.Pair.B], d))
            .Where(x => x.A != x.B)
            .GroupBy(x => (Math.Min(x.A, x.B), Math.Max(x.A, x.B)))
            .Select(g => g.OrderBy(x => x.d.QuoteMatch!.CharStart).First())
            .Select(x => new IdentityReveal(Math.Min(x.A, x.B), Math.Max(x.A, x.B), x.d.Quote!, x.d.QuoteMatch!.CharStart, x.d.QuoteMatch.CharEnd,
                x.d.VotesByRelation.GetValueOrDefault(ClusterRelations.IdentityReveal)))
            .OrderBy(r => r.CharStart).ToList();
        foreach (var note in notes)
            run?.AppendFailure(new { type = FailureTags.MergeConflict, stage = StageName, runId = run.RunId, bookId = stage0.BookId, detail = note });
        return new CorefResult(stage0.BookId, clusters, decisions, entities, identities, notes, settings, chains.Count, aligned, flagged);
    }

    /// <summary>
    /// Canonical names are unique per type. Without transitive merging two groups can keep the same fullest name (the book's two
    /// Hugo Baskervilles: the one of the legend and the one who wrote it down); the later one, by first mention, is numbered:
    /// "Hugo Baskerville (2)". The aliases are untouched.
    /// </summary>
    public static List<Entity> UniqueNames(IReadOnlyList<Entity> entities)
    {
        var seen = new Dictionary<(MentionType, string), int>();
        return entities.OrderBy(e => e.FirstMention).Select(e =>
        {
            var n = seen[(e.Type, e.CanonicalName)] = seen.GetValueOrDefault((e.Type, e.CanonicalName)) + 1;
            return n == 1 ? e : e with { CanonicalName = $"{e.CanonicalName} ({n})" };
        }).OrderBy(e => e.Id).ToList();
    }

    /// <summary>Counts for stage-2.json and the console.</summary>
    public static Dictionary<string, object> Counts(CorefResult r) => new()
    {
        ["chainMentions"] = r.ChainMentions,
        ["stage1CharacterMentionsAligned"] = r.AlignedMentions,
        ["flaggedChainsSplit"] = r.FlaggedChains,
        ["clusters"] = r.Clusters.Count(c => !c.Nominal),
        ["nounPhraseChains"] = r.Clusters.Count(c => c.Nominal),
        ["nounPhraseChainsAttached"] = r.Entities.Sum(e => e.Groups.Count(id => r.Clusters.First(c => c.Id == id).Nominal)),
        ["pairsAsked"] = r.Decisions.Count,
        ["pairsByWhy"] = r.Decisions.GroupBy(d => d.Pair.Why).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count()),
        ["merges"] = r.Decisions.Count(d => d.Relation == ClusterRelations.Same),
        ["notMerged"] = r.Decisions.Count(d => d.Relation == ClusterRelations.Different),
        ["identityReveals"] = r.Identities.Count,
        ["conflicts"] = r.Notes.Count,
        ["entities"] = r.Entities.Count,
        ["characters"] = r.Entities.Count(e => e.Type == MentionType.Character),
        ["locations"] = r.Entities.Count(e => e.Type == MentionType.Location),
        ["samples"] = r.Settings.Samples,
        ["minVotes"] = r.Settings.MinVotes,
    };
}
