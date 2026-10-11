using System.Text;
using System.Text.RegularExpressions;
using TemporalGraph.Cli.Crosscheck;
using TemporalGraph.Cli.Indexing;

namespace TemporalGraph.Cli.Gold;

// The review file a reviewer marks up (a markdown checklist), and its strict parser.
//
// Entity lines:   - [ ] Character · Sherlock Holmes · aliases: Holmes, Mr. Holmes · first: ch1 "short quote"
// Merge lines:    - [ ] merge · Holmes + Mr. Sherlock Holmes → Sherlock Holmes · reason: …
// Cross-checks:   - [ ] LitBank · not in ours · Character · James Mortimer · ch1 · …
// Marks:          [x] accept · [~] edit (changed inline) · [-] reject
// "## Missing (add here)" takes entity lines in the entity format, marked [x].
//
// Only the Entities and Missing sections become gold entities. Merge and cross-check marks are kept as review
// signals for later passes ("wrong merge"); to change the gold, edit or add entity lines.

public enum Mark { Unmarked, Accept, Edit, Reject }

public sealed record GoldEntity(string Type, string Name, IReadOnlyList<string> Aliases, int FirstChapter, string FirstQuote, string Status, int Line);

public sealed record ReviewedLine(string Section, Mark Mark, string Text, int Line);

public sealed record ParseError(int Line, string Text, string Problem);

/// <summary>One run's input to a review draft.</summary>
public sealed record ReviewSource(string RunId, Stage2Result Stage2, IReadOnlyList<ComparisonResult> Comparisons);

/// <summary>An entity of run number <paramref name="Run"/> (0-based, in the order given).</summary>
public sealed record UnionMember(int Run, Entity Entity);

/// <summary>An entity across runs: at most one member per run. <paramref name="NameRuns"/>: each name (canonical and
/// aliases, first surface form seen) and how many runs' entities have it.</summary>
public sealed record UnionEntity(MentionType Type, string CanonicalName, IReadOnlyList<string> Aliases, int FirstMention,
    IReadOnlyList<UnionMember> Members, IReadOnlyDictionary<string, int> NameRuns)
{
    public int Runs => Members.Count;
}

public sealed record ParsedReview(
    IReadOnlyList<GoldEntity> Entities, IReadOnlyList<GoldEntity> Rejected, IReadOnlyList<ReviewedLine> Merges,
    IReadOnlyList<ReviewedLine> CrossChecks, IReadOnlyList<ParseError> Errors);

public static partial class ReviewFile
{
    public const string Sep = " · ";
    public const string EntitiesHeading = "## Entities";
    public const string MergesHeading = "## Merges";
    public const string CrossCheckHeading = "## Cross-check disagreements";
    public const string MissingHeading = "## Missing (add here)";

    public static readonly string[] Types = ["Character", "Location"];

    /// <summary>
    /// A short locator quote from <paramref name="start"/>: the book's own characters (curly quotes kept), whitespace
    /// collapsed, cut at the paragraph's end and at about <paramref name="max"/> characters. A straight " (which would
    /// end the quote field) becomes '.
    /// </summary>
    public static string Quote(string bookText, int start, int max = 60)
    {
        var window = bookText.Substring(start, Math.Min(max * 3, bookText.Length - start));
        var paragraphEnd = window.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        if (paragraphEnd >= 0) window = window[..paragraphEnd];
        var raw = string.Join(' ', window.Split((char[])[' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries));
        var cut = raw.Length <= max ? raw : raw[..Math.Max(raw.LastIndexOf(' ', max), 1)] + "…";
        return cut.Replace("\"", "'");
    }

    public static string EntityLine(string type, string name, IEnumerable<string> aliases, int chapter, string quote) =>
        $"- [ ] {type}{Sep}{name}{Sep}aliases: {AliasList(aliases)}{Sep}first: ch{chapter} \"{quote}\"";

    private static string AliasList(IEnumerable<string> aliases)
    {
        // An alias that contains a comma is written in double quotes.
        var list = aliases.Select(a => a.Contains(',') ? $"\"{a.Replace("\"", "'")}\"" : a).ToList();
        return list.Count == 0 ? "—" : string.Join(", ", list);
    }

    public static string Generate(string bookTitle, string bookId, string chapters, string runId, IEnumerable<object> skills,
        Stage2Result stage2, IReadOnlyList<ComparisonResult> comparisons, string bookText, Func<int, int> chapterOf) =>
        Generate(bookTitle, bookId, chapters, [new ReviewSource(runId, stage2, comparisons)], bookText, chapterOf);

    /// <summary>
    /// The draft from one run, or from the <see cref="Union"/> of several runs of the same book and chapters. With
    /// several runs, every line ends in a comment saying how many of the N runs found it ("&lt;!-- 2/3 runs --&gt;");
    /// the parser ignores comments.
    /// </summary>
    public static string Generate(string bookTitle, string bookId, string chapters, IReadOnlyList<ReviewSource> sources, string bookText, Func<int, int> chapterOf)
    {
        var n = sources.Count;
        string Runs(int k) => n == 1 ? "" : $" <!-- {k}/{n} runs -->";
        var sb = new StringBuilder();
        sb.AppendLine($"# {bookTitle} · ch. {chapters} · draft gold for review");
        sb.AppendLine();
        sb.AppendLine(n == 1
            ? $"Book `{bookId}` · run `{sources[0].RunId}` · generated {DateTime.UtcNow:yyyy-MM-dd} by the tool; **gold is only what you mark here**."
            : $"Book `{bookId}` · union of {n} runs: {string.Join(", ", sources.Select(s => $"`{s.RunId}`"))} · generated {DateTime.UtcNow:yyyy-MM-dd} by the tool; **gold is only what you mark here**.");
        sb.AppendLine();
        sb.AppendLine("Mark every line: `[x]` accept · `[~]` edit (change the line inline, keep the format) · `[-]` reject.");
        sb.AppendLine("Only **Entities** and **Missing** become gold. Merge and cross-check marks are kept as review notes; to change the gold, edit or add entity lines.");
        sb.AppendLine("Aliases are comma-separated (`—` for none). Keep the ` · ` separators.");
        if (n > 1)
            sb.AppendLine($"Each line ends with how many of the {n} runs found it (`<!-- k/{n} runs -->`; names found by fewer runs than their entity are listed there too). Comments are ignored on import.");
        sb.AppendLine();

        sb.AppendLine(EntitiesHeading);
        sb.AppendLine();
        foreach (var u in Union(sources.Select(s => s.Stage2).ToList()))
        {
            var line = EntityLine(u.Type.ToString(), u.CanonicalName, u.Aliases, chapterOf(u.FirstMention), Quote(bookText, u.FirstMention));
            if (n > 1)
            {
                var notes = new List<string> { $"{u.Runs}/{n} runs" };
                var canonicals = u.Members.GroupBy(m => m.Entity.CanonicalName).ToList();
                if (canonicals.Count > 1) notes.Add("canonical: " + string.Join(", ", canonicals.Select(g => $"{g.Key} {g.Count()}/{n}")));
                var fewer = u.Aliases.Where(a => u.NameRuns[a] < u.Runs).Select(a => $"{a} {u.NameRuns[a]}/{n}").ToList();
                if (fewer.Count > 0) notes.Add("fewer runs: " + string.Join(", ", fewer));
                line += $" <!-- {string.Join(" · ", notes)} -->";
            }
            sb.AppendLine(line);
        }
        sb.AppendLine();

        sb.AppendLine(MergesHeading);
        sb.AppendLine();
        var merges = sources.SelectMany((s, i) => s.Stage2.Decisions.Where(d => d.Decision.SameEntity).Select(d => (Run: i, d)))
            .GroupBy(x => (string.Join(" + ", new[] { Norm(x.d.NameA), Norm(x.d.NameB) }.Order(StringComparer.Ordinal)), Norm(x.d.Decision.CanonicalName ?? "")))
            .ToList();
        var conflicts = sources.SelectMany((s, i) => s.Stage2.Conflicts.Select(c => (Run: i, c))).GroupBy(x => x.c).ToList();
        if (merges.Count == 0 && conflicts.Count == 0) sb.AppendLine("_None._");
        foreach (var g in merges)
        {
            var d = g.First().d;
            sb.AppendLine($"- [ ] merge{Sep}{d.NameA} + {d.NameB} → {d.Decision.CanonicalName}{Sep}reason: {d.Decision.Reason.ReplaceLineEndings(" ")}{Runs(g.Select(x => x.Run).Distinct().Count())}");
        }
        foreach (var g in conflicts)
            sb.AppendLine($"- [ ] conflict{Sep}{g.Key}{Runs(g.Select(x => x.Run).Distinct().Count())}");
        sb.AppendLine();

        sb.AppendLine(CrossCheckHeading);
        sb.AppendLine();
        foreach (var note in sources.SelectMany(s => s.Comparisons).Where(c => !c.Available).Select(c => $"_{c.Source}: {c.Note}_").Distinct())
            sb.AppendLine(note);
        var all = sources.SelectMany((s, i) => s.Comparisons.SelectMany(c => c.Disagreements).Select(d => (Run: i, d)))
            .GroupBy(x => (x.d.Source, x.d.Kind, x.d.Type, Norm(x.d.Name)))
            .ToList();
        if (all.Count == 0) sb.AppendLine("_None._");
        foreach (var g in all)
        {
            var d = g.First().d;
            sb.AppendLine($"- [ ] {d.Source}{Sep}{d.Kind}{Sep}{d.Type?.ToString() ?? "?"}{Sep}{d.Name}{Sep}ch{d.Chapter} \"{Quote(bookText, d.CharStart, 40)}\"{Sep}{d.Detail}{Runs(g.Select(x => x.Run).Distinct().Count())}");
        }
        sb.AppendLine();

        sb.AppendLine(MissingHeading);
        sb.AppendLine();
        sb.AppendLine("<!-- Add one line per missing entity, in the Entities format, marked [x]: -->");
        sb.AppendLine("<!-- - [x] Character · Name · aliases: A, B · first: ch2 \"short quote\" -->");
        return sb.ToString();
    }

    private static string Norm(string name) => Stage2.NormalizeName(name);

    /// <summary>
    /// Entities of several runs (same book and chapters) joined into one list. Runs are taken in order; each run's
    /// entities are matched one-to-one to the entities built so far, like the scorer: same type and a shared normalized
    /// name (canonical or alias), same canonical name first, then most shared names, then list order, then entity id.
    /// An unmatched entity starts a new one. One-to-one keeps a wrong merge in one run (an alias naming another
    /// entity) from swallowing that other entity in every run.
    /// </summary>
    public static IReadOnlyList<UnionEntity> Union(IReadOnlyList<Stage2Result> runs)
    {
        var groups = new List<(MentionType Type, List<UnionMember> Members, HashSet<string> Names, HashSet<string> Canonicals)>();
        static IEnumerable<string> NamesOf(Entity e) => new[] { e.CanonicalName }.Concat(e.Aliases);
        for (var run = 0; run < runs.Count; run++)
        {
            var existing = groups.Count;
            var candidates = (from e in runs[run].Entities
                              from gi in Enumerable.Range(0, existing)
                              where groups[gi].Type == e.Type
                              let shared = NamesOf(e).Select(Norm).Distinct().Count(groups[gi].Names.Contains)
                              where shared > 0
                              select (Entity: e, Group: gi, SameCanonical: groups[gi].Canonicals.Contains(Norm(e.CanonicalName)), Shared: shared))
                .OrderByDescending(c => c.SameCanonical).ThenByDescending(c => c.Shared).ThenBy(c => c.Group).ThenBy(c => c.Entity.Id).ToList();
            var assigned = new Dictionary<int, int>();
            var taken = new HashSet<int>();
            foreach (var c in candidates)
            {
                if (assigned.ContainsKey(c.Entity.Id) || taken.Contains(c.Group)) continue;
                assigned[c.Entity.Id] = c.Group;
                taken.Add(c.Group);
            }
            foreach (var e in runs[run].Entities)
            {
                if (!assigned.TryGetValue(e.Id, out var gi))
                {
                    groups.Add((e.Type, [], [], []));
                    gi = groups.Count - 1;
                }
                groups[gi].Members.Add(new UnionMember(run, e));
                groups[gi].Names.UnionWith(NamesOf(e).Select(Norm));
                groups[gi].Canonicals.Add(Norm(e.CanonicalName));
            }
        }

        return groups.Select(g =>
        {
            var canonical = g.Members.GroupBy(m => m.Entity.CanonicalName).OrderByDescending(x => x.Count()).ThenBy(x => x.Min(m => m.Run)).First().Key;
            // Names by normalized form: the first surface form seen, and how many runs have it.
            var names = new List<(string Key, string Surface, int Runs)>();
            foreach (var m in g.Members)
                foreach (var name in NamesOf(m.Entity))
                    if (names.All(x => x.Key != Norm(name)))
                        names.Add((Norm(name), name, g.Members.Count(o => NamesOf(o.Entity).Any(a => Norm(a) == Norm(name)))));
            var aliases = names.Where(x => x.Key != Norm(canonical)).OrderByDescending(x => x.Runs).Select(x => x.Surface).ToList();
            return new UnionEntity(g.Type, canonical, aliases, g.Members.Min(m => m.Entity.FirstMention), g.Members,
                names.ToDictionary(x => x.Surface, x => x.Runs));
        }).OrderBy(u => u.FirstMention).ToList();
    }

    [GeneratedRegex(@"^- \[( |x|~|-)\] (.*)$")]
    private static partial Regex ItemLine();

    [GeneratedRegex(@"^ch(\d+) ""(.*)""$")]
    private static partial Regex FirstField();

    /// <summary>Parses a reviewed file. Never guesses: anything it can't read, or an unmarked line, is an error.</summary>
    public static ParsedReview Parse(string content)
    {
        var entities = new List<GoldEntity>();
        var rejected = new List<GoldEntity>();
        var merges = new List<ReviewedLine>();
        var cross = new List<ReviewedLine>();
        var errors = new List<ParseError>();
        string? section = null;

        var (stripped, unclosedAt) = StripComments(content.Replace("\r\n", "\n"));
        var lines = stripped.Split('\n');
        if (unclosedAt is { } open) errors.Add(new ParseError(open, lines[open - 1], "HTML comment '<!--' is never closed with '-->'"));
        for (var i = 0; i < lines.Length; i++)
        {
            var n = i + 1;
            var line = lines[i].TrimEnd();
            if (line.Length == 0) continue;
            if (line.StartsWith("## "))
            {
                section = line switch
                {
                    EntitiesHeading => "entities",
                    MergesHeading => "merges",
                    CrossCheckHeading => "crosscheck",
                    MissingHeading => "missing",
                    _ => null,
                };
                if (section is null) errors.Add(new ParseError(n, line, "unknown section heading"));
                continue;
            }
            if (section is null || line.Length == 0 || line.StartsWith("# ") || (line.StartsWith('_') && line.EndsWith('_'))) continue;
            if (!line.StartsWith("- ")) { if (section is "entities" or "missing") errors.Add(new ParseError(n, line, "not a checklist line")); continue; }

            var m = ItemLine().Match(line);
            if (!m.Success) { errors.Add(new ParseError(n, line, "expected '- [x] …', '- [~] …', '- [-] …' or '- [ ] …'")); continue; }
            var mark = m.Groups[1].Value switch { "x" => Mark.Accept, "~" => Mark.Edit, "-" => Mark.Reject, _ => Mark.Unmarked };
            var body = m.Groups[2].Value;
            if (mark == Mark.Unmarked) { errors.Add(new ParseError(n, line, "not reviewed: mark it [x], [~] or [-]")); continue; }

            switch (section)
            {
                case "merges": merges.Add(new ReviewedLine("merges", mark, body, n)); break;
                case "crosscheck": cross.Add(new ReviewedLine("crosscheck", mark, body, n)); break;
                default:
                    if (section == "missing" && mark != Mark.Accept) { errors.Add(new ParseError(n, line, "lines under Missing must be marked [x]")); break; }
                    var entity = ParseEntity(body, n, out var problem);
                    if (entity is null) { errors.Add(new ParseError(n, line, problem!)); break; }
                    var status = section == "missing" ? "added" : mark == Mark.Edit ? "edited" : mark == Mark.Accept ? "accepted" : "rejected";
                    (mark == Mark.Reject ? rejected : entities).Add(entity with { Status = status });
                    break;
            }
        }
        return new ParsedReview(entities, rejected, merges, cross, errors);
    }

    /// <summary>
    /// Removes every HTML comment (&lt;!-- … --&gt;), on its own line, trailing a line, or spanning lines, keeping
    /// the line breaks so line numbers in errors still match the file. Reviewer notes are comments and never content.
    /// </summary>
    public static (string Text, int? UnclosedAtLine) StripComments(string content)
    {
        var sb = new System.Text.StringBuilder(content.Length);
        var i = 0;
        while (i < content.Length)
        {
            var open = content.IndexOf("<!--", i, StringComparison.Ordinal);
            if (open < 0) { sb.Append(content, i, content.Length - i); break; }
            sb.Append(content, i, open - i);
            var close = content.IndexOf("-->", open + 4, StringComparison.Ordinal);
            if (close < 0)
                return (sb.ToString() + new string('\n', content[open..].Count(c => c == '\n')), content[..open].Count(c => c == '\n') + 1);
            sb.Append('\n', content[open..close].Count(c => c == '\n'));
            i = close + 3;
        }
        return (sb.ToString(), null);
    }

    /// <summary>Comma-separated; an alias in double quotes may contain commas.</summary>
    private static List<string>? SplitAliases(string text, out string? problem)
    {
        problem = null;
        var list = new List<string>();
        var i = 0;
        while (i <= text.Length)
        {
            while (i < text.Length && text[i] == ' ') i++;
            if (i < text.Length && text[i] == '"')
            {
                var close = text.IndexOf('"', i + 1);
                if (close < 0) { problem = "unclosed quote in aliases"; return null; }
                list.Add(text[(i + 1)..close]);
                i = close + 1;
                while (i < text.Length && text[i] == ' ') i++;
                if (i < text.Length && text[i] != ',') { problem = "expected ',' after a quoted alias"; return null; }
                i++;
            }
            else
            {
                var comma = text.IndexOf(',', i);
                list.Add((comma < 0 ? text[i..] : text[i..comma]).Trim());
                if (comma < 0) break;
                i = comma + 1;
            }
        }
        return list;
    }

    private static GoldEntity? ParseEntity(string body, int line, out string? problem)
    {
        problem = null;
        var parts = body.Split(Sep);
        if (parts.Length != 4) { problem = $"expected 4 fields separated by ' · ' (type · name · aliases: … · first: chN \"quote\"), found {parts.Length}"; return null; }
        var (type, name, aliases, first) = (parts[0].Trim(), parts[1].Trim(), parts[2].Trim(), parts[3].Trim());
        if (!Types.Contains(type)) { problem = $"type must be {string.Join(" or ", Types)}, found '{type}'"; return null; }
        if (name.Length == 0) { problem = "empty name"; return null; }
        if (!aliases.StartsWith("aliases:")) { problem = "third field must start with 'aliases:'"; return null; }
        var aliasText = aliases["aliases:".Length..].Trim();
        var aliasList = aliasText is "—" or "" ? [] : SplitAliases(aliasText, out problem);
        if (aliasList is null) return null;
        if (aliasList.Any(a => a.Length == 0)) { problem = "empty alias between commas"; return null; }
        if (!first.StartsWith("first:")) { problem = "fourth field must start with 'first:'"; return null; }
        var fm = FirstField().Match(first["first:".Length..].Trim());
        if (!fm.Success) { problem = "first must look like: first: ch2 \"short quote\""; return null; }
        return new GoldEntity(type, name, aliasList, int.Parse(fm.Groups[1].Value), fm.Groups[2].Value, "", line);
    }
}
