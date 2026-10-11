using System.Text.Json;
using System.Text.RegularExpressions;
using TemporalGraph.Cli.Indexing;
using TemporalGraph.Cli.Runs;
using TemporalGraph.Data;

namespace TemporalGraph.Cli.Gold;

// The facts gold: the strict parser of a reviewed facts review file (FactsReviewFile's format) and its import into
// gold JSON (<chapters>.facts.json). Every gold item's evidence quote is located in its revealed chapter, so scoring can
// match by evidence span.

/// <summary>A link of a gold Fact/Event: type and the entity's name as written in the review file.</summary>
public sealed record GoldLink(string Type, string Entity);

/// <param name="Number">The line's number without '#' ("12", or "new1" under Missing).</param>
/// <param name="Kind">"edge" or "node".</param>
/// <param name="Type">The edge type, or the node label (Fact / Event).</param>
/// <param name="RelationKind">RELATED_TO's kind, when given.</param>
/// <param name="Quote">The short evidence quote as written (a trailing "…" marks a cut).</param>
/// <param name="StoryFrom">EDTF, or null for "—".</param>
/// <param name="Status">accepted, edited, added or rejected.</param>
public sealed record GoldFact(string Number, string Kind, string Type, string? RelationKind, string? Source, string? Target, string? Text,
    IReadOnlyList<GoldLink> Links, string Quote, int RevealedChapter, string? StoryFrom, string? StoryUntil, string Status, int Line,
    int EvidenceStart = 0, int EvidenceEnd = 0)
{
    /// <summary>The entity names the item involves (edge ends, or node links), distinct.</summary>
    public IReadOnlyList<string> EntityNames => Kind == "edge" ? new[] { Source!, Target! }.Distinct().ToList() : Links.Select(l => l.Entity).Distinct().ToList();
}

public sealed record GoldOrder(string Relation, string A, string B, string Quote, string Status, int Line);

public sealed record ParsedFactsReview(IReadOnlyList<GoldFact> Items, IReadOnlyList<GoldFact> Rejected, IReadOnlyList<GoldOrder> Orders,
    IReadOnlyList<GoldOrder> RejectedOrders, IReadOnlyList<ParseError> Errors);

public static partial class FactsGold
{
    private const string Sep = ReviewFile.Sep;

    [GeneratedRegex(@"^- \[( |x|~|-)\] (.+)$")]
    private static partial Regex CheckLine();

    [GeneratedRegex(@"^#([A-Za-z0-9]+)$")]
    private static partial Regex NumberField();

    [GeneratedRegex(@"^([A-Z_]+)(?: \((.+)\))?$")]
    private static partial Regex EdgeTypeField();

    [GeneratedRegex(@"^""(.*)""$")]
    private static partial Regex Quoted();

    [GeneratedRegex(@"^revealed: ch(\d+)$")]
    private static partial Regex RevealedField();

    [GeneratedRegex(@"(STATES|ABOUT|PARTICIPATED_IN|LOCATED_AT) (←|→) (.+?)(?=, (?:STATES|ABOUT|PARTICIPATED_IN|LOCATED_AT) [←→] |$)")]
    private static partial Regex LinkPattern();

    [GeneratedRegex(@"^#([A-Za-z0-9]+) → #([A-Za-z0-9]+)$")]
    private static partial Regex OrderPair();

    /// <summary>Parses a reviewed facts file. Never guesses: anything it can't read, or an unmarked line, is an error.</summary>
    public static ParsedFactsReview Parse(string content)
    {
        var items = new List<GoldFact>();
        var rejected = new List<GoldFact>();
        var orders = new List<GoldOrder>();
        var rejectedOrders = new List<GoldOrder>();
        var errors = new List<ParseError>();
        string? section = null;
        var raw = content.Replace("\r\n", "\n");
        var (stripped, unclosedAt) = ReviewFile.StripComments(raw);
        var lines = stripped.Split('\n');
        if (unclosedAt is { } open) errors.Add(new ParseError(open, lines[open - 1], "HTML comment '<!--' is never closed with '-->'"));
        // A comment opened on a checklist line must close on it: otherwise it silently hides the lines after it.
        var rawLines = raw.Split('\n');
        for (var i = 0; i < rawLines.Length; i++)
        {
            var l = rawLines[i];
            if (l.StartsWith("- [") && l.LastIndexOf("<!--", StringComparison.Ordinal) is var o and >= 0 && l.IndexOf("-->", o, StringComparison.Ordinal) < 0)
                errors.Add(new ParseError(i + 1, l.Length > 120 ? l[..120] + "…" : l, "HTML comment opened on this checklist line is not closed on it; it would hide the lines after it"));
        }
        for (var i = 0; i < lines.Length; i++)
        {
            var n = i + 1;
            var line = lines[i].TrimEnd();
            if (line.Length == 0) continue;
            if (line.StartsWith("## "))
            {
                section = line switch
                {
                    FactsReviewFile.EdgesHeading => "edges",
                    FactsReviewFile.NodesHeading => "nodes",
                    FactsReviewFile.OrderHeading => "order",
                    FactsReviewFile.MissingHeading => "missing",
                    _ => null,
                };
                if (section is null) errors.Add(new ParseError(n, line, "unknown section heading"));
                continue;
            }
            if (section is null || line.StartsWith("# ") || (line.StartsWith('_') && line.EndsWith('_'))) continue;
            if (!line.StartsWith("- ")) { errors.Add(new ParseError(n, line, "not a checklist line")); continue; }
            var m = CheckLine().Match(line);
            if (!m.Success) { errors.Add(new ParseError(n, line, "expected '- [x] …', '- [~] …', '- [-] …' or '- [ ] …'")); continue; }
            var mark = m.Groups[1].Value;
            if (mark == " ") { errors.Add(new ParseError(n, line, "not reviewed: mark it [x], [~] or [-]")); continue; }
            if (section == "missing" && mark != "x") { errors.Add(new ParseError(n, line, "lines under Missing must be marked [x]")); continue; }
            var status = section == "missing" ? "added" : mark switch { "x" => "accepted", "~" => "edited", _ => "rejected" };
            var body = m.Groups[2].Value;

            var isOrder = section == "order" || (section == "missing" && !body.StartsWith('#'));
            if (isOrder)
            {
                var o = ParseOrder(body, n, status, out var problem);
                if (o is null) errors.Add(new ParseError(n, line, problem!));
                else (status == "rejected" ? rejectedOrders : orders).Add(o);
                continue;
            }
            var item = ParseItem(body, n, status, out var itemProblem);
            if (item is null) { errors.Add(new ParseError(n, line, itemProblem!)); continue; }
            if (section == "edges" && item.Kind != "edge") { errors.Add(new ParseError(n, line, "a Fact/Event line under Edges")); continue; }
            if (section == "nodes" && item.Kind != "node") { errors.Add(new ParseError(n, line, "an edge line under Facts and events")); continue; }
            (status == "rejected" ? rejected : items).Add(item);
        }

        foreach (var g in items.Concat(rejected).GroupBy(x => x.Number).Where(g => g.Count() > 1))
            errors.Add(new ParseError(g.Last().Line, "#" + g.Key, "the same #number twice"));
        var accepted = items.Select(x => x.Number).ToHashSet();
        foreach (var o in orders)
            foreach (var end in new[] { o.A, o.B })
                if (!accepted.Contains(end)) errors.Add(new ParseError(o.Line, $"{o.Relation} · #{o.A} → #{o.B}", $"#{end} is not an accepted, edited or added item"));
        return new ParsedFactsReview(items, rejected, orders, rejectedOrders, errors);
    }

    private static GoldOrder? ParseOrder(string body, int line, string status, out string? problem)
    {
        problem = null;
        var f = body.Split(Sep);
        if (f.Length != 3) { problem = $"an ordering has 3 fields (RELATION · #a → #b · \"evidence\"), found {f.Length}"; return null; }
        if (!TemporalRelations.Ordering.Contains(f[0].Trim())) { problem = $"relation must be one of {string.Join(", ", TemporalRelations.Ordering)}"; return null; }
        var pair = OrderPair().Match(f[1].Trim());
        if (!pair.Success) { problem = "the second field must look like: #12 → #15"; return null; }
        var q = Quoted().Match(f[2].Trim());
        if (!q.Success) { problem = "the evidence must be in double quotes"; return null; }
        return new GoldOrder(f[0].Trim(), pair.Groups[1].Value, pair.Groups[2].Value, q.Groups[1].Value, status, line);
    }

    private static GoldFact? ParseItem(string body, int line, string status, out string? problem)
    {
        problem = null;
        var f = body.Split(Sep).Select(x => x.Trim()).ToList();
        if (f.Count < 2) { problem = "expected '#n · TYPE · …'"; return null; }
        var num = NumberField().Match(f[0]);
        if (!num.Success) { problem = "the first field must be '#number'"; return null; }
        var number = num.Groups[1].Value;
        var isNode = f[1] is Ontology.Fact or Ontology.Event;
        // Edge: #n · TYPE · S → T · "evidence" · revealed · story [· reasoning]. Node: #n · Label · "text" · links: … · "evidence" · revealed · story [· reasoning].
        var needed = isNode ? 7 : 6;
        if (f.Count < needed) { problem = $"expected at least {needed} fields separated by ' · ', found {f.Count}"; return null; }
        var (evidenceField, revealedField, storyField) = isNode ? (f[4], f[5], f[6]) : (f[3], f[4], f[5]);
        if (f.Count > needed && !f[needed].StartsWith("reasoning:")) { problem = $"unexpected field '{f[needed]}' (only 'reasoning: …' may follow the story)"; return null; }

        var q = Quoted().Match(evidenceField);
        if (!q.Success) { problem = "the evidence must be in double quotes"; return null; }
        var rv = RevealedField().Match(revealedField);
        if (!rv.Success) { problem = "expected 'revealed: chN'"; return null; }
        if (!storyField.StartsWith("story:")) { problem = "expected 'story: EDTF', 'story: A until B' or 'story: —'"; return null; }
        if (!ParseStory(storyField["story:".Length..].Trim(), out var from, out var until, out problem)) return null;

        if (isNode)
        {
            var text = Quoted().Match(f[2]);
            if (!text.Success) { problem = "the fact text must be in double quotes"; return null; }
            if (!f[3].StartsWith("links:")) { problem = "expected 'links: TYPE ← name, TYPE → name'"; return null; }
            var linkText = f[3]["links:".Length..].Trim();
            var links = LinkPattern().Matches(linkText).Select(x => (Type: x.Groups[1].Value, Arrow: x.Groups[2].Value, Entity: x.Groups[3].Value.Trim())).ToList();
            if (links.Count == 0 || string.Join(", ", links.Select(l => $"{l.Type} {l.Arrow} {l.Entity}")) != linkText) { problem = "can't read the links (TYPE ← name or TYPE → name, comma-separated)"; return null; }
            foreach (var l in links)
            {
                var expected = l.Type is Ontology.States or Ontology.ParticipatedIn ? "←" : "→";
                if (l.Arrow != expected) { problem = $"{l.Type} is written with {expected}"; return null; }
                if (Stage3.LinkProblemForLabel(f[1], l.Type) is { } lp) { problem = lp; return null; }
            }
            return new GoldFact(number, "node", f[1], null, null, null, text.Groups[1].Value, links.Select(l => new GoldLink(l.Type, l.Entity)).ToList(),
                q.Groups[1].Value, int.Parse(rv.Groups[1].Value), from, until, status, line);
        }

        var t = EdgeTypeField().Match(f[1]);
        if (!t.Success || !Stage3.EntityEdgeTypes.Contains(t.Groups[1].Value)) { problem = $"the type must be one of {string.Join(", ", Stage3.EntityEdgeTypes)}, Fact or Event"; return null; }
        var type = t.Groups[1].Value;
        string? kind = t.Groups[2].Success ? t.Groups[2].Value : null;
        if (type == Ontology.RelatedTo && kind is null) { problem = "RELATED_TO needs a kind in brackets"; return null; }
        if (type != Ontology.RelatedTo && kind is not null) { problem = "only RELATED_TO has a kind"; return null; }
        var ends = f[2].Split(" → ");
        if (ends.Length != 2 || ends.Any(e => e.Trim().Length == 0)) { problem = "the ends must look like: Source → Target"; return null; }
        return new GoldFact(number, "edge", type, kind, ends[0].Trim(), ends[1].Trim(), null, [], q.Groups[1].Value, int.Parse(rv.Groups[1].Value), from, until, status, line);
    }

    /// <summary>"—" → no date; "A" → from A; "A until B" / "? until B" → from A (or none) until B. Each must be valid EDTF.</summary>
    public static bool ParseStory(string text, out string? from, out string? until, out string? problem)
    {
        from = until = problem = null;
        if (text == "—") return true;
        var parts = text.Split(" until ");
        if (parts.Length > 2) { problem = "story: at most one 'until'"; return false; }
        var a = parts[0].Trim();
        if (a != "?")
        {
            if (!Edtf.TryParse(a, out _, out var e)) { problem = $"story '{a}' is not valid EDTF: {e}"; return false; }
            from = a;
        }
        else if (parts.Length == 1) { problem = "story: '?' only before 'until'"; return false; }
        if (parts.Length == 2)
        {
            var b = parts[1].Trim();
            if (!Edtf.TryParse(b, out _, out var e)) { problem = $"story '{b}' is not valid EDTF: {e}"; return false; }
            until = b;
        }
        return true;
    }

    public sealed record ImportOutcome(ParsedFactsReview? Parsed, IReadOnlyList<ParseError> Errors);

    /// <summary>
    /// Parses <paramref name="reviewPath"/>, locates every item's evidence quote in its revealed chapter (the evidence
    /// checker, threshold 0.9; a trailing "…" is dropped), and writes the facts gold JSON. Nothing is written on any error.
    /// </summary>
    /// <param name="supplementPath">Optional: a second reviewed facts file (<c>ch01-03.facts.supplement.review.md</c>) whose
    /// items, rejected lines and orderings are unioned into the same gold JSON; both files' hashes are recorded. Its errors
    /// name the file. A #number used in both files is an error.</param>
    public static ImportOutcome Import(string bookId, string reviewPath, string outPath, string? supplementPath = null)
    {
        var bytes = File.ReadAllBytes(reviewPath);
        var parsed = Parse(System.Text.Encoding.UTF8.GetString(bytes));
        byte[]? supplementBytes = null;
        if (supplementPath is not null)
        {
            supplementBytes = File.ReadAllBytes(supplementPath);
            var supplement = Parse(System.Text.Encoding.UTF8.GetString(supplementBytes));
            var name = Path.GetFileName(supplementPath);
            var errs = parsed.Errors.Concat(supplement.Errors.Select(e => e with { Text = $"{name}: {e.Text}" })).ToList();
            var mainNumbers = parsed.Items.Concat(parsed.Rejected).Select(x => x.Number).ToHashSet();
            errs.AddRange(supplement.Items.Concat(supplement.Rejected).Where(x => mainNumbers.Contains(x.Number))
                .Select(x => new ParseError(x.Line, $"{name}: #{x.Number}", "the same #number is used in the main review")));
            parsed = new ParsedFactsReview(parsed.Items.Concat(supplement.Items).ToList(), parsed.Rejected.Concat(supplement.Rejected).ToList(),
                parsed.Orders.Concat(supplement.Orders).ToList(), parsed.RejectedOrders.Concat(supplement.RejectedOrders).ToList(), errs);
        }
        if (parsed.Errors.Count > 0) return new ImportOutcome(parsed, parsed.Errors);
        var stage0 = IndexingShared.Stage0For(bookId);
        var text = IndexingShared.BookText(bookId);
        var errors = new List<ParseError>();
        GoldFact Locate(GoldFact g)
        {
            var chapter = stage0.Chapters.FirstOrDefault(c => c.Number == g.RevealedChapter);
            if (chapter is null) { errors.Add(new ParseError(g.Line, "#" + g.Number, $"no chapter {g.RevealedChapter}")); return g; }
            var quote = g.Quote.TrimEnd('…').Trim();
            var match = EvidenceChecker.Find(quote, text[chapter.CharStart..chapter.CharEnd], chapter.CharStart);
            if (match is null || match.Score < 0.9)
            {
                errors.Add(new ParseError(g.Line, "#" + g.Number, $"evidence not found in chapter {g.RevealedChapter} (best score {match?.Score ?? 0:0.###})"));
                return g;
            }
            return g with { EvidenceStart = match.CharStart, EvidenceEnd = match.CharEnd };
        }
        var items = parsed.Items.Select(Locate).ToList();
        var rejected = parsed.Rejected.Select(Locate).ToList();
        if (errors.Count > 0) return new ImportOutcome(parsed, errors);

        object Item(GoldFact g) => new
        {
            number = g.Number, kind = g.Kind, type = g.Type, relationKind = g.RelationKind, source = g.Source, target = g.Target, text = g.Text,
            links = g.Links.Select(l => new { type = l.Type, entity = l.Entity }),
            evidence = new { quote = g.Quote, charStart = g.EvidenceStart, charEnd = g.EvidenceEnd },
            revealedChapter = g.RevealedChapter, story = g.StoryFrom is null && g.StoryUntil is null ? null : new { from = g.StoryFrom, until = g.StoryUntil },
            status = g.Status,
        };
        var gold = new
        {
            book = bookId,
            source = new { file = Path.GetFileName(reviewPath), sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)) },
            supplement = supplementBytes is null ? null
                : new { file = Path.GetFileName(supplementPath!), sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(supplementBytes)) },
            importedAt = DateTimeOffset.UtcNow,
            items = items.Select(Item),
            rejected = rejected.Select(Item),
            order = parsed.Orders.Select(o => new { relation = o.Relation, a = o.A, b = o.B, quote = o.Quote, status = o.Status }),
            rejectedOrder = parsed.RejectedOrders.Select(o => new { relation = o.Relation, a = o.A, b = o.B, quote = o.Quote }),
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        File.WriteAllText(outPath, JsonSerializer.Serialize(gold, RunFolder.Json) + "\n");
        return new ImportOutcome(parsed with { Items = items, Rejected = rejected }, []);
    }

    /// <summary>Reads facts gold JSON back (for scoring).</summary>
    public static List<GoldFact> Load(string goldPath)
    {
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(goldPath))!;
        return json["items"]!.AsArray().Select(x => new GoldFact(
            x!["number"]!.GetValue<string>(), x["kind"]!.GetValue<string>(), x["type"]!.GetValue<string>(), x["relationKind"]?.GetValue<string>(),
            x["source"]?.GetValue<string>(), x["target"]?.GetValue<string>(), x["text"]?.GetValue<string>(),
            x["links"]!.AsArray().Select(l => new GoldLink(l!["type"]!.GetValue<string>(), l["entity"]!.GetValue<string>())).ToList(),
            x["evidence"]!["quote"]!.GetValue<string>(), x["revealedChapter"]!.GetValue<int>(),
            x["story"]?["from"]?.GetValue<string>(), x["story"]?["until"]?.GetValue<string>(), x["status"]!.GetValue<string>(), 0,
            x["evidence"]!["charStart"]!.GetValue<int>(), x["evidence"]!["charEnd"]!.GetValue<int>())).ToList();
    }
}
