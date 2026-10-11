using System.Text;
using Microsoft.Extensions.AI;
using TemporalGraph.Cli.Runs;
using TemporalGraph.Cli.Scoring;
using TemporalGraph.Cli.Telemetry;
using TemporalGraph.Llm;

namespace TemporalGraph.Cli.Indexing;

// Stage 2: blocked, batched entity resolution (skills/resolve-blocks.md).
// The BookNLP backbone is unchanged (CorefStage2.BuildClusters: chains as candidate entities, Stage 1's mentions
// aligned into them by offset, a flagged chain split by title, no transitive merging, identity reveals never merged). What
// changes is how the model is asked:
//   1. Blocks. Clusters that share a name word, are spelled alike, or come from one split chain are tied; a block is a
//      connected group of tied clusters. A pair in two different blocks is never asked. Sharing only a title ("Mr.") ties nothing.
//   2. One call per block, not per pair: the block's name groups with two passages each; the model lists the sets that are
//      the same person and cites the passage that proves each. At most MaxGroupsPerCall groups in a call; a larger block is
//      split by the initial of the first name word.
//   3. A merge already made is not asked again: where a block was split, its merged groups are asked once more as single groups.
//   4. k samples per block, m votes per pair (3 and 2). A pair is "same" when m samples put both in one proven set.
// Noun-phrase chains ("the baronet") are asked the same way: one call with the chain and the characters it appears with.

/// <summary>Structured output of one block call. Strict mode: every field required.</summary>
/// <param name="Same">The sets of group ids that are one person (or one place). A group in no set is someone else.</param>
/// <param name="Proof">For every set: its ids, the fullest proper name used for that person, and a passage quoted from those shown that proves it.</param>
/// <param name="Reveals">Pairs of groups the text itself states are one person under two names (never listed in <paramref name="Same"/>), with the statement quoted.</param>
public sealed record BlockAnswer(List<List<int>> Same, List<BlockProof> Proof, List<BlockReveal> Reveals);

public sealed record BlockProof(List<int> Ids, string? CanonicalName, string Quote);

public sealed record BlockReveal(int A, int B, string Quote);

/// <param name="Why"><c>name</c> (the clusters are tied by name words, spelling or a split chain), <c>regroup</c> (the merged groups of a
/// block that was split, asked once more) or <c>noun-phrase</c>.</param>
public sealed record Block(int Id, IReadOnlyList<int> Clusters, string Why);

public static class BlockResolve
{
    public const int DefaultMaxGroups = 25;
    public const int DefaultSamples = 3;
    public const int DefaultMinVotes = 2;

    /// <summary>The passages shown per name group.</summary>
    public const int SnippetsPerGroup = 2;

    /// <summary>The pairs that tie two named clusters into one block: the pairwise candidates without the title-only ones, and the parts
    /// of one flagged chain.</summary>
    public static List<ClusterPair> Ties(IReadOnlyList<Cluster> clusters, CorefSettings settings)
    {
        var ties = CorefStage2.Candidates(clusters, settings).Where(p => p.Why != "honorific").ToList();
        foreach (var chain in clusters.Where(c => !c.Nominal && c.Source.Contains('/')).GroupBy(c => c.Source[..c.Source.IndexOf('/')]))
        {
            var parts = chain.OrderBy(c => c.Id).ToList();
            for (var i = 0; i < parts.Count; i++)
                for (var j = i + 1; j < parts.Count; j++)
                    if (!ties.Any(t => t.A == parts[i].Id && t.B == parts[j].Id)) ties.Add(new ClusterPair(parts[i].Id, parts[j].Id, "flagged-chain"));
        }
        return ties;
    }

    /// <summary>The connected groups of tied clusters (two or more), each sorted, in the order of their first cluster.</summary>
    public static List<List<int>> Components(IEnumerable<int> ids, IEnumerable<ClusterPair> ties)
    {
        var parent = ids.ToDictionary(i => i, i => i);
        int Root(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        foreach (var t in ties.Where(t => parent.ContainsKey(t.A) && parent.ContainsKey(t.B))) parent[Root(t.A)] = Root(t.B);
        return parent.Keys.GroupBy(Root).Select(g => g.Order().ToList()).Where(g => g.Count > 1).OrderBy(g => g[0]).ToList();
    }

    /// <summary>The initial a large block is split by: the first letter of the first name word of the cluster's most used name.</summary>
    public static char Initial(Cluster c) =>
        CorefStage2.NameWords(c.Surfaces[0]).Order(StringComparer.Ordinal).Select(w => w[0]).DefaultIfEmpty(Stage2.NormalizeName(c.Surfaces[0]).FirstOrDefault('?')).First();

    /// <summary>
    /// The blocks to ask (step 1–2). A component of at most <paramref name="maxGroups"/> clusters is one block. A larger one is split
    /// by <see cref="Initial"/>; initials are packed together up to the limit, an initial with more clusters than the limit is cut
    /// into runs of that size, and a part left with one cluster asks nothing. <paramref name="split"/> gets the components that were split.
    /// </summary>
    public static List<Block> Plan(IReadOnlyList<Cluster> clusters, IReadOnlyList<ClusterPair> ties, int maxGroups, out List<List<int>> split)
    {
        var byId = clusters.ToDictionary(c => c.Id);
        var blocks = new List<Block>();
        split = [];
        foreach (var component in Components(clusters.Where(c => !c.Nominal).Select(c => c.Id), ties))
        {
            if (component.Count <= maxGroups) { blocks.Add(new Block(blocks.Count + 1, component, "name")); continue; }
            split.Add(component);
            var current = new List<int>();
            void Flush() { if (current.Count > 1) blocks.Add(new Block(blocks.Count + 1, current.Order().ToList(), "name")); current = []; }
            foreach (var g in component.GroupBy(id => Initial(byId[id])).OrderBy(g => g.Key))
            {
                var ids = g.Order().ToList();
                if (current.Count + ids.Count > maxGroups) Flush();
                if (ids.Count <= maxGroups) { current.AddRange(ids); continue; }
                foreach (var run in ids.Chunk(maxGroups)) blocks.Add(new Block(blocks.Count + 1, run.ToList(), "name"));
            }
            Flush();
        }
        return blocks;
    }

    /// <summary>
    /// Step 3: a block's clusters with the merges already made taken as one group each — its lowest cluster stands for it, so a
    /// pair inside a merged group is not asked again. Null when fewer than two groups are left.
    /// </summary>
    public static List<int>? Collapse(IEnumerable<int> block, IReadOnlyList<List<int>> groups)
    {
        var representative = groups.SelectMany(g => g.Select(id => (id, Rep: g.Min()))).ToDictionary(x => x.id, x => x.Rep);
        var reps = block.Select(id => representative.GetValueOrDefault(id, id)).Distinct().Order().ToList();
        return reps.Count > 1 ? reps : null;
    }

    public static (string Prompt, List<CorefStage2.Passage> Shown) BlockPrompt(IReadOnlyList<Cluster> groups, CorefStage2.Passages passages)
    {
        var shown = new List<CorefStage2.Passage>();
        var sb = new StringBuilder("Name groups (each is a set of mentions a coreference tool put together):\n");
        foreach (var c in groups)
        {
            var names = string.Join(", ", c.Mentions.GroupBy(m => m.Text).OrderByDescending(g => g.Count()).Take(6).Select(g => $"\"{g.Key}\" ×{g.Count()}"));
            sb.AppendLine($"Group {c.Id}: {(c.Nominal ? "a noun phrase" : c.Type.ToString())}; names used: {names}; chapters {c.Mentions.Min(m => m.Chapter)}–{c.Mentions.Max(m => m.Chapter)}.");
            foreach (var m in CorefStage2.Spread(c.Mentions, SnippetsPerGroup))
            {
                var p = passages.Around(m);
                shown.Add(p);
                sb.AppendLine($"  - chapter {p.Chapter}: \"{p.Text}\"");
            }
        }
        sb.Append($"Which of these {groups.Count} groups are the same person or place? List the sets (ids from: {string.Join(", ", groups.Select(g => g.Id))}), and for each set quote the passage that proves it.");
        return (sb.ToString(), shown);
    }

    /// <summary>One sample's proven sets: a set of two or more listed ids whose proof quote is found in the passages shown (the
    /// evidence check); with the canonical name its proof gives.</summary>
    public static List<(HashSet<int> Ids, string? CanonicalName)> ProvenSets(BlockAnswer answer, IReadOnlySet<int> listed, IReadOnlyList<CorefStage2.Passage> shown, string bookText, double threshold)
    {
        var proven = new List<(HashSet<int>, string?)>();
        foreach (var set in answer.Same ?? [])
        {
            var ids = (set ?? []).Where(listed.Contains).ToHashSet();
            if (ids.Count < 2) continue;
            var proof = (answer.Proof ?? []).FirstOrDefault(p => p.Ids is not null && ids.IsSubsetOf(p.Ids) && Found(p.Quote, shown, bookText, threshold) is not null)
                ?? (answer.Proof ?? []).FirstOrDefault(p => p.Ids is not null && p.Ids.Count(ids.Contains) >= 2 && Found(p.Quote, shown, bookText, threshold) is not null);
            if (proof is not null) proven.Add((ids, string.IsNullOrWhiteSpace(proof.CanonicalName) ? null : proof.CanonicalName.Trim()));
        }
        return proven;
    }

    private static EvidenceMatch? Found(string? quote, IReadOnlyList<CorefStage2.Passage> shown, string bookText, double threshold)
    {
        if (string.IsNullOrWhiteSpace(quote)) return null;
        var best = shown.Select(p => EvidenceChecker.Find(quote, bookText[p.CharStart..p.CharEnd], p.CharStart)).OfType<EvidenceMatch>().OrderByDescending(m => m.Score).FirstOrDefault();
        return best is not null && best.Score >= threshold ? best : null;
    }

    /// <summary>
    /// The vote on a block (step 4): one decision per pair of its groups. <c>same</c> when at least <paramref name="minVotes"/>
    /// samples put both in one proven set; <c>identity-reveal</c> when that many name the pair as a reveal with a quote found in the
    /// passages; else <c>different</c>. A chain of sets (A with B in one, B with C in another) leaves A and C different.
    /// </summary>
    public static List<ClusterDecision> Vote(Block block, IReadOnlyList<Cluster> groups, IReadOnlyList<BlockAnswer> answers, int minVotes,
        IReadOnlyList<CorefStage2.Passage> shown, string bookText, double threshold)
    {
        var listed = groups.Select(g => g.Id).ToHashSet();
        var samples = answers.Select(a => ProvenSets(a, listed, shown, bookText, threshold)).ToList();
        var decisions = new List<ClusterDecision>();
        for (var i = 0; i < groups.Count; i++)
            for (var j = i + 1; j < groups.Count; j++)
            {
                var (a, b) = groups[i].Id < groups[j].Id ? (groups[i], groups[j]) : (groups[j], groups[i]);
                var together = samples.Select(s => s.FirstOrDefault(x => x.Ids.Contains(a.Id) && x.Ids.Contains(b.Id))).Where(x => x.Ids is not null).ToList();
                var reveals = answers.SelectMany(x => x.Reveals ?? []).Where(r => (r.A == a.Id && r.B == b.Id) || (r.A == b.Id && r.B == a.Id))
                    .Select(r => (r.Quote, Match: Found(r.Quote, shown, bookText, threshold))).Where(r => r.Match is not null).ToList();
                var votes = new Dictionary<string, int> { [ClusterRelations.Same] = together.Count, [ClusterRelations.IdentityReveal] = reveals.Count,
                    [ClusterRelations.Different] = Math.Max(0, answers.Count - together.Count - reveals.Count) };
                var pair = new ClusterPair(a.Id, b.Id, block.Why);
                if (together.Count >= minVotes)
                {
                    var canonical = together.Select(x => x.CanonicalName).Where(n => n is not null).GroupBy(n => n!).OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key.Length).Select(g => g.Key).FirstOrDefault();
                    decisions.Add(new ClusterDecision(pair, a.Surfaces[0], b.Surfaces[0], ClusterRelations.Same, votes, canonical, null, null, $"block {block.Id}: in one proven set in {together.Count} of {answers.Count} answers"));
                }
                else if (reveals.Count >= minVotes && together.Count == 0)
                {
                    var first = reveals.OrderBy(r => r.Match!.CharStart).First();
                    decisions.Add(new ClusterDecision(pair, a.Surfaces[0], b.Surfaces[0], ClusterRelations.IdentityReveal, votes, null, first.Quote, first.Match, $"block {block.Id}: the text states the identity"));
                }
                else decisions.Add(new ClusterDecision(pair, a.Surfaces[0], b.Surfaces[0], ClusterRelations.Different, votes, null, null, null,
                    answers.Count == 0 ? $"block {block.Id}: no readable answer" : $"block {block.Id}: in one proven set in {together.Count} of {answers.Count} answers"));
            }
        return decisions;
    }

    /// <param name="CandidatePairs">The pairs that would otherwise be asked one by one (the title-only ones included).</param>
    /// <param name="PairsInBlocks">The pairs the block calls decide.</param>
    public sealed record BlockStats(int CandidatePairs, int Blocks, int Calls, int PairsInBlocks, int LargestBlock, int SplitBlocks, int NounPhraseCalls, int PeakInFlight);

    public static async Task<(CorefResult Result, BlockStats Stats)> RunAsync(IChatClient client, LlmCallContext? context, SkillFile skill, Stage0Result stage0, string bookText,
        IReadOnlyList<Mention> stage1, IReadOnlyList<ChainMention> chains, CorefSettings settings, int maxGroups, double evidenceThreshold, StageRunner runner, RunFolder? run,
        CancellationToken ct, Action<int, int>? progress = null)
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
        var (clusters, aligned, flagged) = CorefStage2.BuildClusters(stage1, chains, bookText, chapterOf, settings);
        var byId = clusters.ToDictionary(c => c.Id);
        var passages = new CorefStage2.Passages(stage0, bookText);
        var system = new ChatMessage(ChatRole.System, skill.Text);
        var options = Stage1.StrictOptions();
        int calls = 0, done = 0, planned = 0;

        async Task<List<ClusterDecision>> Ask(Block block, IReadOnlyList<Cluster> groups)
        {
            using var span = IndexingTelemetry.Source.StartActivity($"block {block.Id}");
            span?.SetTag(IndexingTelemetry.RunId, run?.RunId);
            span?.SetTag(IndexingTelemetry.Stage, CorefStage2.StageName);
            span?.SetTag(IndexingTelemetry.BookId, stage0.BookId);
            span?.SetTag("block.groups", groups.Count);
            var (prompt, shown) = BlockPrompt(groups, passages);
            List<ChatMessage> messages = [system, new(ChatRole.User, prompt)];
            var replies = await Task.WhenAll(Enumerable.Range(0, settings.Samples).Select(async _ =>
            {
                Interlocked.Increment(ref calls);
                try { return await runner.CallAsync(async () => (await client.GetResponseAsync<BlockAnswer>(messages, Stage1.Json, options, useJsonSchemaResponseFormat: true, ct)).Result, ct, cache: new CacheHints("1h")); }
                catch (System.Text.Json.JsonException) { return null; }
            }));
            var unreadable = replies.Count(r => r is null);
            if (unreadable > 0)
                run?.AppendFailure(new { type = FailureTags.UnreadableReply, stage = CorefStage2.StageName, runId = run.RunId, bookId = stage0.BookId, block = block.Id, samples = unreadable });
            progress?.Invoke(Interlocked.Increment(ref done), planned);
            return Vote(block, groups, replies.OfType<BlockAnswer>().ToList(), settings.MinVotes, shown, bookText, evidenceThreshold);
        }

        // 1–2. The blocks of named clusters, asked side by side.
        var ties = Ties(clusters, settings);
        var blocks = Plan(clusters, ties, maxGroups, out var split);
        var nominals = clusters.Where(c => c.Nominal).ToList();
        planned = blocks.Count + split.Count + nominals.Count;
        var decisions = (await StageRunner.ForEachAsync(blocks, (b, _) => Ask(b, b.Clusters.Select(id => byId[id]).ToList()))).SelectMany(d => d).ToList();
        var named = clusters.Where(c => !c.Nominal).ToList();
        var never = (ClusterPair pair) => Task.FromResult(new ClusterDecision(pair, byId[pair.A].Surfaces[0], byId[pair.B].Surfaces[0], ClusterRelations.Different,
            new Dictionary<string, int>(), null, null, null, "not in one block: never asked"));
        var (groups, notes) = await CorefStage2.MergeAsync(named, decisions, never);

        // 3. A block that was split: its merged groups once more, each as one group. The answer for two groups stands for every
        // pair of clusters between them.
        // They are asked side by side, through the same gate as every other call of the stage, and applied in their order.
        var regrouped = 0;
        var again = new List<(List<int> Reps, Dictionary<int, List<int>> Members, List<Cluster> Asked, int Block)>();
        foreach (var component in split)
        {
            var reps = Collapse(component, groups);
            if (reps is null || reps.Count > maxGroups) { progress?.Invoke(Interlocked.Increment(ref done), planned); continue; }
            var members = reps.ToDictionary(r => r, r => groups.First(g => g.Contains(r)));
            again.Add((reps, members, reps.Select(r => byId[r] with { Mentions = members[r].SelectMany(id => byId[id].Mentions).OrderBy(m => m.CharStart).ToList() }).ToList(), blocks.Count + ++regrouped));
        }
        var regroupAnswers = await StageRunner.ForEachAsync(again, (r, _) => Ask(new Block(r.Block, r.Reps, "regroup"), r.Asked));
        for (var i = 0; i < again.Count; i++)
        {
            var members = again[i].Members;
            var answers = regroupAnswers[i];
            foreach (var d in answers)
                foreach (var x in members[d.Pair.A])
                    foreach (var y in members[d.Pair.B])
                    {
                        var pair = new ClusterPair(Math.Min(x, y), Math.Max(x, y), "regroup");
                        decisions.RemoveAll(e => e.Pair.A == pair.A && e.Pair.B == pair.B && e.Relation == ClusterRelations.Different);
                        if (!decisions.Any(e => e.Pair.A == pair.A && e.Pair.B == pair.B)) decisions.Add(d with { Pair = pair, NameA = byId[pair.A].Surfaces[0], NameB = byId[pair.B].Surfaces[0] });
                    }
        }
        if (regrouped > 0) (groups, notes) = await CorefStage2.MergeAsync(named, decisions, never);

        // Noun-phrase chains: each with the named groups it shares the most paragraphs with, in one call.
        var groupMentions = groups.Select(g => (Group: g, Mentions: g.SelectMany(id => byId[id].Mentions).OrderBy(m => m.CharStart).ToList())).ToList();
        var paragraphGroups = new Dictionary<int, HashSet<int>>();
        for (var gi = 0; gi < groupMentions.Count; gi++)
        {
            if (byId[groupMentions[gi].Group[0]].Type != MentionType.Character) continue;
            foreach (var m in groupMentions[gi].Mentions)
                if (passages.ParagraphOf(m.CharStart) is { } p)
                    (paragraphGroups.TryGetValue(p.CharStart, out var set) ? set : paragraphGroups[p.CharStart] = []).Add(gi);
        }
        var nominalCalls = 0;
        var attached = new Dictionary<int, List<Cluster>>();
        var nominalResults = await StageRunner.ForEachAsync(nominals, async (nominal, i) =>
        {
            var counts = new Dictionary<int, int>();
            foreach (var m in nominal.Mentions)
                if (passages.ParagraphOf(m.CharStart) is { } p && paragraphGroups.TryGetValue(p.CharStart, out var here))
                    foreach (var gi in here) counts[gi] = counts.GetValueOrDefault(gi) + 1;
            var candidates = counts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).Take(settings.NominalCandidates).Select(kv => kv.Key).ToList();
            if (candidates.Count == 0) { progress?.Invoke(Interlocked.Increment(ref done), planned); return (nominal, Candidates: candidates, Decisions: new List<ClusterDecision>()); }
            Interlocked.Increment(ref nominalCalls);
            var shown = candidates.Select(gi => byId[groupMentions[gi].Group[0]] with { Mentions = groupMentions[gi].Mentions }).Append(nominal).ToList();
            var answers = await Ask(new Block(blocks.Count + regrouped + i + 1, shown.Select(c => c.Id).ToList(), "noun-phrase"), shown);
            return (nominal, Candidates: candidates, Decisions: answers.Where(d => d.Pair.A == nominal.Id || d.Pair.B == nominal.Id).ToList());
        });
        foreach (var (nominal, candidates, answers) in nominalResults)
        {
            decisions.AddRange(answers);
            var same = candidates.Where(gi => answers.Any(d => d.Relation == ClusterRelations.Same && (d.Pair.A == groupMentions[gi].Group[0] || d.Pair.B == groupMentions[gi].Group[0]))).ToList();
            if (same.Count == 1) (attached.TryGetValue(same[0], out var list) ? list : attached[same[0]] = []).Add(nominal);
            else if (same.Count > 1)
                notes.Add($"\"{nominal.Surfaces[0]}\" was voted the same as {same.Count} characters ({string.Join(", ", same.Select(gi => $"\"{byId[groupMentions[gi].Group[0]].Surfaces[0]}\""))}); attached to none");
        }

        // Entities, in the order of their first named mention.
        var built = groupMentions.Select((g, gi) =>
        {
            var extra = attached.TryGetValue(gi, out var list) ? list : [];
            var mentions = g.Mentions.Concat(extra.SelectMany(c => c.Mentions)).OrderBy(m => m.CharStart).ToList();
            var ids = g.Group.ToHashSet();
            var voted = decisions.Where(d => d.Relation == ClusterRelations.Same && ids.Contains(d.Pair.A) && ids.Contains(d.Pair.B)).Select(d => d.CanonicalName);
            return (Clusters: g.Group.Concat(extra.Select(c => c.Id)).Order().ToArray(), Type: byId[g.Group[0]].Type, Mentions: mentions,
                Canonical: CorefStage2.CanonicalName(g.Mentions, voted), First: g.Mentions[0].CharStart);
        }).OrderBy(e => e.First).ToList();
        var entities = CorefStage2.UniqueNames(built.Select((e, i) => new Entity(i + 1, e.Canonical, e.Type,
            e.Mentions.Select(m => m.Text).Distinct().OrderBy(a => a, StringComparer.Ordinal).ToList(), e.Mentions, e.First, e.Clusters)).ToList());

        var entityOf = entities.SelectMany(e => e.Groups.Select(c => (Cluster: c, e.Id))).ToDictionary(x => x.Cluster, x => x.Id);
        var identities = decisions.Where(d => d.Relation == ClusterRelations.IdentityReveal && d.QuoteMatch is not null && entityOf.ContainsKey(d.Pair.A) && entityOf.ContainsKey(d.Pair.B))
            .Select(d => (A: entityOf[d.Pair.A], B: entityOf[d.Pair.B], d))
            .Where(x => x.A != x.B)
            .GroupBy(x => (Math.Min(x.A, x.B), Math.Max(x.A, x.B)))
            .Select(g => g.OrderBy(x => x.d.QuoteMatch!.CharStart).First())
            .Select(x => new IdentityReveal(Math.Min(x.A, x.B), Math.Max(x.A, x.B), x.d.Quote!, x.d.QuoteMatch!.CharStart, x.d.QuoteMatch.CharEnd,
                x.d.VotesByRelation.GetValueOrDefault(ClusterRelations.IdentityReveal)))
            .OrderBy(r => r.CharStart).ToList();
        foreach (var note in notes)
            run?.AppendFailure(new { type = FailureTags.MergeConflict, stage = CorefStage2.StageName, runId = run.RunId, bookId = stage0.BookId, detail = note });

        var stats = new BlockStats(CorefStage2.Candidates(clusters, settings).Count, blocks.Count + regrouped, calls, blocks.Sum(b => b.Clusters.Count * (b.Clusters.Count - 1) / 2),
            blocks.Count == 0 ? 0 : blocks.Max(b => b.Clusters.Count), split.Count, nominalCalls, runner.Peak);
        return (new CorefResult(stage0.BookId, clusters, decisions, entities, identities, notes, settings, chains.Count, aligned, flagged), stats);
    }
}
