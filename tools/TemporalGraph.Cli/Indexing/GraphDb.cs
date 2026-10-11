using System.Globalization;
using System.Text.Json;
using TemporalGraph.Cli.Runs;
using TemporalGraph.Data;

namespace TemporalGraph.Cli.Indexing;

/// <summary>What <see cref="GraphDb.Build"/> wrote, for run.json and the report.</summary>
/// <param name="StoryFromLlm">Elements whose story time the model assigned (Events and Facts, the anchor's Event among them).</param>
/// <param name="StoryPropagated">Elements whose story time code derived (every other node and edge, and the Events and Facts the model left).</param>
/// <param name="StoryNull">Elements left without a story time. Must be 0: the build refuses otherwise.</param>
public sealed record GraphDbCounts(IReadOnlyDictionary<string, int> Rows, IReadOnlyDictionary<string, int> Labels, IReadOnlyDictionary<string, int> EdgeTypes,
    IReadOnlyDictionary<string, int> DiscoveredByHow, IReadOnlyDictionary<string, int> PerceivesByKind, int StoryFromLlm, int StoryPropagated, int StoryNull,
    IReadOnlyDictionary<string, int> StoryBySource, int DatedEdges, int Edges, string AnchorEdtf, string AnchorNote);

/// <summary>
/// Writes a run's graph in the full schema into a new book database: Character, Location, Event and Fact nodes; the
/// PARTICIPATED_IN, DISCOVERED, PERCEIVES, MENTIONED_IN, IDENTICAL_TO, LOCATED_AT and RELATED_TO edges; the <c>timeline</c> table;
/// and both clocks on every node and every edge.
/// <para><b>Reader clock.</b> <c>revealed_pos</c> is the start of the element's evidence: a Character's or Location's first mention, an
/// Event's or a Fact's quote, a perception's quote (a seeded one: where its Fact is discovered), an identity's quote. Two rules
/// keep the graph closed under the spoiler rule: a referenced Event is revealed no earlier than its narrating Event, a Fact no
/// earlier than its subjects, and an edge no earlier than its two ends. <c>retracted_pos</c> comes from a Stage 3 retraction (a
/// Fact) or from the edge that closes a perception.</para>
/// <para><b>Story clock (propagation by code).</b> Events and Facts have Stage 4's time. Everything else inherits:
/// PARTICIPATED_IN, MENTIONED_IN and an Event's LOCATED_AT from the Event; DISCOVERED from its Event; PERCEIVES from its
/// <c>cause_event</c>; RELATED_TO and a Character's LOCATED_AT from the narrated Event being told where their evidence stands;
/// a Character or Location from the earliest thing that involves it (its Events, the Facts about it, its edges), so backstory
/// pushes it earlier; IDENTICAL_TO from the earlier of its two Characters (the identity always held). An edge never starts
/// before its two ends. After this nothing has a NULL <c>story_from_edtf</c>; the build throws if anything does.</para>
/// </summary>
public static class GraphDb
{
    private static readonly JsonSerializerOptions Compact = new(RunFolder.Json) { WriteIndented = false };

    private sealed record Clock(int Revealed, int? Retracted, int EvidenceStart, int EvidenceEnd, StoryDate From, StoryDate? Until, double? Fabula);

    private static StoryDate Earlier(StoryDate a, StoryDate b) => string.CompareOrdinal(a.Lo, b.Lo) <= 0 ? a : b;
    private static StoryDate Later(StoryDate a, StoryDate b) => string.CompareOrdinal(a.Lo, b.Lo) >= 0 ? a : b;

    public static GraphDbCounts Build(string path, Stage0Result stage0, Stage2Result stage2, GraphStage3Result stage3, TimelineStage4Result stage4, PerceptionStage5Result stage5)
    {
        string Json(object o) => JsonSerializer.Serialize(o, Compact);
        var times = stage4.Items.ToDictionary(i => i.Id);
        var events = stage3.Events.ToDictionary(e => e.Id);
        var facts = stage3.Facts.ToDictionary(f => f.Id);
        // Canonical names unique per type (CorefStage2 does this itself; a stage-2.json written before that rule gets it here).
        stage2 = stage2 with { Entities = CorefStage2.UniqueNames(stage2.Entities) };
        var entities = stage2.Entities.ToDictionary(e => e.Id);
        var anchor = TimelineStage4.Date(Edtf.Parse(stage4.Anchor.Edtf));
        var narrated = stage3.Events.Where(e => e.Kind == EventKinds.Narrated).OrderBy(e => e.EvidenceMatch.CharStart).ToList();
        // The story time being told at a text position: the narrated Event at or just before it.
        TimedNode? Telling(int pos) => narrated.LastOrDefault(e => e.EvidenceMatch.CharStart <= pos) is { } e ? times[e.Id] : null;

        // ---- Clocks before anything is written ----------------------------------------------------------------------
        var entityRevealed = stage2.Entities.ToDictionary(e => e.Id, e => e.Mentions.Min(m => m.CharStart));
        var eventClock = new Dictionary<string, Clock>();
        foreach (var e in stage3.Events.OrderBy(e => e.Kind == EventKinds.Referenced))
        {
            var revealed = e.MentionedIn is { } n ? Math.Max(e.EvidenceMatch.CharStart, eventClock[n].Revealed) : e.EvidenceMatch.CharStart;
            eventClock[e.Id] = new Clock(revealed, null, e.EvidenceMatch.CharStart, e.EvidenceMatch.CharEnd, times[e.Id].From, times[e.Id].Until, times[e.Id].FabulaSeq);
        }
        var retractions = stage5.FactRetractions.GroupBy(r => r.Fact).ToDictionary(g => g.Key, g => g.Min(r => r.RetractedPos));
        var factClock = new Dictionary<string, Clock>();
        foreach (var f in stage3.Facts)
        {
            var revealed = Math.Max(f.EvidenceMatch.CharStart, f.Subjects.Where(entityRevealed.ContainsKey).Select(s => entityRevealed[s]).DefaultIfEmpty(0).Max());
            // A claim the same passage exposes as false (the retraction's quote stands before the claim's) is retracted as soon as it is revealed.
            int? retracted = retractions.TryGetValue(f.Id, out var r) ? Math.Max(r, revealed + 1) : null;
            factClock[f.Id] = new Clock(revealed, retracted, f.EvidenceMatch.CharStart, f.EvidenceMatch.CharEnd, times[f.Id].From, times[f.Id].Until, times[f.Id].FabulaSeq);
        }

        // Identity reveals: Stage 2's (voted on a pair of name chains) and Stage 3's (a quote in a chunk), one per pair, the earliest.
        var identities = (stage2.Identities ?? []).Select(i => (i.A, i.B, i.CharStart, i.CharEnd, Source: "stage-2"))
            .Concat(stage3.Identities.Select(i => (i.A, i.B, i.EvidenceMatch.CharStart, i.EvidenceMatch.CharEnd, Source: "stage-3")))
            .Where(i => entities.ContainsKey(i.A) && entities.ContainsKey(i.B) && i.A != i.B)
            .GroupBy(i => (Math.Min(i.A, i.B), Math.Max(i.A, i.B))).Select(g => g.OrderBy(i => i.CharStart).First()).ToList();

        // A Character's or Location's story time: the earliest thing that involves it.
        var entityStory = new Dictionary<int, (StoryDate From, double? Fabula)>();
        void Involve(int entity, StoryDate from, double? fabula)
        {
            if (!entities.ContainsKey(entity)) return;
            if (!entityStory.TryGetValue(entity, out var have) || string.CompareOrdinal(from.Lo, have.From.Lo) < 0) entityStory[entity] = (from, fabula);
        }
        foreach (var e in stage3.Events)
        {
            foreach (var p in e.Participants) Involve(p.Character, times[e.Id].From, times[e.Id].FabulaSeq);
            if (e.Location is { } loc) Involve(loc, times[e.Id].From, times[e.Id].FabulaSeq);
        }
        foreach (var f in stage3.Facts)
            foreach (var s in f.Subjects) Involve(s, times[f.Id].From, times[f.Id].FabulaSeq);
        foreach (var p in stage5.Edges)
        {
            Involve(p.Character, p.From, p.FabulaSeq);
            if (p.TargetCharacter is { } t) Involve(t, p.From, p.FabulaSeq);
        }
        foreach (var r in stage3.Relations)
        {
            var telling = Telling(r.EvidenceMatch.CharStart);
            Involve(r.Source, telling?.From ?? anchor, telling?.FabulaSeq);
            Involve(r.Target, telling?.From ?? anchor, telling?.FabulaSeq);
        }
        foreach (var e in stage2.Entities.Where(e => !entityStory.ContainsKey(e.Id)))
        {
            var telling = Telling(entityRevealed[e.Id]);
            entityStory[e.Id] = (telling?.From ?? anchor, telling?.FabulaSeq);
        }
        // The two sides of an identity have always been one person: both start at the earlier one's time.
        foreach (var i in identities)
        {
            var first = string.CompareOrdinal(entityStory[i.A].From.Lo, entityStory[i.B].From.Lo) <= 0 ? entityStory[i.A] : entityStory[i.B];
            entityStory[i.A] = entityStory[i.B] = first;
        }

        // ---- Write ---------------------------------------------------------------------------------------------------
        BookDatabase.Create(path);
        using var db = BookDatabase.OpenReadWrite(path);
        db.Chapters.AddRange(stage0.Chapters.Select(c => new Chapter { Number = c.Number, Title = c.Title, CharStart = c.CharStart, CharEnd = c.CharEnd, Narrator = c.NarratorColumn }));
        db.Positions.AddRange(stage0.Chapters.SelectMany(c => c.Paragraphs).Select(p => new Position { Chapter = p.Chapter, Paragraph = p.Paragraph, CharStart = p.CharStart, CharEnd = p.CharEnd }));

        static void Stamp(Node node, Clock c)
        {
            node.RevealedPos = c.Revealed;
            node.RetractedPos = c.Retracted;
            node.EvidenceStart = c.EvidenceStart;
            node.EvidenceEnd = c.EvidenceEnd;
            node.StoryFromEdtf = c.From.Edtf;
            node.StoryFromLo = c.From.Lo;
            node.StoryFromHi = c.From.Hi;
            node.StoryUntilEdtf = c.Until?.Edtf;
            node.StoryUntilLo = c.Until?.Lo;
            node.StoryUntilHi = c.Until?.Hi;
            node.FabulaSeq = c.Fabula;
        }

        var nodeClock = new Dictionary<Node, Clock>(ReferenceEqualityComparer.Instance);
        var entityNodes = new Dictionary<int, Node>();
        var owners = ShortNames.Owners(stage2.Entities.Where(e => e.Type == MentionType.Character));
        foreach (var e in stage2.Entities)
        {
            var first = e.Mentions.OrderBy(m => m.CharStart).First();
            var aliasFirstPos = e.Mentions.GroupBy(m => m.Text).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Min(m => m.CharStart));
            var shortName = e.Type == MentionType.Character ? ShortNames.Pick(e, owners) : null;
            var node = new Node
            {
                Properties = shortName is null
                    ? Json(new { name = e.CanonicalName, aliases = e.Aliases, stage2Id = e.Id, aliasFirstPos, kind = e.Kind })
                    : Json(new { name = e.CanonicalName, shortName, aliases = e.Aliases, stage2Id = e.Id, aliasFirstPos, kind = e.Kind }),
                Labels = [new NodeLabel { Label = e.Type == MentionType.Character ? Ontology.Character : Ontology.Location }],
            };
            var clock = new Clock(first.CharStart, null, first.CharStart, first.CharEnd, entityStory[e.Id].From, null, entityStory[e.Id].Fabula);
            Stamp(node, clock);
            nodeClock[node] = clock;
            entityNodes[e.Id] = node;
            db.Nodes.Add(node);
            if (e.Type == MentionType.Character)
                db.Characters.Add(new Character { Name = e.CanonicalName, ShortName = shortName, Initials = TemporalGraph.Chat.ChatCharacters.DeriveInitials(shortName!), Node = node });
            else db.Locations.Add(new Location { Name = e.CanonicalName, Node = node });
        }
        var eventNodes = new Dictionary<string, Node>();
        foreach (var e in stage3.Events)
        {
            var node = new Node
            {
                Properties = Json(new { kind = e.Kind, summary = e.Summary, when = e.When, chapter = e.Chapter, stage3Id = e.Id, storySource = times[e.Id].Source }),
                Labels = [new NodeLabel { Label = Ontology.Event }],
            };
            Stamp(node, eventClock[e.Id]);
            nodeClock[node] = eventClock[e.Id];
            eventNodes[e.Id] = node;
            db.Nodes.Add(node);
        }
        db.SaveChanges(); // node ids, for a Fact's subject list and a perception's cause_event

        var factNodes = new Dictionary<string, Node>();
        foreach (var f in stage3.Facts)
        {
            var node = new Node
            {
                Properties = Json(new
                {
                    text = f.Text, subject = f.Subjects.Where(entityNodes.ContainsKey).Select(s => entityNodes[s].Id).ToList(), evidence = f.Evidence,
                    chapter = f.Chapter, stage3Id = f.Id, storySource = times[f.Id].Source, alsoEvidence = f.AlsoEvidence, subjectSource = f.SubjectSource,
                }),
                Labels = [new NodeLabel { Label = Ontology.Fact }],
            };
            Stamp(node, factClock[f.Id]);
            nodeClock[node] = factClock[f.Id];
            factNodes[f.Id] = node;
            db.Nodes.Add(node);
        }

        // An edge with its own clock, never visible before its two ends on either clock.
        void AddEdge(Node source, Node target, string type, object properties, Clock own)
        {
            var (s, t) = (nodeClock[source], nodeClock[target]);
            var from = Later(own.From, Later(s.From, t.From));
            var revealed = Math.Max(own.Revealed, Math.Max(s.Revealed, t.Revealed));
            // Nor does it outlast them: it ends in story time with the first of its ends to end, and is retracted with the first to be retracted.
            var until = new[] { own.Until, s.Until, t.Until }.OfType<StoryDate>().OrderBy(u => u.Lo, StringComparer.Ordinal).FirstOrDefault();
            // A clamp may not leave an interval that ends before it starts, or a retraction before the reveal.
            if (until is not null && string.CompareOrdinal(until.Lo, from.Lo) <= 0) until = from;
            int? retracted = new[] { own.Retracted, s.Retracted, t.Retracted }.Where(r => r is not null).Min() is { } first ? Math.Max(first, revealed + 1) : null;
            db.Edges.Add(new Edge
            {
                Source = source, Target = target, Type = type, Properties = Json(properties),
                RevealedPos = revealed, RetractedPos = retracted, EvidenceStart = own.EvidenceStart, EvidenceEnd = own.EvidenceEnd,
                StoryFromEdtf = from.Edtf, StoryFromLo = from.Lo, StoryFromHi = from.Hi,
                StoryUntilEdtf = until?.Edtf, StoryUntilLo = until?.Lo, StoryUntilHi = until?.Hi,
                FabulaSeq = own.Fabula,
            });
        }

        foreach (var e in stage3.Events)
        {
            var clock = eventClock[e.Id];
            foreach (var p in e.Participants.Where(p => entityNodes.ContainsKey(p.Character)))
                AddEdge(entityNodes[p.Character], eventNodes[e.Id], Ontology.ParticipatedIn, new { role = p.Role, stage3Id = e.Id }, clock);
            if (e.Location is { } loc && entityNodes.TryGetValue(loc, out var place))
                AddEdge(eventNodes[e.Id], place, Ontology.LocatedAt, new { stage3Id = e.Id }, clock);
            if (e.MentionedIn is { } n)
                AddEdge(eventNodes[e.Id], eventNodes[n], Ontology.MentionedIn, new { stage3Id = e.Id }, clock with { From = eventClock[n].From, Until = null, Fabula = eventClock[n].Fabula });
        }
        foreach (var f in stage3.Facts)
            foreach (var d in f.Discoveries)
            {
                var e = eventClock[d.Event];
                AddEdge(eventNodes[d.Event], factNodes[f.Id], Ontology.Discovered,
                    d.Discovery is null ? new { how = d.How, stage3Id = f.Id, votes = d.Votes } : new { how = d.How, stage3Id = f.Id, votes = d.Votes, discovery = d.Discovery },
                    new Clock(Math.Max(e.Revealed, factClock[f.Id].Revealed), null, f.EvidenceMatch.CharStart, f.EvidenceMatch.CharEnd, e.From, null, e.Fabula));
            }
        foreach (var p in stage5.Edges)
        {
            if (!entityNodes.TryGetValue(p.Character, out var holder)) continue;
            var target = p.Fact is { } fact ? factNodes[fact] : entityNodes.GetValueOrDefault(p.TargetCharacter!.Value);
            if (target is null) continue;
            AddEdge(holder, target, Ontology.Perceives, new { kind = p.Kind, causeEvent = eventNodes[p.CauseEvent].Id, source = p.Source, how = p.How, evidence = p.Evidence, stage5Id = p.Id },
                new Clock(p.RevealedPos, p.RetractedPos, p.EvidenceStart, p.EvidenceEnd, p.From, p.Until, p.FabulaSeq));
        }
        foreach (var r in stage3.Relations)
        {
            if (!entityNodes.TryGetValue(r.Source, out var s) || !entityNodes.TryGetValue(r.Target, out var t)) continue;
            var telling = Telling(r.EvidenceMatch.CharStart);
            AddEdge(s, t, r.Type, r.Type == Ontology.RelatedTo ? new { kind = r.Kind, stage3Id = r.Id } : new { kind = (string?)null, stage3Id = r.Id },
                new Clock(r.EvidenceMatch.CharStart, null, r.EvidenceMatch.CharStart, r.EvidenceMatch.CharEnd, telling?.From ?? anchor, null, telling?.FabulaSeq));
        }
        foreach (var i in identities)
        {
            var (a, b) = (entityNodes[Math.Min(i.A, i.B)], entityNodes[Math.Max(i.A, i.B)]);
            AddEdge(a, b, Ontology.IdenticalTo, new { source = i.Source },
                new Clock(i.CharStart, null, i.CharStart, i.CharEnd, Earlier(nodeClock[a].From, nodeClock[b].From), null, nodeClock[a].Fabula));
        }

        // ---- Timeline -----------------------------------------------------------------------------------------
        foreach (var row in stage4.Timeline)
        {
            var isEvent = row.Kind == TimelineKinds.Event;
            db.Timeline.Add(new TimelineRow
            {
                Kind = row.Kind,
                RefId = row.Ref is null ? null : isEvent ? eventNodes[row.Ref].Id : long.Parse(row.Ref, CultureInfo.InvariantCulture),
                Label = row.Label,
                RevealedPos = isEvent ? eventClock[row.Ref!].Revealed : row.RevealedPos,
                RevealedPosEnd = isEvent ? Math.Max(row.RevealedPosEnd ?? 0, eventClock[row.Ref!].Revealed) : row.RevealedPosEnd,
                StoryFromEdtf = row.From?.Edtf, StoryFromLo = row.From?.Lo, StoryFromHi = row.From?.Hi,
                StoryUntilEdtf = row.Until?.Edtf, StoryUntilLo = row.Until?.Lo, StoryUntilHi = row.Until?.Hi,
                FabulaSeq = row.FabulaSeq,
                Note = row.Note,
            });
        }
        db.SaveChanges();

        // ---- The acceptance test of Stage 4: nothing undated ----------------------------------------------------------
        var nullNodes = db.Nodes.Count(n => n.StoryFromEdtf == null || n.StoryFromLo == null || n.RevealedPos == null);
        var nullEdges = db.Edges.Count(e => e.StoryFromEdtf == null || e.StoryFromLo == null || e.RevealedPos == null);
        if (nullNodes + nullEdges > 0)
            throw new InvalidOperationException($"{nullNodes} node(s) and {nullEdges} edge(s) have no story_from_edtf or revealed_pos; every element must have both clocks.");

        var llm = stage4.Items.Count(i => i.Source is "anchor" or "llm" or "llm-coarse");
        var total = db.Nodes.Count() + db.Edges.Count();
        var edgeTypes = db.Edges.GroupBy(e => e.Type).Select(g => new { g.Key, Count = g.Count() }).ToList().OrderBy(x => x.Key, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.Count);
        return new GraphDbCounts(
            new Dictionary<string, int>
            {
                ["chapters"] = db.Chapters.Count(), ["positions"] = db.Positions.Count(), ["nodes"] = db.Nodes.Count(), ["node_labels"] = db.NodeLabels.Count(),
                ["edges"] = db.Edges.Count(), ["timeline"] = db.Timeline.Count(), ["event_order"] = db.EventOrder.Count(), ["characters"] = db.Characters.Count(), ["locations"] = db.Locations.Count(),
            },
            db.NodeLabels.GroupBy(l => l.Label).Select(g => new { g.Key, Count = g.Count() }).ToList().OrderBy(x => x.Key, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.Count),
            edgeTypes,
            DiscoveryModes.All.ToDictionary(h => h, h => stage3.Facts.Sum(f => f.Discoveries.Count(d => d.How == h))),
            PerceptionKinds.All.ToDictionary(k => k, k => stage5.Edges.Count(e => e.Kind == k)),
            llm, total - llm, nullNodes + nullEdges,
            stage4.Items.GroupBy(i => i.Source).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count()),
            db.Edges.Count(e => e.StoryFromEdtf != null), db.Edges.Count(), stage4.Anchor.Edtf, stage4.Anchor.Note);
    }
}
