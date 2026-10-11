using System.Text;
using Microsoft.Extensions.AI;
using TemporalGraph.Llm;
using TemporalGraph.Cli.Runs;
using TemporalGraph.Cli.Scoring;
using TemporalGraph.Cli.Telemetry;
using TemporalGraph.Data;

namespace TemporalGraph.Cli.Indexing;

// Stage 3 — facts and edges (skills/extract-facts.md). One structured-output call per chunk, given the chunk and the
// run's resolved entities (Stage 2). The model returns edges between entities and Fact/Event nodes linked to entities.
// Code then checks every item: the type is in the ontology, the endpoints exist and fit the type, and the evidence
// quote is in the chunk (the Stage 1 evidence checker). Rejects go to failures.jsonl. Items found again in the next
// chunk's overlap are dropped.

/// <summary>Structured output of one Stage 3 call. Strict mode: every field required; Kind is string|null.</summary>
public sealed record FactExtraction(List<ExtractedEdge> Edges, List<ExtractedNode> Nodes);

/// <param name="Type">An edge type between two entities: MET, RELATED_TO, LOCATED_AT or IDENTICAL_TO.</param>
/// <param name="Source">Entity id (from the list in the prompt).</param>
/// <param name="Target">Entity id.</param>
/// <param name="Kind">For RELATED_TO: the relation in a word or two; otherwise null.</param>
/// <param name="Evidence">A verbatim quote from the chunk, one sentence or less.</param>
public sealed record ExtractedEdge(string Type, int Source, int Target, string? Kind, string Evidence);

/// <param name="Label">Fact or Event.</param>
/// <param name="Text">The fact or event in a short sentence.</param>
/// <param name="Links">Edges between the node and entities: STATES, ABOUT, PARTICIPATED_IN, LOCATED_AT.</param>
public sealed record ExtractedNode(string Label, string Text, string Evidence, List<ExtractedLink> Links);

/// <param name="Type">STATES (speaker → Fact), ABOUT (node → entity), PARTICIPATED_IN (character → Event), LOCATED_AT (Event → place).</param>
/// <param name="Entity">Entity id.</param>
public sealed record ExtractedLink(string Type, int Entity);

/// <summary>An accepted edge between two Stage 2 entities. Id "E1", "E2", … in book order.</summary>
/// <param name="Votes">How many of the chunk's samples found it (<see cref="Stage3.Vote"/>); 1 without voting.</param>
public sealed record GraphEdge(string Id, string Type, int Source, int Target, string? Kind, int Chunk, int Chapter, string Evidence, EvidenceMatch EvidenceMatch, int Votes = 1);

/// <summary>An accepted Fact or Event node. Id "N1", "N2", … in book order.</summary>
/// <param name="Votes">How many of the chunk's samples found it (<see cref="Stage3.Vote"/>); 1 without voting.</param>
/// <param name="AlsoEvidence">The evidence spans ([start, end]) of later near-identical nodes merged into this one by the
/// run-wide dedupe (<see cref="RunDedupe"/>); null when none.</param>
public sealed record GraphNode(string Id, string Label, string Text, int Chunk, int Chapter, string Evidence, EvidenceMatch EvidenceMatch, IReadOnlyList<ExtractedLink> Links, int Votes = 1,
    IReadOnlyList<int[]>? AlsoEvidence = null);

/// <param name="Item">"edge", "node" or "link".</param>
/// <param name="Tag">The failure tag (FailureTags).</param>
/// <param name="Sample">The sample (0-based) whose output was rejected.</param>
public sealed record Stage3Reject(string Item, string Tag, string Reason, int Chunk, int Chapter, ExtractedEdge? Edge, ExtractedNode? Node, ExtractedLink? Link, EvidenceMatch? BestMatch, int Sample = 0);

/// <param name="ReturnedEdges">Edges returned by all samples together.</param>
/// <param name="AcceptedEdges">Voted edges with at least MinVotes votes (before the overlap dedupe).</param>
/// <param name="SampleDuplicates">Items a sample returned twice (same match key as one it already returned); counted once.</param>
/// <param name="Unreadable">Samples whose reply couldn't be read (dropped; logged as unreadable reply).</param>
public sealed record Stage3Chunk(int Index, int Chapter, int ReturnedEdges, int ReturnedNodes, int AcceptedEdges, int AcceptedNodes, int Rejected, int SampleDuplicates = 0, int Unreadable = 0);

/// <param name="Edges">Voted edges with at least <paramref name="MinVotes"/> votes, deduped across chunk overlaps, numbered.</param>
/// <param name="EdgePool">Every voted edge of every chunk, whatever its votes, before the dedupe: any m can be re-derived
/// with <see cref="Stage3.WithMinVotes"/> (no new calls). Null in files written before vote pools were kept.</param>
public sealed record Stage3Result(
    string BookId, int FirstChapter, int LastChapter, double EvidenceThreshold, IReadOnlyList<Stage3Chunk> Chunks,
    IReadOnlyList<GraphEdge> Edges, IReadOnlyList<GraphNode> Nodes, IReadOnlyList<Stage3Reject> Rejected, int DuplicatesDropped,
    int Samples = 1, int MinVotes = 1, IReadOnlyList<GraphEdge>? EdgePool = null, IReadOnlyList<GraphNode>? NodePool = null,
    RunDedupeCounts? Dedupe = null);

/// <summary>An entity as Stage 3 sees it (from stage-2.json).</summary>
public sealed record EntityRef(int Id, string Name, MentionType Type, IReadOnlyList<string> Aliases, IReadOnlyList<(int Start, int End, string Text)> Mentions);

public static class Stage3
{
    public const string StageName = "stage-3";

    /// <summary>Edge types between two entities, and the link types between a Fact/Event node and an entity.</summary>
    public static readonly IReadOnlyList<string> EntityEdgeTypes = [Ontology.Met, Ontology.RelatedTo, Ontology.LocatedAt, Ontology.IdenticalTo];
    public static readonly IReadOnlyList<string> LinkTypes = [Ontology.States, Ontology.About, Ontology.ParticipatedIn, Ontology.LocatedAt];

    public static IReadOnlyList<EntityRef> Entities(Stage2Result stage2) =>
        stage2.Entities.Select(e => new EntityRef(e.Id, e.CanonicalName, e.Type, e.Aliases.Where(a => a != e.CanonicalName).ToList(),
            e.Mentions.Select(m => (m.CharStart, m.CharEnd, m.Text)).ToList())).ToList();

    /// <summary>The entity list in the prompt: every entity of the run, those named in this chunk marked, with the names used there.</summary>
    public static string ChunkPrompt(ChunkInfo chunk, string chunkText, IReadOnlyList<EntityRef> entities)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Chunk {chunk.Index} (chapter {chunk.Chapter}).");
        sb.AppendLine("Entities (id · type · name · other names; \"named here\" lists the names used in this chunk):");
        foreach (var e in entities)
        {
            var here = e.Mentions.Where(m => m.Start >= chunk.CharStart && m.End <= chunk.CharEnd).Select(m => m.Text).Distinct().ToList();
            var line = $"{e.Id} · {e.Type} · {e.Name}";
            if (e.Aliases.Count > 0) line += " · " + string.Join(", ", e.Aliases);
            if (here.Count > 0) line += $" · named here: {string.Join(", ", here)}";
            sb.AppendLine(line);
        }
        sb.Append($"Return the edges and the Fact/Event nodes in this chunk.\n<<<\n{chunkText}\n>>>");
        return sb.ToString();
    }

    public static async Task<Stage3Result> RunAsync(
        IChatClient client, LlmCallContext? context, SkillFile skill, Stage0Result stage0, string bookText, IReadOnlyList<EntityRef> entities,
        int firstChapter, int lastChapter, double threshold, RunFolder? run, CancellationToken ct, VotingSettings? voting = null,
        double dedupeThreshold = RunDedupe.DefaultThreshold)
    {
        voting ??= new VotingSettings();
        if (voting.Validate() is [var problem, ..]) throw new ArgumentException(problem.Replace("Stage1", "Stage3"), nameof(voting));
        var chunks = stage0.Chunks.Where(c => c.Chapter >= firstChapter && c.Chapter <= lastChapter).ToList();
        var summaries = new List<Stage3Chunk>();
        var edges = new List<GraphEdge>();
        var nodes = new List<GraphNode>();
        var rejected = new List<Stage3Reject>();

        foreach (var chunk in chunks)
        {
            using var span = IndexingTelemetry.Source.StartActivity($"chunk {chunk.Index}");
            span?.SetTag(IndexingTelemetry.RunId, run?.RunId);
            span?.SetTag(IndexingTelemetry.Stage, StageName);
            span?.SetTag(IndexingTelemetry.BookId, stage0.BookId);
            span?.SetTag(IndexingTelemetry.Chapter, chunk.Chapter);
            span?.SetTag(IndexingTelemetry.ChunkIndex, chunk.Index);
            span?.SetTag(IndexingTelemetry.SkillName, skill.Name);
            span?.SetTag(IndexingTelemetry.SkillVersion, skill.Version);
            if (context is not null)
            {
                context.BookId = stage0.BookId;
                context.Chapter = chunk.Chapter;
                context.ChunkIndex = chunk.Index;
                context.SkillName = skill.Name;
                context.SkillVersion = skill.Version.ToString();
            }

            var chunkText = bookText[chunk.CharStart..chunk.CharEnd];
            List<ChatMessage> messages =
            [
                new(ChatRole.System, skill.Text),
                new(ChatRole.User, ChunkPrompt(chunk, chunkText, entities)),
            ];
            // k independent samples of the same request, in parallel; each is checked, then they vote.
            // A reply that can't be read (cut off) drops that sample; the others vote.
            var replies = await Task.WhenAll(Enumerable.Range(0, voting.Samples).Select(async _ =>
            {
                try { return ((FactExtraction?)(await client.GetResponseAsync<FactExtraction>(messages, Stage1.Json, Stage1.StrictOptions(), useJsonSchemaResponseFormat: true, ct)).Result, (string?)null); }
                catch (System.Text.Json.JsonException ex) { return (null, ex.Message); }
            }));
            foreach (var (_, error) in replies.Where(r => r.Item1 is null))
                run?.AppendFailure(new { type = FailureTags.UnreadableReply, stage = StageName, runId = run.RunId, bookId = stage0.BookId, chapter = chunk.Chapter, chunkIndex = chunk.Index, reason = error, traceId = span?.TraceId.ToString() });
            var outputs = replies.Where(r => r.Item1 is not null).Select(r => r.Item1!).ToList();
            span?.SetTag("samples.unreadable", replies.Length - outputs.Count);
            var checkedSamples = outputs.Select((o, s) => Check(o, chunk, chunkText, entities, threshold) is var c
                ? c with { Rejected = c.Rejected.Select(r => r with { Sample = s }).ToList() } : null!).ToList();
            var (votedEdges, votedNodes, sampleDuplicates) = Vote(checkedSamples);
            edges.AddRange(votedEdges);
            nodes.AddRange(votedNodes);
            var chunkRejected = checkedSamples.SelectMany(c => c.Rejected).ToList();
            rejected.AddRange(chunkRejected);
            foreach (var r in chunkRejected)
                run?.AppendFailure(new
                {
                    type = r.Tag,
                    stage = StageName,
                    runId = run.RunId,
                    bookId = stage0.BookId,
                    chapter = chunk.Chapter,
                    chunkIndex = chunk.Index,
                    item = r.Item,
                    edge = r.Edge,
                    node = r.Node,
                    link = r.Link,
                    sample = r.Sample,
                    reason = r.Reason,
                    bestMatch = r.BestMatch,
                    threshold = r.Tag == FailureTags.QuoteMismatch ? threshold : (double?)null,
                    traceId = span?.TraceId.ToString(),
                });

            var summary = new Stage3Chunk(chunk.Index, chunk.Chapter, outputs.Sum(o => o.Edges.Count), outputs.Sum(o => o.Nodes.Count),
                votedEdges.Count(e => e.Votes >= voting.MinVotes), votedNodes.Count(n => n.Votes >= voting.MinVotes), chunkRejected.Count, sampleDuplicates, replies.Length - outputs.Count);
            summaries.Add(summary);
            span?.SetTag("edges.returned", summary.ReturnedEdges);
            span?.SetTag("nodes.returned", summary.ReturnedNodes);
            span?.SetTag("edges.accepted", summary.AcceptedEdges);
            span?.SetTag("nodes.accepted", summary.AcceptedNodes);
            span?.SetTag("items.rejected", summary.Rejected);
            span?.SetTag("stage3.samples", voting.Samples);
        }

        var (keptEdges, keptNodes, dropped) = Dedupe(edges.Where(e => e.Votes >= voting.MinVotes).ToList(), nodes.Where(n => n.Votes >= voting.MinVotes).ToList());
        var result = new Stage3Result(stage0.BookId, firstChapter, lastChapter, threshold, summaries, keptEdges, keptNodes, rejected, dropped,
            voting.Samples, voting.MinVotes, edges, nodes);
        // run-wide de-duplication (MET once per pair, RELATED_TO per pair and kind, LOCATED_AT per source and target,
        // near-identical Fact/Event nodes merged), logged as "duplicate".
        var (deduped, merges, _) = RunDedupe.Apply(result, dedupeThreshold);
        if (run is not null)
            foreach (var m in merges) run.AppendFailure(RunDedupe.FailureLine(m, run.RunId, stage0.BookId));
        return deduped;
    }

    private static bool Overlap(EvidenceMatch a, EvidenceMatch b) => a.CharStart < b.CharEnd && b.CharStart < a.CharEnd;

    /// <summary>The entities a node is linked to (link types ignored), for the match rule.</summary>
    public static IReadOnlySet<int> LinkedEntities(GraphNode n) => n.Links.Select(l => l.Entity).ToHashSet();

    /// <summary>
    /// The match rule (facts are matched by evidence, not wording). Two edges are the same item when they have the
    /// same type, the same endpoints (MET / IDENTICAL_TO are stored lower id first, so either order) and overlapping
    /// evidence spans; the RELATED_TO kind is ignored. Two nodes are the same item when they have the same label, the same
    /// linked entities (link types ignored) and overlapping evidence spans; the wording is ignored.
    /// </summary>
    public static bool SameItem(GraphEdge a, GraphEdge b) =>
        a.Type == b.Type && a.Source == b.Source && a.Target == b.Target && Overlap(a.EvidenceMatch, b.EvidenceMatch);

    public static bool SameItem(GraphNode a, GraphNode b) =>
        a.Label == b.Label && LinkedEntities(a).SetEquals(LinkedEntities(b)) && Overlap(a.EvidenceMatch, b.EvidenceMatch);

    /// <summary>
    /// Self-consistency voting over one chunk's samples (each already checked), with <see cref="SameItem(GraphEdge, GraphEdge)"/>
    /// as the match rule. Samples are taken in order: an item joins the first cluster it matches; if that cluster already
    /// has an item from the same sample, the item is a repeat within the sample and is dropped (counted). Votes = the number
    /// of samples in the cluster. A cluster's item is its most common evidence span (ties: the earliest sample), with that
    /// member's text and links; an edge's kind is the cluster's most common kind (ties: the earliest). Nothing is dropped
    /// for votes here.
    /// </summary>
    public static (List<GraphEdge> Edges, List<GraphNode> Nodes, int SampleDuplicates) Vote(IReadOnlyList<CheckedChunk> samples)
    {
        var duplicates = 0;
        List<List<(int Sample, T Item)>> Clusters<T>(Func<CheckedChunk, IEnumerable<T>> items, Func<T, T, bool> same)
        {
            var clusters = new List<List<(int Sample, T Item)>>();
            for (var s = 0; s < samples.Count; s++)
                foreach (var item in items(samples[s]))
                {
                    var target = clusters.FirstOrDefault(c => c.Any(x => same(x.Item, item)));
                    if (target is null) clusters.Add([(s, item)]);
                    else if (target.Any(x => x.Sample == s)) duplicates++;
                    else target.Add((s, item));
                }
            return clusters;
        }
        static (int, int) Span(EvidenceMatch m) => (m.CharStart, m.CharEnd);
        static T Representative<T>(List<(int Sample, T Item)> c, Func<T, EvidenceMatch> match) =>
            c.GroupBy(x => Span(match(x.Item))).OrderByDescending(g => g.Count()).ThenBy(g => g.Min(x => x.Sample)).First().OrderBy(x => x.Sample).First().Item;

        var edgeClusters = Clusters<GraphEdge>(c => c.Edges.OrderBy(e => e.EvidenceMatch.CharStart), SameItem);
        var nodeClusters = Clusters<GraphNode>(c => c.Nodes.OrderBy(n => n.EvidenceMatch.CharStart), SameItem);
        var edges = edgeClusters.Select(c =>
        {
            var kind = c.Select(x => x.Item.Kind).GroupBy(k => k?.ToLowerInvariant()).OrderByDescending(g => g.Count()).First().First();
            return Representative(c, e => e.EvidenceMatch) with { Kind = kind, Votes = c.Count };
        }).OrderBy(e => e.EvidenceMatch.CharStart).ToList();
        var nodes = nodeClusters.Select(c => Representative(c, n => n.EvidenceMatch) with { Votes = c.Count }).OrderBy(n => n.EvidenceMatch.CharStart).ToList();
        return (edges, nodes, duplicates);
    }

    /// <summary>
    /// The same Stage 3 output at another m: the pool's items with at least <paramref name="minVotes"/> votes, deduped and
    /// renumbered. Requires a pool (files that keep one) and m ≤ the samples taken.
    /// </summary>
    public static Stage3Result WithMinVotes(Stage3Result r, int minVotes)
    {
        if (r.EdgePool is null || r.NodePool is null) throw new InvalidOperationException("This stage-3.json has no vote pool (written before vote pools were kept); it can't be re-filtered.");
        if (minVotes < 1 || minVotes > r.Samples) throw new ArgumentOutOfRangeException(nameof(minVotes), $"--min-votes must be between 1 and the run's samples ({r.Samples}).");
        var (edges, nodes, dropped) = Dedupe(r.EdgePool.Where(e => e.Votes >= minVotes).ToList(), r.NodePool.Where(n => n.Votes >= minVotes).ToList());
        var chunks = r.Chunks.Select(c => c with
        {
            AcceptedEdges = r.EdgePool.Count(e => e.Chunk == c.Index && e.Votes >= minVotes),
            AcceptedNodes = r.NodePool.Count(n => n.Chunk == c.Index && n.Votes >= minVotes),
        }).ToList();
        return r with { MinVotes = minVotes, Edges = edges, Nodes = nodes, DuplicatesDropped = dropped, Chunks = chunks };
    }

    public sealed record CheckedChunk(List<GraphEdge> Edges, List<GraphNode> Nodes, List<Stage3Reject> Rejected);

    /// <summary>
    /// The code checks on one chunk's output, in order: the type or label is in the ontology (and in this pass), the
    /// endpoints are known entities whose types fit, then the evidence quote matches the chunk (exact, or fuzzy at or
    /// above <paramref name="threshold"/>). A node keeps its valid links and is rejected only when none is left.
    /// MET and IDENTICAL_TO are symmetric: their endpoints are stored lower id first. Ids are assigned later.
    /// </summary>
    public static CheckedChunk Check(FactExtraction output, ChunkInfo chunk, string chunkText, IReadOnlyList<EntityRef> entities, double threshold)
    {
        var byId = entities.ToDictionary(e => e.Id);
        var result = new CheckedChunk([], [], []);
        Stage3Reject Reject(string item, string tag, string reason, ExtractedEdge? e = null, ExtractedNode? n = null, ExtractedLink? l = null, EvidenceMatch? best = null) =>
            new(item, tag, reason, chunk.Index, chunk.Chapter, e, n, l, best);

        string? EvidenceProblem(string evidence, out EvidenceMatch? match)
        {
            match = EvidenceChecker.Find(evidence, chunkText, chunk.CharStart);
            if (match is null) return "empty evidence";
            if (match.Score < threshold) return $"evidence not in chunk (best {match.Method} score {match.Score:0.###} < {threshold})";
            return null;
        }

        foreach (var e in output.Edges)
        {
            if (TypeProblem(e.Type, isLink: false) is { } typeProblem) { result.Rejected.Add(Reject("edge", FailureTags.TypeOutsideOntology, typeProblem, e)); continue; }
            if (e.Type == Ontology.RelatedTo && string.IsNullOrWhiteSpace(e.Kind)) { result.Rejected.Add(Reject("edge", FailureTags.TypeOutsideOntology, "RELATED_TO needs a kind", e)); continue; }
            if (EdgeEndpointProblem(e, byId) is { } endpointProblem) { result.Rejected.Add(Reject("edge", FailureTags.BadEndpoint, endpointProblem, e)); continue; }
            if (EvidenceProblem(e.Evidence, out var match) is { } evidenceProblem) { result.Rejected.Add(Reject("edge", FailureTags.QuoteMismatch, evidenceProblem, e, best: match)); continue; }
            var (source, target) = e.Type is Ontology.Met or Ontology.IdenticalTo && e.Target < e.Source ? (e.Target, e.Source) : (e.Source, e.Target);
            var kind = e.Type == Ontology.RelatedTo ? e.Kind!.Trim() : null;
            result.Edges.Add(new GraphEdge("", e.Type, source, target, kind, chunk.Index, chunk.Chapter, e.Evidence, match!));
        }

        foreach (var n in output.Nodes)
        {
            if (n.Label is not (Ontology.Fact or Ontology.Event))
            {
                var why = n.Label is Ontology.Character or Ontology.Location ? $"{n.Label} nodes come from Stage 2 (entities), not Stage 3" : $"label '{n.Label}' is not in the ontology";
                result.Rejected.Add(Reject("node", FailureTags.TypeOutsideOntology, why, n: n));
                continue;
            }
            if (string.IsNullOrWhiteSpace(n.Text)) { result.Rejected.Add(Reject("node", FailureTags.TypeOutsideOntology, "empty node text", n: n)); continue; }
            var links = new List<ExtractedLink>();
            var linkRejects = new List<Stage3Reject>();
            foreach (var l in n.Links.Distinct())
            {
                if (TypeProblem(l.Type, isLink: true) is { } lt) linkRejects.Add(Reject("link", FailureTags.TypeOutsideOntology, lt, n: n, l: l));
                else if (LinkEndpointProblem(n.Label, l, byId) is { } lp) linkRejects.Add(Reject("link", FailureTags.BadEndpoint, lp, n: n, l: l));
                else links.Add(l);
            }
            if (links.Count == 0)
            {
                result.Rejected.AddRange(linkRejects);
                result.Rejected.Add(Reject("node", FailureTags.BadEndpoint, n.Links.Count == 0 ? "no links to entities" : "no valid links to entities", n: n));
                continue;
            }
            if (EvidenceProblem(n.Evidence, out var match) is { } evidenceProblem)
            {
                result.Rejected.Add(Reject("node", FailureTags.QuoteMismatch, evidenceProblem, n: n, best: match));
                continue;
            }
            result.Rejected.AddRange(linkRejects);
            result.Nodes.Add(new GraphNode("", n.Label, n.Text.Trim(), chunk.Index, chunk.Chapter, n.Evidence, match!, links));
        }
        return result;
    }

    /// <summary>Null when <paramref name="type"/> may be used here; else why not.</summary>
    public static string? TypeProblem(string type, bool isLink)
    {
        if (type == Ontology.Knows) return "KNOWS (character knowledge) is Stage 5, not this pass";
        if (!Ontology.EdgeTypes.Contains(type)) return $"edge type '{type}' is not in the ontology";
        if (isLink && !LinkTypes.Contains(type)) return $"{type} links two entities, not a Fact/Event node and an entity";
        if (!isLink && !EntityEdgeTypes.Contains(type)) return $"{type} links a Fact/Event node and an entity, not two entities";
        return null;
    }

    /// <summary>Null when a node with <paramref name="label"/> may have a <paramref name="linkType"/> link; else why not.</summary>
    public static string? LinkProblemForLabel(string label, string linkType) => (linkType, label) switch
    {
        (Ontology.States, not Ontology.Fact) => "STATES links a speaker to a Fact, not to an Event",
        (Ontology.ParticipatedIn, not Ontology.Event) => "PARTICIPATED_IN links a character to an Event, not to a Fact",
        (Ontology.LocatedAt, not Ontology.Event) => "LOCATED_AT links an Event to a place, not a Fact",
        _ when !LinkTypes.Contains(linkType) => $"'{linkType}' is not a link type",
        _ => null,
    };

    private static string? EdgeEndpointProblem(ExtractedEdge e, IReadOnlyDictionary<int, EntityRef> byId)
    {
        if (!byId.TryGetValue(e.Source, out var s)) return $"unknown source entity {e.Source}";
        if (!byId.TryGetValue(e.Target, out var t)) return $"unknown target entity {e.Target}";
        if (s.Id == t.Id) return "source and target are the same entity";
        return e.Type switch
        {
            Ontology.Met or Ontology.RelatedTo when s.Type != MentionType.Character || t.Type != MentionType.Character => $"{e.Type} links two characters ({s.Type} → {t.Type})",
            Ontology.LocatedAt when t.Type != MentionType.Location => $"LOCATED_AT needs a place as target ({s.Type} → {t.Type})",
            Ontology.IdenticalTo when s.Type != t.Type => $"IDENTICAL_TO links two entities of the same type ({s.Type} → {t.Type})",
            _ => null,
        };
    }

    private static string? LinkEndpointProblem(string label, ExtractedLink l, IReadOnlyDictionary<int, EntityRef> byId)
    {
        if (!byId.TryGetValue(l.Entity, out var e)) return $"unknown entity {l.Entity}";
        return l.Type switch
        {
            Ontology.States when label != Ontology.Fact => "STATES links a speaker to a Fact, not to an Event",
            Ontology.States when e.Type != MentionType.Character => "STATES needs a character as the speaker",
            Ontology.ParticipatedIn when label != Ontology.Event => "PARTICIPATED_IN links a character to an Event, not to a Fact",
            Ontology.ParticipatedIn when e.Type != MentionType.Character => "PARTICIPATED_IN needs a character",
            Ontology.LocatedAt when label != Ontology.Event => "LOCATED_AT links an Event to a place, not a Fact",
            Ontology.LocatedAt when e.Type != MentionType.Location => "LOCATED_AT needs a place",
            _ => null,
        };
    }

    private static bool Overlaps(EvidenceMatch a, EvidenceMatch b) => a.CharStart < b.CharEnd && b.CharStart < a.CharEnd;

    /// <summary>
    /// Chunks overlap, so the next chunk can return an item again. Across different chunks, an item that is the same item
    /// (<see cref="SameItem(GraphEdge, GraphEdge)"/>, the voting match rule) as one from an earlier chunk is a duplicate;
    /// the earlier chunk's item is kept. Items from the same chunk are never dropped here (voting already merged them).
    /// Ids are then assigned in book order: E1… for edges, N1… for nodes.
    /// </summary>
    public static (List<GraphEdge> Edges, List<GraphNode> Nodes, int Dropped) Dedupe(IReadOnlyList<GraphEdge> edges, IReadOnlyList<GraphNode> nodes)
    {
        var dropped = 0;
        var keptEdges = new List<GraphEdge>();
        foreach (var e in edges.OrderBy(e => e.Chunk).ThenBy(e => e.EvidenceMatch.CharStart))
        {
            if (keptEdges.Any(k => k.Chunk != e.Chunk && SameItem(k, e))) { dropped++; continue; }
            keptEdges.Add(e);
        }
        var keptNodes = new List<GraphNode>();
        foreach (var n in nodes.OrderBy(n => n.Chunk).ThenBy(n => n.EvidenceMatch.CharStart))
        {
            if (keptNodes.Any(k => k.Chunk != n.Chunk && SameItem(k, n))) { dropped++; continue; }
            keptNodes.Add(n);
        }
        return (
            keptEdges.OrderBy(e => e.EvidenceMatch.CharStart).ThenBy(e => e.Chunk).Select((e, i) => e with { Id = $"E{i + 1}" }).ToList(),
            keptNodes.OrderBy(n => n.EvidenceMatch.CharStart).ThenBy(n => n.Chunk).Select((n, i) => n with { Id = $"N{i + 1}" }).ToList(),
            dropped);
    }

    /// <summary>Counts for stage-3.json and the stage span.</summary>
    public static Dictionary<string, object> Counts(Stage3Result r)
    {
        var returned = r.Chunks.Sum(c => c.ReturnedEdges + c.ReturnedNodes);
        var quoteRejects = r.Rejected.Count(x => x.Tag == FailureTags.QuoteMismatch);
        var evidenceChecked = returned - r.Rejected.Count(x => x.Item != "link" && x.Tag != FailureTags.QuoteMismatch);
        return new Dictionary<string, object>
        {
            ["chunks"] = r.Chunks.Count,
            ["edgesReturned"] = r.Chunks.Sum(c => c.ReturnedEdges),
            ["nodesReturned"] = r.Chunks.Sum(c => c.ReturnedNodes),
            ["edges"] = r.Edges.Count,
            ["nodes"] = r.Nodes.Count,
            ["edgesByType"] = Stage3.EntityEdgeTypes.ToDictionary(t => t, t => r.Edges.Count(e => e.Type == t)),
            ["nodesByLabel"] = new[] { Ontology.Fact, Ontology.Event }.ToDictionary(l => l, l => r.Nodes.Count(n => n.Label == l)),
            ["linksByType"] = LinkTypes.ToDictionary(t => t, t => r.Nodes.Sum(n => n.Links.Count(l => l.Type == t))),
            ["rejected"] = r.Rejected.Count,
            ["rejectedByReason"] = r.Rejected.GroupBy(x => x.Tag).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count()),
            ["duplicatesDropped"] = r.DuplicatesDropped,
            ["runDedupeRemoved"] = r.Dedupe is { } d ? d.MetRemoved + d.RelatedRemoved + d.LocatedRemoved + d.NodesMerged : 0,
            // Of the returned edges and nodes that reached the evidence check, the share whose quote matched.
            ["evidenceChecked"] = evidenceChecked,
            ["evidenceMatchRate"] = evidenceChecked > 0 ? Math.Round((evidenceChecked - quoteRejects) / (double)evidenceChecked, 4) : 0,
            ["samples"] = r.Samples,
            ["minVotes"] = r.MinVotes,
            ["sampleDuplicates"] = r.Chunks.Sum(c => c.SampleDuplicates),
            ["unreadableReplies"] = r.Chunks.Sum(c => c.Unreadable),
            ["pool"] = (r.EdgePool?.Count ?? r.Edges.Count) + (r.NodePool?.Count ?? r.Nodes.Count),
            // Pool items by votes (1…k), edges and nodes together.
            ["poolByVotes"] = Enumerable.Range(1, r.Samples).ToDictionary(v => v.ToString(),
                v => (r.EdgePool ?? r.Edges).Count(e => e.Votes == v) + (r.NodePool ?? r.Nodes).Count(n => n.Votes == v)),
        };
    }
}
