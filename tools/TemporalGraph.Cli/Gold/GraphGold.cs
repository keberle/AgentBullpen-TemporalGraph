using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TemporalGraph.Cli.Indexing;
using TemporalGraph.Cli.Runs;
using TemporalGraph.Data;

namespace TemporalGraph.Cli.Gold;

// The facts gold in the shape of the full schema. The human-marked review files (ch01-03.facts.review.md and its
// supplement, imported as ch01-03.facts.json) are edge-shaped: Fact and Event nodes linked by STATES / ABOUT /
// PARTICIPATED_IN / LOCATED_AT, and MET / RELATED_TO / LOCATED_AT edges. Code converts them; no mark is changed and nothing
// is added:
//   - a Fact node → a gold Fact: its text, its subjects (the ABOUT entities), its evidence. A Fact with a STATES link is a
//     claim someone makes: its discovery mode is the obvious one, `told`, by that speaker. A Fact without one has no
//     obvious mode (the old shape did not record how a fact was come by).
//   - an Event node → a gold Event: its participants (PARTICIPATED_IN) and place (LOCATED_AT).
//   - a MET edge → a gold Event with the two as participants (a meeting is an Event; there is no MET edge).
//   - RELATED_TO and LOCATED_AT edges stay edges.
// Rejected lines are converted the same way and stay rejected.

/// <param name="How">The obvious discovery mode: <c>told</c> for a claim (the old STATES link); null when the gold gives none.</param>
/// <param name="Speaker">For a claim: who states it.</param>
public sealed record GoldGraphFact(string Number, string Text, IReadOnlyList<string> Subjects, string Quote, int EvidenceStart, int EvidenceEnd, int RevealedChapter,
    string Status, string? How, string? Speaker);

/// <param name="From">"event" (an Event node) or "met" (a MET edge).</param>
public sealed record GoldGraphEvent(string Number, string Summary, IReadOnlyList<string> Participants, string? Location, string Quote, int EvidenceStart, int EvidenceEnd,
    int RevealedChapter, string Status, string From);

public sealed record GoldGraphRelation(string Number, string Type, string Source, string Target, string? Kind, string Quote, int EvidenceStart, int EvidenceEnd,
    int RevealedChapter, string Status);

public sealed record GoldGraph(string Book, IReadOnlyList<GoldGraphFact> Facts, IReadOnlyList<GoldGraphEvent> Events, IReadOnlyList<GoldGraphRelation> Relations,
    IReadOnlyList<GoldGraphFact> RejectedFacts, IReadOnlyList<GoldGraphEvent> RejectedEvents, IReadOnlyList<GoldGraphRelation> RejectedRelations);

public static partial class GraphGold
{
    public const string Suffix = ".graph.json";

    /// <summary>gold/&lt;book&gt;/ch01-03.facts.json → gold/&lt;book&gt;/ch01-03.graph.json.</summary>
    public static string PathFor(string factsGoldPath) =>
        factsGoldPath.EndsWith(".facts.json", StringComparison.Ordinal) ? factsGoldPath[..^".facts.json".Length] + Suffix : factsGoldPath + Suffix;

    private static GoldFact Item(JsonNode x) => new(
        x["number"]!.GetValue<string>(), x["kind"]!.GetValue<string>(), x["type"]!.GetValue<string>(), x["relationKind"]?.GetValue<string>(),
        x["source"]?.GetValue<string>(), x["target"]?.GetValue<string>(), x["text"]?.GetValue<string>(),
        x["links"]!.AsArray().Select(l => new GoldLink(l!["type"]!.GetValue<string>(), l["entity"]!.GetValue<string>())).ToList(),
        x["evidence"]!["quote"]!.GetValue<string>(), x["revealedChapter"]!.GetValue<int>(),
        x["story"]?["from"]?.GetValue<string>(), x["story"]?["until"]?.GetValue<string>(), x["status"]?.GetValue<string>() ?? "rejected", 0,
        x["evidence"]!["charStart"]!.GetValue<int>(), x["evidence"]!["charEnd"]!.GetValue<int>());

    /// <summary>The conversion of one list of edge-shaped gold items (see the notes at the top of the file).</summary>
    public static (List<GoldGraphFact> Facts, List<GoldGraphEvent> Events, List<GoldGraphRelation> Relations) Convert(IEnumerable<GoldFact> items, string? status = null)
    {
        var facts = new List<GoldGraphFact>();
        var events = new List<GoldGraphEvent>();
        var relations = new List<GoldGraphRelation>();
        foreach (var g in items)
        {
            var s = status ?? g.Status;
            if (g.Kind == "node" && g.Type == Ontology.Fact)
            {
                var speaker = g.Links.FirstOrDefault(l => l.Type == Ontology.States)?.Entity;
                facts.Add(new GoldGraphFact(g.Number, g.Text!, g.Links.Where(l => l.Type == Ontology.About).Select(l => l.Entity).Distinct().ToList(),
                    g.Quote, g.EvidenceStart, g.EvidenceEnd, g.RevealedChapter, s, speaker is null ? null : DiscoveryModes.Told, speaker));
            }
            else if (g.Kind == "node")
                events.Add(new GoldGraphEvent(g.Number, g.Text!, g.Links.Where(l => l.Type == Ontology.ParticipatedIn).Select(l => l.Entity).Distinct().ToList(),
                    g.Links.FirstOrDefault(l => l.Type == Ontology.LocatedAt)?.Entity, g.Quote, g.EvidenceStart, g.EvidenceEnd, g.RevealedChapter, s, "event"));
            else if (g.Type == Ontology.Met)
                events.Add(new GoldGraphEvent(g.Number, $"{g.Source} and {g.Target} meet.", [g.Source!, g.Target!], null, g.Quote, g.EvidenceStart, g.EvidenceEnd, g.RevealedChapter, s, "met"));
            else
                relations.Add(new GoldGraphRelation(g.Number, g.Type, g.Source!, g.Target!, g.RelationKind, g.Quote, g.EvidenceStart, g.EvidenceEnd, g.RevealedChapter, s));
        }
        return (facts, events, relations);
    }

    /// <summary>Converts a facts gold JSON (from <c>gold import</c>) and writes the graph gold next to it. Returns the converted gold.</summary>
    public static GoldGraph ConvertFile(string factsGoldPath, string outPath)
    {
        var bytes = File.ReadAllBytes(factsGoldPath);
        var json = JsonNode.Parse(Encoding.UTF8.GetString(bytes))!;
        var (facts, events, relations) = Convert(json["items"]!.AsArray().Select(x => Item(x!)));
        var (rf, re, rr) = Convert((json["rejected"]?.AsArray() ?? []).Select(x => Item(x!)), "rejected");
        var gold = new GoldGraph(json["book"]!.GetValue<string>(), facts, events, relations, rf, re, rr);
        var file = new
        {
            book = gold.Book,
            shape = GraphStage3.Shape,
            source = new { file = Path.GetFileName(factsGoldPath), sha256 = System.Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)), reviewFiles = new[] { json["source"]?["file"]?.GetValue<string>(), json["supplement"]?["file"]?.GetValue<string>() } },
            convertedAt = DateTimeOffset.UtcNow,
            note = "Converted by code from the edge-shaped facts gold (gold convert); the accept/reject marks are the review files'. Nothing was added.",
            counts = new
            {
                facts = facts.Count, factsWithObviousMode = facts.Count(f => f.How is not null), events = events.Count, eventsFromMet = events.Count(e => e.From == "met"),
                relations = relations.Count, rejected = rf.Count + re.Count + rr.Count,
            },
            facts, events, relations,
            rejected = new { facts = rf, events = re, relations = rr },
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        File.WriteAllText(outPath, JsonSerializer.Serialize(file, RunFolder.Json) + "\n");
        return gold;
    }

    public static GoldGraph Load(string path)
    {
        var json = JsonNode.Parse(File.ReadAllText(path))!;
        T[] Read<T>(JsonNode? node) => node is null ? [] : node.Deserialize<T[]>(RunFolder.Json) ?? [];
        return new GoldGraph(json["book"]!.GetValue<string>(), Read<GoldGraphFact>(json["facts"]), Read<GoldGraphEvent>(json["events"]), Read<GoldGraphRelation>(json["relations"]),
            Read<GoldGraphFact>(json["rejected"]?["facts"]), Read<GoldGraphEvent>(json["rejected"]?["events"]), Read<GoldGraphRelation>(json["rejected"]?["relations"]));
    }

    // ---- The perception candidates, for marking later --------------------------------------------------------------------

    public const string PerceptionsHeading = "## Perceptions the text shows (extracted and revised)";
    public const string SeededHeading = "## Seeded perceptions (code, from the DISCOVERED edges)";

    [GeneratedRegex(@"^- \[(x|~|-| )\] #(Q\d+) ", RegexOptions.Multiline)]
    private static partial Regex Mark();

    /// <summary>
    /// The PERCEIVES edges of the chapters as a checklist for a reviewer to mark later (<c>[x]</c> right, <c>[-]</c> wrong). The list is not
    /// gold and is not complete: an unmarked line is never counted wrong.
    /// </summary>
    public static string PerceptionsReview(string title, string chapters, string runId, PerceptionStage5Result stage5, GraphStage3Result stage3,
        IReadOnlyDictionary<int, string> names, int firstChapter, int lastChapter)
    {
        var facts = stage3.Facts.ToDictionary(f => f.Id);
        var events = stage3.Events.ToDictionary(e => e.Id);
        static string Flat(string s) => string.Join(' ', s.Split((char[])[' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries));
        string Name(int id) => names.TryGetValue(id, out var n) ? n : $"#{id}";
        string Line(PerceptionEdge e) =>
            $"- [ ] #{e.Id} · {Name(e.Character)} · {e.Kind} · {(e.Fact is { } f ? $"{f} \"{Flat(facts[f].Text)}\"" : Name(e.TargetCharacter!.Value))}" +
            $" · cause: {e.CauseEvent} \"{Flat(events[e.CauseEvent].Summary)}\"" + (e.Evidence is null ? "" : $" · \"{Flat(e.Evidence)}\"") + $" · ch{e.Chapter}" +
            (e.Source == "seeded" ? $" · seeded ({e.How})" : $" · {e.Source} <!-- {e.Votes} votes -->") + (e.ClosedBy is null ? "" : $" · closed by {e.ClosedBy}");
        var inRange = stage5.Edges.Where(e => e.Chapter >= firstChapter && e.Chapter <= lastChapter).OrderBy(e => e.RevealedPos).ToList();
        var sb = new StringBuilder();
        sb.AppendLine($"# {title} · ch. {chapters} · perception edges for review");
        sb.AppendLine();
        sb.AppendLine($"Book run `{runId}` · generated by the tool, unreviewed. These are **candidates**, not gold: mark a line `[x]` (right) or `[-]` (wrong) when you have looked at it.");
        sb.AppendLine("An unmarked line is never counted wrong: precision is computed over the marked lines only (`index score --graph`).");
        sb.AppendLine("Line: `#id · character · kind · target · cause: event · \"evidence\" · chapter · where it came from`. Kinds: " + string.Join(", ", PerceptionKinds.All) + ".");
        sb.AppendLine();
        sb.AppendLine(PerceptionsHeading);
        sb.AppendLine();
        foreach (var e in inRange.Where(e => e.Source != "seeded")) sb.AppendLine(Line(e));
        sb.AppendLine();
        sb.AppendLine(SeededHeading);
        sb.AppendLine();
        foreach (var e in inRange.Where(e => e.Source == "seeded")) sb.AppendLine(Line(e));
        return sb.ToString();
    }

    /// <summary>The marks of a perceptions review file: edge id → true ([x]), false ([-]); unmarked and edited lines are left out.</summary>
    public static Dictionary<string, bool> PerceptionMarks(string content) =>
        Mark().Matches(content.Replace("\r\n", "\n")).Where(m => m.Groups[1].Value is "x" or "-").GroupBy(m => m.Groups[2].Value).ToDictionary(g => g.Key, g => g.First().Groups[1].Value == "x");
}
