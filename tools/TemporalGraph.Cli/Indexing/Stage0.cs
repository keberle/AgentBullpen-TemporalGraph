using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using TemporalGraph.Cli.Telemetry;

namespace TemporalGraph.Cli.Indexing;

// Stage 0 — text preparation. Pure: text in, chapters / paragraphs / chunk plan / checks out.
//
// Offsets are UTF-16 code units of the source text decoded from its committed UTF-8 bytes (no BOM in
// either file), with CRLF counted as two units. Neither text has characters outside the BMP, so these
// are also Unicode code points. Ranges are [start, end).

public sealed record TextPart(string Name, int CharStart, int CharEnd);

public sealed record ContentsEntry(int Number, string Title);

public sealed record ParagraphInfo(int Chapter, int Paragraph, int CharStart, int CharEnd);

public sealed record ChunkInfo(int Index, int Chapter, int CharStart, int CharEnd, int EstimatedTokens);

/// <param name="Narrator">Who tells the chapter, from the chapter titles (<see cref="Stage0.Narrators"/>); null when the titles name no one.</param>
/// <param name="NarratorForm">"letter" or "diary" when the title says the chapter is one; else null (plain narration).</param>
public sealed record ChapterInfo(int Number, string Heading, string Title, int CharStart, int CharEnd, IReadOnlyList<ParagraphInfo> Paragraphs,
    string? Narrator = null, string? NarratorForm = null)
{
    /// <summary>The <c>chapters.narrator</c> column: "Dr. Watson", "Dr. Watson (letter)", "Dr. Watson (diary)"; null without a narrator.</summary>
    public string? NarratorColumn => Narrator is null ? null : NarratorForm is null ? Narrator : $"{Narrator} ({NarratorForm})";
}

public sealed record CheckResult(string Name, string Book, bool Passed, string Details);

/// <summary>
/// chapter-sized chunks. One chunk per chapter; a chapter over <paramref name="ChunkTokens"/> estimated tokens is split at
/// paragraph boundaries into equal parts, with no overlap. Tokens = characters ÷ 4. <see cref="Windows"/> is the earlier
/// setting (~1,000-token windows with a ~100-token overlap, cut at line breaks), still available as <c>--chunk-tokens 1000</c>.
/// </summary>
public sealed record ChunkSettings(int ChunkTokens = 8000, int OverlapTokens = 0, int CharsPerToken = 4, bool ChapterAligned = true)
{
    /// <summary>The chunking before the one-pass pipeline, and of every run folder made before it.</summary>
    public static readonly ChunkSettings Windows = new(1000, 100, 4, false);

    /// <summary><c>Indexing:Chunk:Tokens</c> and <c>Indexing:Chunk:ChapterAligned</c>; a size under 4,000 tokens without the second key is
    /// the windowed chunking (so <c>--chunk-tokens 1000</c> is the old setting as it was).</summary>
    public static ChunkSettings From(Microsoft.Extensions.Configuration.IConfiguration config, int? tokens = null)
    {
        var s = config.GetSection("Indexing:Chunk");
        var size = tokens ?? (int.TryParse(s["Tokens"], out var t) && t > 0 ? t : 8000);
        var aligned = tokens is null && bool.TryParse(s["ChapterAligned"], out var a) ? a : size >= 4000;
        return aligned ? new ChunkSettings(size, 0, 4, true) : new ChunkSettings(size, Math.Max(0, size / 10), 4, false);
    }

    public string TokenEstimate => $"characters / {CharsPerToken} (https://help.openai.com/en/articles/4936856)";
}

public sealed record Stage0Result(
    string BookId,
    string TextSha256,
    int TextLength,
    TextPart Body,
    IReadOnlyList<TextPart> Skipped,
    IReadOnlyList<ContentsEntry> Contents,
    IReadOnlyList<ChapterInfo> Chapters,
    ChunkSettings ChunkSettings,
    IReadOnlyList<ChunkInfo> Chunks,
    IReadOnlyList<CheckResult> Checks)
{
    public int ParagraphCount => Chapters.Sum(c => c.Paragraphs.Count);
    public bool Passed => Checks.All(c => c.Passed);
}

public static partial class Stage0
{
    public const string StageName = "stage-0";

    // Body chapter heading: the whole line, e.g. "Chapter 1." (#2852) or "Chapter I." (#1257).
    // The full stop may be missing and the title may follow on the same line ("Chapter 1 Mr. Sherlock Holmes").
    [GeneratedRegex(@"^Chapter\s+([IVXLC]+|\d+)\.?(?:\s+(\S.*?))?\s*$")]
    private static partial Regex HeadingLine();

    // HeadingStyle.ChapterCaps: "CHAPTER I", "CHAPTER 12.", "CHAPTER ONE", the title on the same line or the next.
    [GeneratedRegex(@"^CHAPTER\s+([IVXLC]+|\d+|[A-Z]+(?:-[A-Z]+)?)\.?(?:[\s.:—-]+(\S.*?))?\s*$")]
    private static partial Regex CapsHeadingLine();

    // HeadingStyle.Numbered: "1. The Title".
    [GeneratedRegex(@"^(\d+)\.\s+(\S.*?)\s*$")]
    private static partial Regex NumberedHeadingLine();

    // Contents entries of the two contents styles (front matter only).
    [GeneratedRegex(@"^\s+CHAPTER\s+([IVXLC]+|\d+|[A-Z]+(?:-[A-Z]+)?)\.?[\s.:—-]+(\S.*?)\s*$")]
    private static partial Regex CapsContentsLine();

    [GeneratedRegex(@"^\s+(\d+)\.\s+(\S.*?)\s*$")]
    private static partial Regex NumberedContentsLine();

    // A "PART ONE" / "PART II--The Sea-cook" line starts a part to skip.
    [GeneratedRegex(@"^PART\s+\w+\b.*$")]
    private static partial Regex PartLine();

    // Contents entry (front matter only): " Chapter 1  Mr. Sherlock Holmes" or " Chapter I. THE THREE …".
    [GeneratedRegex(@"^\s+Chapter\s+([IVXLC]+|\d+)\.?\s+(\S.*?)\s*$")]
    private static partial Regex ContentsLine();

    // HeadingStyle.BareRoman (#120): the heading "XVI"; the contents line "     XVI.  TITLE . . . . 100", whose page number (after
    // dot leaders) may sit on an indented continuation line when the title wraps.
    [GeneratedRegex(@"^([IVXLC]+)$")]
    private static partial Regex BareRomanLine();

    [GeneratedRegex(@"^\s+([IVXLC]+)\.\s+(\S.*?)\s*$")]
    private static partial Regex RomanContentsLine();

    [GeneratedRegex(@"[\s.]*\d+\s*$")]
    private static partial Regex PageNumber();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    private const string StartMarker = "*** START OF THE PROJECT GUTENBERG EBOOK";
    private const string EndMarker = "*** END OF THE PROJECT GUTENBERG EBOOK";

    public static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <param name="textBytes">The source text exactly as committed / embedded.</param>
    /// <param name="embeddedSha256">SHA-256 of the embedded resource, read separately, for the hash check.</param>
    public static Stage0Result Prepare(BookProfile profile, byte[] textBytes, string embeddedSha256, ChunkSettings? chunkSettings = null, string? runId = null)
    {
        chunkSettings ??= new ChunkSettings();
        var bookId = profile.BookId;
        var hash = Sha256(textBytes);
        var text = Encoding.UTF8.GetString(textBytes);
        var lines = SplitLines(text);

        // Body: between the START and END marker lines.
        var start = lines.FindIndex(l => l.Text.StartsWith(StartMarker, StringComparison.Ordinal));
        var end = lines.FindIndex(l => l.Text.StartsWith(EndMarker, StringComparison.Ordinal));
        if (start < 0 || end < 0 || end < start)
            throw new InvalidDataException($"{bookId}: Gutenberg START/END lines not found.");
        var body = new TextPart("body", lines[start].NextStart, lines[end].Start);
        var bodyLines = lines.Skip(start + 1).Take(end - start - 1).ToList();

        // Markers: chapter headings and skipped-part headings, in text order.
        var (markers, contentsHeadings) = Scan(bodyLines, profile.Headings, profile.SkipHeadings.Contains);

        var firstMarker = markers.Count > 0 ? markers[0].Start : body.CharEnd;
        var skipped = new List<TextPart> { new("front matter (title, contents)", body.CharStart, firstMarker) };
        var front = bodyLines.Where(l => l.Start < firstMarker).Select(l => l.Text).ToList();
        var contents = ContentsOf(profile.Headings, front, contentsHeadings);

        var chapters = new List<ChapterInfo>();
        var chunks = new List<ChunkInfo>();
        var fixes = new List<string>();
        for (var k = 0; k < markers.Count; k++)
        {
            var (lineIx, partStart, number, name, inlineTitle) = markers[k];
            var partEnd = k + 1 < markers.Count ? markers[k + 1].Start : body.CharEnd;
            if (number is null)
            {
                skipped.Add(new TextPart(name, partStart, partEnd));
                continue;
            }

            // Title: the non-blank lines after the heading, up to the first blank line, joined by a space.
            var titleLines = new List<string>();
            var i = lineIx + 1;
            if (inlineTitle is null)
                for (; i < bodyLines.Count && bodyLines[i].Text.Length > 0; i++) titleLines.Add(bodyLines[i].Text.Trim());
            var title = Normalize(inlineTitle ?? string.Join(" ", titleLines));
            if (profile.NumberFixes?.TryGetValue(title, out var fixedNumber) == true)
            {
                fixes.Add($"\"{name}\" ({title}) is chapter {fixedNumber}");
                number = fixedNumber;
            }

            using var chapterSpan = IndexingTelemetry.Source.StartActivity($"chapter {number}");
            chapterSpan?.SetTag(IndexingTelemetry.RunId, runId);
            chapterSpan?.SetTag(IndexingTelemetry.Stage, StageName);
            chapterSpan?.SetTag(IndexingTelemetry.BookId, bookId);
            chapterSpan?.SetTag(IndexingTelemetry.Chapter, number);

            // Paragraphs: blocks of non-blank lines after the title, inside the chapter.
            var paragraphs = new List<ParagraphInfo>();
            int? paraStart = null;
            var paraEnd = 0;
            for (; i < bodyLines.Count && bodyLines[i].Start < partEnd; i++)
            {
                var line = bodyLines[i];
                if (line.Text.Trim().Length == 0)
                {
                    if (paraStart is { } ps) paragraphs.Add(new ParagraphInfo(number.Value, paragraphs.Count + 1, ps, paraEnd));
                    paraStart = null;
                    continue;
                }
                var lead = line.Text.Length - line.Text.TrimStart().Length;
                paraStart ??= line.Start + lead;
                paraEnd = line.Start + line.Text.TrimEnd().Length;
            }
            if (paraStart is { } last) paragraphs.Add(new ParagraphInfo(number.Value, paragraphs.Count + 1, last, paraEnd));

            var chapter = new ChapterInfo(number.Value, name, title, partStart, partEnd, paragraphs);
            chapters.Add(chapter);
            var chapterChunks = PlanChunks(text, chapter, chunkSettings, chunks.Count);
            chunks.AddRange(chapterChunks);
            chapterSpan?.SetTag("chapter.paragraphs", paragraphs.Count);
            chapterSpan?.SetTag("chapter.chunks", chapterChunks.Count);
        }

        chapters = Narrators(chapters);
        var result = new Stage0Result(bookId, hash, text.Length, body, skipped, contents, chapters, chunkSettings, chunks, []);
        return result with { Checks = RunChecks(profile, result, embeddedSha256, fixes) };
    }

    private readonly record struct Marker(int LineIx, int Start, int? Chapter, string Name, string? InlineTitle);

    /// <summary>A chapter heading of <paramref name="style"/>: its number, and its title when that is on the same line.</summary>
    private static (int Number, string? Title)? Heading(HeadingStyle style, string line)
    {
        var m = (style switch
        {
            HeadingStyle.BareRoman => BareRomanLine(),
            HeadingStyle.ChapterCaps => CapsHeadingLine(),
            HeadingStyle.Numbered => NumberedHeadingLine(),
            _ => HeadingLine(),
        }).Match(line);
        if (!m.Success || TryNumber(m.Groups[1].Value) is not { } number) return null;
        return (number, m.Groups.Count > 2 && m.Groups[2].Success && m.Groups[2].Value.Length > 0 ? m.Groups[2].Value : null);
    }

    /// <summary>
    /// The markers of the body in text order: chapter headings and the headings of parts to skip. When the numbering starts
    /// again (a contents list written with the same words as the headings), the headings before the last chapter 1 are the contents
    /// list, not chapters, and are returned apart.
    /// </summary>
    private static (List<Marker> Markers, List<ContentsEntry> ContentsHeadings) Scan(IReadOnlyList<Line> bodyLines, HeadingStyle style, Func<string, bool> skip)
    {
        var markers = new List<Marker>();
        for (var i = 0; i < bodyLines.Count; i++)
        {
            var line = bodyLines[i];
            if (Heading(style, line.Text) is { } h) markers.Add(new Marker(i, line.Start, h.Number, line.Text, h.Title));
            else if (skip(line.Text)) markers.Add(new Marker(i, line.Start, null, line.Text, null));
        }
        var contents = new List<ContentsEntry>();
        var lastFirst = markers.FindLastIndex(m => m.Chapter == 1);
        if (lastFirst > 0 && markers.Take(lastFirst).Any(m => m.Chapter == 1))
        {
            contents = markers.Take(lastFirst).Where(m => m.Chapter is not null && m.InlineTitle is not null)
                .Select(m => new ContentsEntry(m.Chapter!.Value, Normalize(m.InlineTitle!))).ToList();
            markers = markers.Skip(lastFirst).ToList();
        }
        return (markers, contents);
    }

    /// <summary>The title of a chapter marker: on its line, else the non-blank lines after it up to the first blank line.</summary>
    private static string TitleOf(IReadOnlyList<Line> bodyLines, Marker marker)
    {
        if (marker.InlineTitle is { } inline) return Normalize(inline);
        var lines = new List<string>();
        for (var i = marker.LineIx + 1; i < bodyLines.Count && bodyLines[i].Text.Length > 0; i++) lines.Add(bodyLines[i].Text.Trim());
        return Normalize(string.Join(" ", lines));
    }

    /// <summary>The contents entries in the front matter, by heading style; else the contents list <see cref="Scan"/> set apart.</summary>
    private static List<ContentsEntry> ContentsOf(HeadingStyle style, IReadOnlyList<string> front, List<ContentsEntry> contentsHeadings)
    {
        if (style == HeadingStyle.BareRoman) return RomanContents(front);
        var line = style switch { HeadingStyle.ChapterCaps => CapsContentsLine(), HeadingStyle.Numbered => NumberedContentsLine(), _ => ContentsLine() };
        var found = front.Select(l => line.Match(l)).Where(m => m.Success && TryNumber(m.Groups[1].Value) is not null)
            .Select(m => new ContentsEntry(TryNumber(m.Groups[1].Value)!.Value, Normalize(m.Groups[2].Value))).ToList();
        return found.Count > 0 ? found : contentsHeadings;
    }

    /// <summary>The headings of parts to skip when no profile names them.</summary>
    public static readonly IReadOnlyList<string> DefaultSkipHeadings =
        ["THE END", "EPILOGUE", "PREFACE", "AUTHOR'S PREFACE", "AUTHOR’S PREFACE", "INTRODUCTION"];

    private static bool DefaultSkip(string line) => DefaultSkipHeadings.Contains(line) || PartLine().IsMatch(line);

    /// <summary>
    /// The profile of a text, detected. Each heading style is tried; the one with the most headings that number 1…n wins
    /// (a heading misnumbered in the text counts when the contents list gives its title the number it should have, which becomes a
    /// number fix). The parts to skip are the default headings and the PART lines the body has. Null when the text has no
    /// Gutenberg START/END lines.
    /// </summary>
    public static BookProfile? Detect(string bookId, string text)
    {
        var lines = SplitLines(text);
        var start = lines.FindIndex(l => l.Text.StartsWith(StartMarker, StringComparison.Ordinal));
        var end = lines.FindIndex(l => l.Text.StartsWith(EndMarker, StringComparison.Ordinal));
        if (start < 0 || end < 0 || end < start) return null;
        var bodyLines = lines.Skip(start + 1).Take(end - start - 1).ToList();

        (BookProfile Profile, bool Sequential)? best = null;
        foreach (var style in Enum.GetValues<HeadingStyle>())
        {
            var (markers, contentsHeadings) = Scan(bodyLines, style, DefaultSkip);
            var chapters = markers.Where(m => m.Chapter is not null).ToList();
            if (chapters.Count == 0) continue;
            var firstMarker = markers[0].Start;
            var contents = ContentsOf(style, bodyLines.Where(l => l.Start < firstMarker).Select(l => l.Text).ToList(), contentsHeadings);
            var fixes = new Dictionary<string, int>();
            var sequential = true;
            for (var k = 0; k < chapters.Count; k++)
            {
                if (chapters[k].Chapter == k + 1) continue;
                var title = TitleOf(bodyLines, chapters[k]);
                if (contents.Any(c => c.Number == k + 1 && string.Equals(c.Title, title, StringComparison.OrdinalIgnoreCase))) fixes[title] = k + 1;
                else sequential = false;
            }
            var skips = markers.Where(m => m.Chapter is null).Select(m => m.Name).Distinct().ToList();
            var profile = new BookProfile(bookId, chapters.Count, skips, style, fixes.Count > 0 ? fixes : null);
            if (best is null || (sequential && !best.Value.Sequential) || (sequential == best.Value.Sequential && chapters.Count > best.Value.Profile.ExpectedChapters))
                best = (profile, sequential);
        }
        return best?.Profile;
    }

    /// <summary>HeadingStyle.BareRoman contents: "     XVI.  TITLE" entries; a line without a page number continues on the next line.</summary>
    private static List<ContentsEntry> RomanContents(IReadOnlyList<string> front)
    {
        var entries = new List<ContentsEntry>();
        for (var i = 0; i < front.Count; i++)
        {
            var m = RomanContentsLine().Match(front[i]);
            if (!m.Success) continue;
            var title = m.Groups[2].Value;
            while (!PageNumber().IsMatch(title) && i + 1 < front.Count && front[i + 1].Trim().Length > 0) title += " " + front[++i].Trim();
            entries.Add(new ContentsEntry(ParseNumber(m.Groups[1].Value), Normalize(PageNumber().Replace(title, ""))));
        }
        return entries;
    }

    // ---- Narrator ---------------------------------------------------------------------------------

    // A title that says whose report or diary the chapter is: "First Report of Dr. Watson", "Extract from the Diary of Dr. Watson".
    [GeneratedRegex(@"^(?:\w+ )?Report of (?<letter>.+)$|^Extract from the Diary of (?<diary>.+)$")]
    private static partial Regex NarratorTitle();

    /// <summary>
    /// The narrator of each chapter, from the chapter titles alone (no outside data). A title that names whose report
    /// or diary the chapter is gives that chapter's narrator and form (letter, diary). When every such title names the same
    /// person, the book is that person's telling, so the other chapters get the same narrator in plain narration. Titles that
    /// name nobody, or several people, leave the narrator null. For #2852: Dr. Watson for chapters 1–7, 9 and 11–15,
    /// Dr. Watson (letter) for chapter 8, Dr. Watson (diary) for chapter 10.
    /// </summary>
    public static List<ChapterInfo> Narrators(IReadOnlyList<ChapterInfo> chapters)
    {
        var named = chapters.Select(c => (c.Number, Match: NarratorTitle().Match(c.Title))).Where(x => x.Match.Success)
            .ToDictionary(x => x.Number, x => x.Match.Groups["letter"].Success
                ? (Name: x.Match.Groups["letter"].Value.Trim(), Form: "letter") : (Name: x.Match.Groups["diary"].Value.Trim(), Form: "diary"));
        var names = named.Values.Select(v => v.Name).Distinct().ToList();
        if (names.Count != 1) return chapters.ToList();
        return chapters.Select(c => named.TryGetValue(c.Number, out var n) ? c with { Narrator = n.Name, NarratorForm = n.Form } : c with { Narrator = names[0] }).ToList();
    }

    // ---- Chunk plan -------------------------------------------------------------------------------

    /// <summary>
    /// Windows of ChunkTokens × CharsPerToken characters over the whole chapter range, never crossing it.
    /// A window ends at the last line break before its limit (if one is in the back half), and the next
    /// window starts OverlapTokens × CharsPerToken characters earlier, moved forward to a line start.
    /// </summary>
    public static List<ChunkInfo> PlanChunks(string text, ChapterInfo chapter, ChunkSettings s, int firstIndex)
    {
        if (s.ChapterAligned) return PlanChapterChunks(chapter, s, firstIndex);
        var size = s.ChunkTokens * s.CharsPerToken;
        var overlap = s.OverlapTokens * s.CharsPerToken;
        var chunks = new List<ChunkInfo>();
        var start = chapter.CharStart;
        while (true)
        {
            var end = Math.Min(start + size, chapter.CharEnd);
            if (end < chapter.CharEnd)
            {
                var lineBreak = text.LastIndexOf('\n', end - 1, end - (start + size / 2));
                if (lineBreak >= 0) end = lineBreak + 1;
            }
            chunks.Add(new ChunkInfo(firstIndex + chunks.Count, chapter.Number, start, end, (end - start + s.CharsPerToken - 1) / s.CharsPerToken));
            if (end >= chapter.CharEnd) break;

            var next = end - overlap;
            var lineStart = text.IndexOf('\n', next, end - next);
            next = lineStart >= 0 && lineStart + 1 < end ? lineStart + 1 : next;
            start = Math.Max(next, start + 1);
        }
        return chunks;
    }

    /// <summary>
    /// The whole chapter as one chunk; a chapter over ChunkTokens × CharsPerToken characters as n equal parts
    /// (n = the fewest that fit), each cut at the start of the paragraph nearest to its even share. The parts tile the chapter:
    /// the first starts at the chapter's start (its heading), the last ends at its end, and their text put together is the chapter.
    /// </summary>
    public static List<ChunkInfo> PlanChapterChunks(ChapterInfo chapter, ChunkSettings s, int firstIndex)
    {
        int Tokens(int start, int end) => (end - start + s.CharsPerToken - 1) / s.CharsPerToken;
        var length = chapter.CharEnd - chapter.CharStart;
        var size = s.ChunkTokens * s.CharsPerToken;
        var parts = Math.Max(1, (length + size - 1) / size);
        var cuts = new List<int> { chapter.CharStart };
        var starts = chapter.Paragraphs.Skip(1).Select(p => p.CharStart).ToList();
        for (var k = 1; k < parts && starts.Count > 0; k++)
        {
            var ideal = chapter.CharStart + (long)length * k / parts;
            var cut = starts.Where(p => p > cuts[^1]).OrderBy(p => Math.Abs(p - ideal)).Cast<int?>().FirstOrDefault();
            if (cut is { } c && c < chapter.CharEnd) cuts.Add(c);
        }
        cuts.Add(chapter.CharEnd);
        return cuts.Zip(cuts.Skip(1)).Select((x, i) => new ChunkInfo(firstIndex + i, chapter.Number, x.First, x.Second, Tokens(x.First, x.Second))).ToList();
    }

    // ---- Layer 1 checks ---------------------------------------------------------------------------

    public static List<CheckResult> RunChecks(BookProfile profile, Stage0Result r, string embeddedSha256, IReadOnlyList<string>? fixes = null)
    {
        var book = r.BookId;
        var checks = new List<CheckResult>();
        void Add(string name, bool ok, string details) => checks.Add(new CheckResult(name, book, ok, details));

        Add("text-hash", r.TextSha256 == embeddedSha256,
            $"recorded {r.TextSha256}; embedded resource {embeddedSha256}");

        Add("chapter-count", r.Chapters.Count == profile.ExpectedChapters,
            $"found {r.Chapters.Count}, expected {profile.ExpectedChapters}");

        var numbers = r.Chapters.Select(c => c.Number).ToList();
        Add("chapter-numbers", numbers.SequenceEqual(Enumerable.Range(1, numbers.Count)),
            $"numbers {string.Join(",", numbers)}" + (fixes is { Count: > 0 } ? $"; fixed from the profile: {string.Join("; ", fixes)}" : ""));

        var foundSkips = r.Skipped.Skip(1).Select(p => p.Name).ToList();
        Add("skipped-parts", foundSkips.Order().SequenceEqual(profile.SkipHeadings.Order()),
            $"skipped: front matter; {string.Join("; ", r.Skipped.Skip(1).Select(p => $"{p.Name} [{p.CharStart},{p.CharEnd})"))}");

        var titleProblems = new List<string>();
        // A text without a contents list has nothing to cross-check the titles against.
        if (r.Contents.Count == 0) { }
        else if (r.Contents.Count != r.Chapters.Count) titleProblems.Add($"contents has {r.Contents.Count} chapter entries, body has {r.Chapters.Count} chapters");
        foreach (var c in r.Contents.Count == 0 ? [] : r.Chapters)
        {
            var entry = r.Contents.FirstOrDefault(e => e.Number == c.Number);
            if (entry is null) titleProblems.Add($"chapter {c.Number}: not in contents");
            else if (!string.Equals(entry.Title, c.Title, StringComparison.OrdinalIgnoreCase)) titleProblems.Add($"chapter {c.Number}: body \"{c.Title}\" vs contents \"{entry.Title}\"");
        }
        Add("titles", titleProblems.Count == 0,
            r.Contents.Count == 0 ? "no contents list in the front matter to check the titles against"
            : titleProblems.Count == 0 ? $"all {r.Chapters.Count} chapter titles match the contents" : string.Join("; ", titleProblems));

        // Coverage: front matter, chapters and skipped parts tile the body exactly.
        var parts = r.Skipped.Concat(r.Chapters.Select(c => new TextPart($"chapter {c.Number}", c.CharStart, c.CharEnd)))
            .OrderBy(p => p.CharStart).ToList();
        var coverage = new List<string>();
        var pos = r.Body.CharStart;
        foreach (var p in parts)
        {
            if (p.CharStart != pos) coverage.Add($"{p.Name} starts at {p.CharStart}, expected {pos}");
            if (p.CharEnd <= p.CharStart) coverage.Add($"{p.Name} is empty");
            pos = p.CharEnd;
        }
        if (pos != r.Body.CharEnd) coverage.Add($"parts end at {pos}, body ends at {r.Body.CharEnd}");
        foreach (var c in r.Chapters)
        {
            var prevEnd = c.CharStart;
            foreach (var p in c.Paragraphs)
            {
                if (p.CharStart < prevEnd || p.CharEnd <= p.CharStart || p.CharEnd > c.CharEnd)
                    coverage.Add($"chapter {c.Number} paragraph {p.Paragraph} [{p.CharStart},{p.CharEnd}) outside or overlapping");
                prevEnd = p.CharEnd;
            }
            if (c.Paragraphs.Count == 0) coverage.Add($"chapter {c.Number} has no paragraphs");
        }
        Add("coverage", coverage.Count == 0,
            coverage.Count == 0
                ? $"{parts.Count} parts tile the body [{r.Body.CharStart},{r.Body.CharEnd}); {r.ParagraphCount} paragraphs inside their chapters"
                : string.Join("; ", coverage));

        var chunkProblems = new List<string>();
        foreach (var c in r.Chapters)
        {
            var mine = r.Chunks.Where(k => k.Chapter == c.Number).OrderBy(k => k.CharStart).ToList();
            var covered = c.CharStart;
            foreach (var k in mine)
            {
                if (k.CharStart < c.CharStart || k.CharEnd > c.CharEnd) chunkProblems.Add($"chunk {k.Index} leaves chapter {c.Number}");
                if (k.CharStart > covered) chunkProblems.Add($"gap before chunk {k.Index} in chapter {c.Number}");
                covered = Math.Max(covered, k.CharEnd);
            }
            if (covered != c.CharEnd) chunkProblems.Add($"chapter {c.Number} covered to {covered} of {c.CharEnd}");
        }
        Add("chunks", chunkProblems.Count == 0,
            chunkProblems.Count == 0
                ? $"{r.Chunks.Count} chunks, each inside one chapter, covering all {r.Chapters.Count} chapters"
                : string.Join("; ", chunkProblems));

        return checks;
    }

    // ---- Helpers ----------------------------------------------------------------------------------

    private readonly record struct Line(int Start, string Text, int NextStart);

    /// <summary>Lines with their start offsets; the terminator (CRLF, LF or CR) is not part of the text.</summary>
    private static List<Line> SplitLines(string text)
    {
        var lines = new List<Line>();
        var i = 0;
        while (i < text.Length)
        {
            var j = text.IndexOfAny(['\r', '\n'], i);
            if (j < 0) { lines.Add(new Line(i, text[i..], text.Length)); break; }
            var next = j + (text[j] == '\r' && j + 1 < text.Length && text[j + 1] == '\n' ? 2 : 1);
            lines.Add(new Line(i, text[i..j], next));
            i = next;
        }
        return lines;
    }

    private static string Normalize(string s) => Whitespace().Replace(s.Trim(), " ");

    private static int ParseNumber(string s) => char.IsDigit(s[0]) ? int.Parse(s) : RomanToInt(s);

    private static readonly string[] Units = ["ONE", "TWO", "THREE", "FOUR", "FIVE", "SIX", "SEVEN", "EIGHT", "NINE", "TEN", "ELEVEN", "TWELVE",
        "THIRTEEN", "FOURTEEN", "FIFTEEN", "SIXTEEN", "SEVENTEEN", "EIGHTEEN", "NINETEEN"];
    private static readonly string[] Tens = ["TWENTY", "THIRTY", "FORTY", "FIFTY", "SIXTY", "SEVENTY", "EIGHTY", "NINETY"];

    /// <summary>A chapter number as digits, a Roman numeral or an English word ("TWENTY-ONE"); null when it is none of them.</summary>
    private static int? TryNumber(string s)
    {
        if (s.Length == 0) return null;
        if (s.All(char.IsDigit)) return int.TryParse(s, out var n) ? n : null;
        if (s.All(c => "IVXLC".Contains(c))) return RomanToInt(s);
        var parts = s.ToUpperInvariant().Split('-');
        var unit = Array.IndexOf(Units, parts[^1]) + 1;
        var ten = Array.IndexOf(Tens, parts[0]);
        return parts.Length == 1 ? (unit > 0 ? unit : ten >= 0 ? (ten + 2) * 10 : null)
            : parts.Length == 2 && ten >= 0 && unit is > 0 and < 10 ? (ten + 2) * 10 + unit : null;
    }

    private static int RomanToInt(string roman)
    {
        int V(char c) => c switch { 'I' => 1, 'V' => 5, 'X' => 10, 'L' => 50, 'C' => 100, _ => throw new FormatException(roman) };
        var total = 0;
        for (var i = 0; i < roman.Length; i++)
        {
            var v = V(roman[i]);
            total += i + 1 < roman.Length && V(roman[i + 1]) > v ? -v : v;
        }
        return total;
    }
}
