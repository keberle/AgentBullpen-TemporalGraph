using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TemporalGraph.Cli.Indexing;
using TemporalGraph.Cli.Runs;

namespace TemporalGraph.Cli.Crosscheck;

/// <summary>One mention of a LitBank coreference chain, mapped onto our text.</summary>
/// <param name="Chain">LitBank's chain label ("Mr__Sherlock_Holmes-0").</param>
/// <param name="Kind">PROP, NOM or PRON.</param>
/// <param name="Category">PER, LOC, GPE, FAC, ORG, VEH.</param>
public sealed record LitBankCorefMention(string Chain, string Kind, string Category, string Text, int? CharStart, int? CharEnd);

/// <summary>
/// Agreement of Stage 2's chains with LitBank's coreference over the slice LitBank annotates (the start of chapter 1).
/// Over the LitBank person mentions that fall on one of our chains, every pair of them is a question both sides answer: are the two
/// the same person? <see cref="Agreement"/> is the share of pairs answered alike. The link scores take LitBank's "same" as the reference:
/// precision = pairs both call the same ÷ pairs we call the same; recall = ÷ pairs LitBank calls the same.
/// </summary>
/// <param name="PersonMentions">LitBank's person mentions in the slice (names, noun phrases and pronouns), with an offset in our text.</param>
/// <param name="OnOurChains">Those that overlap a mention of one of our chains.</param>
/// <param name="ByKind">Per mention kind: LitBank's count and how many fall on our chains.</param>
public sealed record CorefAgreement(string Source, int SliceStart, int SliceEnd, int PersonMentions, int OnOurChains, IReadOnlyDictionary<string, int[]> ByKind,
    int LitBankChains, int OurChains, long Pairs, long Agree, double Agreement, long SameInBoth, long SameInLitBankOnly, long SameInOursOnly,
    double LinkPrecision, double LinkRecall, double LinkF1, IReadOnlyList<string> Disagreements);

/// <summary>
/// LitBank's coreference annotation of #2852 (data/crosscheck/litbank/coref/, CC BY 4.0), read-only, as a second check
/// on Stage 2 for chapter 1. The brat file lists every mention twice: once with its kind and category (<c>NOM_PER</c>), once with its
/// chain's label; the two are joined by their span. The text is the one the entity annotation uses, aligned the same way.
/// </summary>
public static partial class LitBankCoref
{
    [GeneratedRegex(@"^T\d+\t(\S+) (\d+) (\d+)\t(.*)$")]
    private static partial Regex Line();

    [GeneratedRegex(@"^(PROP|NOM|PRON)_([A-Z]+)$")]
    private static partial Regex Typed();

    public static string Folder(string root) => Path.Combine(LitBank.Folder(root), "coref");

    public static bool Exists(string root) => File.Exists(Path.Combine(Folder(root), LitBank.BookFile + ".ann"));

    public static List<LitBankCorefMention> Load(string root, string bookText, int searchFrom)
    {
        var lbText = File.ReadAllText(Path.Combine(Folder(root), LitBank.BookFile + ".txt"));
        var (tokens, map, _, _) = LitBank.Align(lbText, bookText, searchFrom);
        var typed = new Dictionary<(int, int), (string Kind, string Category)>();
        var chains = new List<(string Chain, int Start, int End, string Text)>();
        foreach (var line in File.ReadAllLines(Path.Combine(Folder(root), LitBank.BookFile + ".ann")))
        {
            var m = Line().Match(line);
            if (!m.Success) continue;
            var (start, end) = (int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value));
            if (Typed().Match(m.Groups[1].Value) is { Success: true } t) typed[(start, end)] = (t.Groups[1].Value, t.Groups[2].Value);
            else chains.Add((m.Groups[1].Value, start, end, m.Groups[4].Value));
        }
        return chains.Where(c => typed.ContainsKey((c.Start, c.End))).Select(c =>
        {
            var first = tokens.FindIndex(t => t.Index >= c.Start);
            var last = tokens.FindLastIndex(t => t.Index < c.End);
            int? cs = first >= 0 && map.TryGetValue(tokens[first].Index, out var f) ? f.Start : null;
            int? ce = last >= 0 && map.TryGetValue(tokens[last].Index, out var l) ? l.End : null;
            var (kind, category) = typed[(c.Start, c.End)];
            return new LitBankCorefMention(c.Chain, kind, category, c.Text, cs, ce);
        }).ToList();
    }

    /// <summary>
    /// Stage 2's chains as spans: each Character entity's own mentions, plus every BookNLP mention (pronouns included) of the chains
    /// the entity was built from. A chain Stage 2 split (it mixed two people) gives only the entity's own mentions.
    /// </summary>
    public static Dictionary<int, List<(int Start, int End)>> OurChains(RunFolder run, Stage2Result stage2)
    {
        var json = JsonNode.Parse(File.ReadAllText(run.File(RunFolder.Stage2Json)))!;
        var source = (json["clusters"]?.AsArray() ?? []).ToDictionary(c => c!["id"]!.GetValue<int>(), c => c!["source"]!.GetValue<string>());
        var byChain = BookNlpChains.Exists(run) ? BookNlpChains.Load(run).GroupBy(m => m.Chain).ToDictionary(g => g.Key, g => g.Select(m => (m.CharStart, m.CharEnd)).ToList()) : [];
        var spans = new Dictionary<int, List<(int, int)>>();
        foreach (var e in stage2.Entities.Where(e => e.Type == MentionType.Character))
        {
            var list = e.Mentions.Select(m => (m.CharStart, m.CharEnd)).ToList();
            foreach (var cluster in e.Groups)
                if (source.TryGetValue(cluster, out var s) && s.Split(':') is [("booknlp" or "booknlp-nominal"), var id] && int.TryParse(id, out var chain) && byChain.TryGetValue(chain, out var more))
                    list.AddRange(more);
            spans[e.Id] = list;
        }
        return spans;
    }

    public static CorefAgreement Compare(IReadOnlyList<LitBankCorefMention> litBank, IReadOnlyDictionary<int, List<(int Start, int End)>> ours, IReadOnlyDictionary<int, string> names)
    {
        var people = litBank.Where(m => m.Category == "PER" && m.CharStart is not null && m.CharEnd is not null).ToList();
        int? EntityOf(LitBankCorefMention m)
        {
            (int Entity, int Overlap)? best = null;
            foreach (var (entity, spans) in ours)
                foreach (var (start, end) in spans)
                {
                    var overlap = Math.Min(end, m.CharEnd!.Value) - Math.Max(start, m.CharStart!.Value);
                    if (overlap > 0 && (best is null || overlap > best.Value.Overlap)) best = (entity, overlap);
                }
            return best?.Entity;
        }
        var aligned = people.Select(m => (m, Entity: EntityOf(m))).Where(x => x.Entity is not null).Select(x => (x.m, Entity: x.Entity!.Value)).ToList();
        long pairs = 0, agree = 0, both = 0, onlyTheirs = 0, onlyOurs = 0;
        var split = new Dictionary<(string, int, int), int>();
        var merged = new Dictionary<(int, string, string), int>();
        for (var i = 0; i < aligned.Count; i++)
            for (var j = i + 1; j < aligned.Count; j++)
            {
                var sameTheirs = aligned[i].m.Chain == aligned[j].m.Chain;
                var sameOurs = aligned[i].Entity == aligned[j].Entity;
                pairs++;
                if (sameTheirs == sameOurs) agree++;
                if (sameTheirs && sameOurs) both++;
                else if (sameTheirs)
                {
                    onlyTheirs++;
                    var key = (aligned[i].m.Chain, Math.Min(aligned[i].Entity, aligned[j].Entity), Math.Max(aligned[i].Entity, aligned[j].Entity));
                    split[key] = split.GetValueOrDefault(key) + 1;
                }
                else if (sameOurs)
                {
                    onlyOurs++;
                    var (a, b) = string.CompareOrdinal(aligned[i].m.Chain, aligned[j].m.Chain) <= 0 ? (aligned[i].m.Chain, aligned[j].m.Chain) : (aligned[j].m.Chain, aligned[i].m.Chain);
                    merged[(aligned[i].Entity, a, b)] = merged.GetValueOrDefault((aligned[i].Entity, a, b)) + 1;
                }
            }
        string Name(int id) => names.TryGetValue(id, out var n) ? n : $"#{id}";
        var disagreements = split.OrderByDescending(kv => kv.Value).Select(kv => $"LitBank's {kv.Key.Item1} is split between our {Name(kv.Key.Item2)} and {Name(kv.Key.Item3)} ({kv.Value} pairs)")
            .Concat(merged.OrderByDescending(kv => kv.Value).Select(kv => $"our {Name(kv.Key.Item1)} joins LitBank's {kv.Key.Item2} and {kv.Key.Item3} ({kv.Value} pairs)")).Take(12).ToList();
        static double Ratio(long a, long b) => b == 0 ? 0 : Math.Round(a / (double)b, 4);
        var (p, r) = (Ratio(both, both + onlyOurs), Ratio(both, both + onlyTheirs));
        var slice = people.Count == 0 ? (0, 0) : (people.Min(m => m.CharStart!.Value), people.Max(m => m.CharEnd!.Value));
        return new CorefAgreement("LitBank coreference", slice.Item1, slice.Item2, people.Count, aligned.Count,
            new[] { "PROP", "NOM", "PRON" }.ToDictionary(k => k, k => new[] { people.Count(m => m.Kind == k), aligned.Count(x => x.m.Kind == k) }),
            people.Select(m => m.Chain).Distinct().Count(), aligned.Select(x => x.Entity).Distinct().Count(), pairs, agree, Ratio(agree, pairs),
            both, onlyTheirs, onlyOurs, p, r, p + r == 0 ? 0 : Math.Round(2 * p * r / (p + r), 4), disagreements);
    }
}
