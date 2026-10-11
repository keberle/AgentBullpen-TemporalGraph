using System.Text;
using Microsoft.Extensions.AI;
using TemporalGraph.Llm;
using TemporalGraph.Cli.Runs;
using TemporalGraph.Cli.Telemetry;

namespace TemporalGraph.Cli.Indexing;

// Stage 2 as it was before the full schema (the name normalization, Jaro-Winkler and the result records are still used). `index resolve`
// now runs CorefStage2 (a BookNLP backbone, one voted decision per pair of clusters, no transitive merging); the union-find
// below is kept only for the tests of the earlier behaviour.
// 1. Group mentions by exact normalized name (and type).
// 2. Candidate pairs among same-type groups by string similarity (configurable method and threshold).
// 3. One structured-output LLM decision per candidate pair: same entity or not, canonical name, one-line reason.
// 4. Merge the "same" pairs (union-find); every merge records its reason and both sides' evidence offsets.

/// <summary>Structured output of one resolution call. Strict mode: all required; CanonicalName is string|null.</summary>
public sealed record MergeDecision(bool SameEntity, string? CanonicalName, string Reason);

public sealed record NameGroup(int Id, MentionType Type, string Key, IReadOnlyList<Mention> Mentions)
{
    /// <summary>Surface forms, most frequent first.</summary>
    public IReadOnlyList<string> Surfaces => Mentions.GroupBy(m => m.Text).OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key.Length).Select(g => g.Key).ToList();
}

public sealed record CandidatePair(int A, int B, string Method, double Score);

public sealed record PairDecision(CandidatePair Pair, string NameA, string NameB, MergeDecision Decision,
    IReadOnlyList<int[]> EvidenceA, IReadOnlyList<int[]> EvidenceB);

/// <param name="Kind"><c>descriptive</c> for an entity the link stage added under the words the text uses for someone it does
/// not name ("the stranger", "the man on the tor"), so that an identity reveal can join it to the named one later; null otherwise.</param>
public sealed record Entity(int Id, string CanonicalName, MentionType Type, IReadOnlyList<string> Aliases,
    IReadOnlyList<Mention> Mentions, int FirstMention, int[] Groups, string? Kind = null)
{
    public const string Descriptive = "descriptive";
}

/// <param name="Identities">The identity reveals Stage 2 voted (two entities the text states are one person; never merged).
/// Null in a stage-2.json written before the full schema.</param>
public sealed record Stage2Result(
    string BookId, IReadOnlyList<NameGroup> Groups, IReadOnlyList<CandidatePair> Candidates,
    IReadOnlyList<PairDecision> Decisions, IReadOnlyList<Entity> Entities, IReadOnlyList<string> Conflicts,
    ResolveSettings Settings, IReadOnlyList<IdentityReveal>? Identities = null);

/// <param name="Similarity">"token-containment+jaro-winkler": a pair is a candidate when one name's words are all in
/// the other's (after dropping honorifics), or their Jaro-Winkler similarity is at or above the threshold.</param>
public sealed record ResolveSettings(string Similarity = ResolveSettings.Default, double JaroWinklerThreshold = 0.88, int EvidencePerSide = 3)
{
    public const string Default = "token-containment+jaro-winkler";
}

public static class Stage2
{
    public const string StageName = "stage-2";

    // Honorifics dropped for token containment only (not for grouping).
    private static readonly HashSet<string> Honorifics = ["mr", "mrs", "miss", "ms", "dr", "sir", "lady", "lord", "the", "of"];

    /// <summary>Case-folded, curly quotes straightened, whitespace collapsed, trailing punctuation and a possessive 's dropped.</summary>
    public static string NormalizeName(string name)
    {
        var s = EvidenceChecker.Normalize(name).Text.ToLowerInvariant().Trim();
        if (s.EndsWith("'s")) s = s[..^2];
        return s.TrimEnd('.', ',', ';', ':', '!', '?', '"', '\'').Trim();
    }

    public static List<NameGroup> Group(IReadOnlyList<Mention> mentions) =>
        mentions.GroupBy(m => (m.Type, Key: NormalizeName(m.Text)))
            .OrderBy(g => g.Min(m => m.CharStart))
            .Select((g, i) => new NameGroup(i + 1, g.Key.Type, g.Key.Key, g.OrderBy(m => m.CharStart).ToList()))
            .ToList();

    public static List<CandidatePair> Candidates(IReadOnlyList<NameGroup> groups, ResolveSettings s)
    {
        if (s.Similarity != ResolveSettings.Default) throw new InvalidOperationException($"Unknown similarity method '{s.Similarity}'.");
        var pairs = new List<CandidatePair>();
        for (var i = 0; i < groups.Count; i++)
            for (var j = i + 1; j < groups.Count; j++)
            {
                var (a, b) = (groups[i], groups[j]);
                if (a.Type != b.Type) continue;
                var ta = Tokens(a.Key);
                var tb = Tokens(b.Key);
                if (ta.Count > 0 && tb.Count > 0 && (ta.IsSubsetOf(tb) || tb.IsSubsetOf(ta)))
                    pairs.Add(new CandidatePair(a.Id, b.Id, "token-containment", 1.0));
                else
                {
                    var jw = JaroWinkler(a.Key, b.Key);
                    if (jw >= s.JaroWinklerThreshold) pairs.Add(new CandidatePair(a.Id, b.Id, "jaro-winkler", Math.Round(jw, 4)));
                }
            }
        return pairs;
    }

    private static HashSet<string> Tokens(string key) =>
        key.Split([' ', '.', ',', '-'], StringSplitOptions.RemoveEmptyEntries).Where(t => !Honorifics.Contains(t)).ToHashSet();

    public static double JaroWinkler(string s1, string s2)
    {
        if (s1 == s2) return 1;
        if (s1.Length == 0 || s2.Length == 0) return 0;
        var range = Math.Max(0, Math.Max(s1.Length, s2.Length) / 2 - 1);
        var m1 = new bool[s1.Length];
        var m2 = new bool[s2.Length];
        var matches = 0;
        for (var i = 0; i < s1.Length; i++)
            for (var j = Math.Max(0, i - range); j < Math.Min(s2.Length, i + range + 1); j++)
                if (!m2[j] && s1[i] == s2[j]) { m1[i] = m2[j] = true; matches++; break; }
        if (matches == 0) return 0;
        var t = 0;
        for (int i = 0, k = 0; i < s1.Length; i++)
        {
            if (!m1[i]) continue;
            while (!m2[k]) k++;
            if (s1[i] != s2[k]) t++;
            k++;
        }
        var jaro = (matches / (double)s1.Length + matches / (double)s2.Length + (matches - t / 2.0) / matches) / 3;
        var prefix = 0;
        while (prefix < Math.Min(4, Math.Min(s1.Length, s2.Length)) && s1[prefix] == s2[prefix]) prefix++;
        return jaro + prefix * 0.1 * (1 - jaro);
    }

    public static string PairPrompt(NameGroup a, NameGroup b, int evidencePerSide)
    {
        var sb = new StringBuilder();
        void Side(string label, NameGroup g)
        {
            sb.AppendLine($"Group {label}: type {g.Type}; names used: {string.Join(", ", g.Surfaces.Select(s => $"\"{s}\""))}; {g.Mentions.Count} mention(s).");
            foreach (var m in g.Mentions.Take(evidencePerSide)) sb.AppendLine($"  - chapter {m.Chapter}: \"{m.Evidence}\"");
        }
        Side("A", a);
        Side("B", b);
        sb.Append("Do groups A and B refer to the same entity?");
        return sb.ToString();
    }

    public static async Task<Stage2Result> RunAsync(
        IChatClient client, LlmCallContext? context, SkillFile skill, string bookId, IReadOnlyList<Mention> mentions,
        ResolveSettings settings, RunFolder? run, CancellationToken ct)
    {
        var groups = Group(mentions);
        var byId = groups.ToDictionary(g => g.Id);
        var candidates = Candidates(groups, settings);
        var decisions = new List<PairDecision>();
        if (context is not null)
        {
            context.BookId = bookId;
            context.Chapter = null;
            context.ChunkIndex = null;
            context.SkillName = skill.Name;
            context.SkillVersion = skill.Version.ToString();
        }

        foreach (var pair in candidates)
        {
            var (a, b) = (byId[pair.A], byId[pair.B]);
            using var span = IndexingTelemetry.Source.StartActivity($"pair {a.Surfaces[0]} / {b.Surfaces[0]}");
            span?.SetTag(IndexingTelemetry.RunId, run?.RunId);
            span?.SetTag(IndexingTelemetry.Stage, StageName);
            span?.SetTag(IndexingTelemetry.BookId, bookId);
            span?.SetTag(IndexingTelemetry.SkillName, skill.Name);
            span?.SetTag(IndexingTelemetry.SkillVersion, skill.Version);
            span?.SetTag("candidate.method", pair.Method);
            span?.SetTag("candidate.score", pair.Score);

            List<ChatMessage> messages =
            [
                new(ChatRole.System, skill.Text),
                new(ChatRole.User, PairPrompt(a, b, settings.EvidencePerSide)),
            ];
            var response = await client.GetResponseAsync<MergeDecision>(messages, Stage1.Json, Stage1.StrictOptions(), useJsonSchemaResponseFormat: true, ct);
            var d = response.Result;
            span?.SetTag("decision.same", d.SameEntity);
            decisions.Add(new PairDecision(pair, a.Surfaces[0], b.Surfaces[0], d,
                a.Mentions.Select(m => new[] { m.CharStart, m.CharEnd }).ToList(),
                b.Mentions.Select(m => new[] { m.CharStart, m.CharEnd }).ToList()));
        }

        var (entities, conflicts) = Merge(groups, decisions);
        foreach (var c in conflicts)
            run?.AppendFailure(new { type = Scoring.FailureTags.MergeConflict, stage = StageName, runId = run.RunId, bookId, detail = c });
        return new Stage2Result(bookId, groups, candidates, decisions, entities, conflicts, settings);
    }

    /// <summary>
    /// Union-find over the "same" decisions. Canonical name: the canonical name the LLM gave most often within the
    /// cluster (ties: longer); a cluster with no merge takes its most frequent surface form (ties: longer).
    /// A conflict is a pair the LLM called different that ends up in one cluster through other merges.
    /// </summary>
    public static (List<Entity> Entities, List<string> Conflicts) Merge(IReadOnlyList<NameGroup> groups, IReadOnlyList<PairDecision> decisions)
    {
        var parent = groups.ToDictionary(g => g.Id, g => g.Id);
        int Find(int x) => parent[x] == x ? x : parent[x] = Find(parent[x]);
        foreach (var d in decisions.Where(d => d.Decision.SameEntity))
            parent[Find(d.Pair.A)] = Find(d.Pair.B);

        var conflicts = decisions
            .Where(d => !d.Decision.SameEntity && Find(d.Pair.A) == Find(d.Pair.B))
            .Select(d => $"\"{d.NameA}\" and \"{d.NameB}\" were judged different ({d.Decision.Reason}) but are merged through other pairs")
            .ToList();

        var entities = groups.GroupBy(g => Find(g.Id))
            .Select(cluster =>
            {
                var ids = cluster.Select(g => g.Id).ToHashSet();
                var mentions = cluster.SelectMany(g => g.Mentions).OrderBy(m => m.CharStart).ToList();
                var canonicalVotes = decisions
                    .Where(d => d.Decision.SameEntity && ids.Contains(d.Pair.A) && !string.IsNullOrWhiteSpace(d.Decision.CanonicalName))
                    .GroupBy(d => d.Decision.CanonicalName!.Trim())
                    .OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key.Length).Select(g => g.Key).FirstOrDefault();
                var surfaceDefault = mentions.GroupBy(m => m.Text).OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key.Length).First().Key;
                var aliases = mentions.Select(m => m.Text).Distinct().OrderBy(a => a, StringComparer.Ordinal).ToList();
                return (Mentions: mentions, Canonical: canonicalVotes ?? surfaceDefault, Aliases: aliases, Groups: ids.Order().ToArray(), Type: cluster.First().Type);
            })
            .OrderBy(e => e.Mentions[0].CharStart)
            .Select((e, i) => new Entity(i + 1, e.Canonical, e.Type, e.Aliases, e.Mentions, e.Mentions[0].CharStart, e.Groups))
            .ToList();
        return (entities, conflicts);
    }
}
