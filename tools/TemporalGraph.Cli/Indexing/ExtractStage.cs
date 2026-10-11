using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using TemporalGraph.Cli.Runs;
using TemporalGraph.Cli.Scoring;
using TemporalGraph.Cli.Telemetry;
using TemporalGraph.Data;
using TemporalGraph.Llm;

namespace TemporalGraph.Cli.Indexing;

// Stage 1 of the pipeline: "extract" (skills/extract-graph.md).
// The text is read once. One structured-output call per chunk (a chunk is a chapter, or a part of a long one), one sample,
// temperature 0. The model returns, with surface names as the text has them and no entity ids:
//   the names of the people and places in the chunk; Events (narrated or referenced) with participants; Facts with their
//   subjects and, per discovering Event, how it was discovered; the Perceptions the text shows; Retractions; Identity
//   reveals; RELATED_TO and LOCATED_AT.
// The reply is kept small. A name is written once, in the name list, with a number; every other item points at it by
// that number. A summary is one sentence of at most 140 characters and a quote's opening words at most 80; code cuts what runs
// over. The model's thinking, billed as output, is set by Indexing:StageA:Thinking (medium, which gold decided).
// Evidence is compact: an offset into the chunk, a length, and the first characters of the quote. Code finds the quote by its
// opening words (at the offset, else anywhere in the chunk, else by fuzzy match), cuts the passage out of the text itself, and
// keeps what it finds at or above the evidence threshold. An item just under it (the judge band) is shown to the model once
// more, without the text, to keep or drop. Everything else is dropped and logged.
// Stage 2 resolves the names into entities; Stage 3 (link, code only) points the items at them.

// ---- What the model returns (strict mode: every field required; nullable fields are T|null) ---------------------------

// The schema is kept flat on purpose: no enumerations and no nullable fields. Anthropic compiles a strict schema into a
// grammar, and the first shape of this one (enums for the kinds, T|null for every optional field) was refused as too large
// ("The compiled grammar is too large"). So a kind is a string that code checks against the ontology (GraphStage3.Check), an
// absent text is "" and an absent id is 0 (ids start at 1).

/// <summary>
/// An item's evidence as the model gives it. In the reply it is one string, "at:len:q" (for example
/// <c>197:64:“There are only two women in the house, Sir Henry,” he answered.</c>): a nested object on every item made the
/// compiled grammar too large for the provider.
/// </summary>
/// <param name="At">Where the quote starts: the character offset from the start of the chunk text.</param>
/// <param name="Len">How many characters the quote runs (one sentence or less).</param>
/// <param name="Q">The first 80 characters of the quote, copied exactly from the chunk.</param>
public sealed record AEv(int At, int Len, string Q)
{
    /// <summary>"at:len:q" → the three parts. A string without the two numbers is taken as the quote alone (offset −1).</summary>
    public static AEv? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var first = text.IndexOf(':');
        var second = first < 0 ? -1 : text.IndexOf(':', first + 1);
        return second > 0 && int.TryParse(text.AsSpan(0, first).Trim(), out var at) && int.TryParse(text.AsSpan(first + 1, second - first - 1).Trim(), out var len)
            ? new AEv(at, len, text[(second + 1)..]) : new AEv(-1, 0, text);
    }
}

/// <param name="Id">The name's number in this answer: 1, 2, 3, … Every other item points at a name by this number.</param>
/// <param name="Name">A name or identifying phrase exactly as written in the chunk (the narrator: the name stated in the instructions).</param>
/// <param name="Type">character or location.</param>
public sealed record AMention(int Id, string Name, string Type);

/// <param name="N">The number of the name in <c>mentions</c>.</param>
/// <param name="Role">A word or two: "speaker", "listener", "narrator", "watched", …; "" when there is none.</param>
public sealed record AWho(int N, string Role);

/// <param name="Id">A number of your own for this answer: 1, 2, 3, …</param>
/// <param name="Kind">narrated or referenced.</param>
/// <param name="When">The text's own time expression, copied; "" when it gives none.</param>
/// <param name="Summary">One sentence, at most 140 characters.</param>
/// <param name="Where">The number of the name of the place where it happens; 0 when the chunk names none.</param>
/// <param name="In">For a referenced event: the id of the narrated event of this answer in which it is mentioned; 0 for a narrated event.</param>
public sealed record AEvent(int Id, string Kind, string Summary, string When, List<AWho> Who, int Where, int In, string Ev);

/// <param name="How">witnessed, told, overheard, inferred, read or recalled.</param>
public sealed record ABy(int Event, string How);

/// <param name="About">The numbers of the names of the people and places the fact is about.</param>
/// <param name="By">Every event of this answer through which characters come to the fact, each with how.</param>
public sealed record AFact(int Id, string Text, List<int> About, List<ABy> By, string Ev);

/// <param name="Fact">The id of a fact of this answer, when the perception is of a fact; else 0.</param>
/// <param name="Who">The number of the name of the character who perceives.</param>
/// <param name="Of">The number of the name of the character perceived, when the perception is of a character; else 0.</param>
/// <param name="Kind">One of the twelve perception kinds.</param>
/// <param name="Cause">The id of the event of this answer that produces it; 0 when it is the fact's discovering event.</param>
public sealed record APerception(int Who, int Fact, int Of, string Kind, int Cause, string Ev);

/// <param name="Fact">The id of a fact of this answer, when the retracted fact is in this chunk; else 0.</param>
/// <param name="Who">The number of the name of the character whose belief ends; 0 when the reader learns the fact itself was false.</param>
public sealed record ARetraction(int Fact, int Who, string Text, List<int> About, string Ev);

/// <param name="A">The number of one name.</param>
/// <param name="B">The number of the other.</param>
public sealed record AIdentity(int A, int B, string Ev);

/// <param name="Type">RELATED_TO or LOCATED_AT.</param>
/// <param name="From">The number of the source's name.</param>
/// <param name="To">The number of the target's name.</param>
/// <param name="Kind">For RELATED_TO: the relation in a word or two; else "".</param>
public sealed record ARel(string Type, int From, int To, string Kind, string Ev);

public sealed record ExtractOutput(List<AMention> Mentions, List<AEvent> Events, List<AFact> Facts, List<APerception> Perceptions, List<ARetraction> Retractions,
    List<AIdentity> Identities, List<ARel> Relations);
/// <summary>Structured output of the judge call: the numbers of the listed items to keep.</summary>
public sealed record JudgeAnswer(List<int> Keep);

// ---- What the stage keeps (extract.json): names, and evidence cut from the text ------------------------------------------

public sealed record NParticipant(string Name, string? Role);

/// <summary>An item's evidence after the check: the passage as the book has it and where it is (book offsets).</summary>
public sealed record NEvidence(string Quote, int CharStart, int CharEnd, double Score, string Method);

public sealed record NEvent(string Id, string Kind, string Summary, string? When, List<NParticipant> Participants, string? Location, string? MentionedIn, NEvidence Evidence);

public sealed record NFact(string Id, string Text, List<string> Subjects, List<XDiscovery> DiscoveredBy, NEvidence Evidence);

public sealed record NPerception(string Character, string? Fact, string? TargetCharacter, string Kind, string? CauseEvent, NEvidence Evidence);

public sealed record NRetraction(string? Fact, string? Character, string Text, List<string> Subjects, NEvidence Evidence);

public sealed record NIdentity(string A, string B, NEvidence Evidence);

public sealed record NRelation(string Type, string Source, string Target, string? Kind, NEvidence Evidence);

/// <summary>One chunk's extraction, with names. Ids are "e1", "f1", … within the chunk.</summary>
/// <param name="Calls">Model calls spent on the chunk (extraction, a split retry, the judge, a glean).</param>
/// <param name="Parts">1; more when the chunk's reply could not be read and it was asked again in halves.</param>
public sealed record ExtractChunk(int Index, int Chapter, int Calls, int Parts, int Returned, int Dropped, int Judged, int JudgeKept, int Unreadable,
    List<NEvent> Events, List<NFact> Facts, List<NPerception> Perceptions, List<NRetraction> Retractions, List<NIdentity> Identities, List<NRelation> Relations);

/// <param name="Thinking"><c>Indexing:StageA:Thinking</c> and <c>--thinking</c>: <c>low</c>, <c>medium</c> (the default, chosen on gold) or <c>high</c> (adaptive
/// thinking at that effort), <c>off</c> (the request says <c>thinking: disabled</c>), <c>default</c> (nothing is sent: the model's own
/// default), or a number (a thinking budget in tokens, for a model that takes one). Anthropic only.</param>
public sealed record ExtractSettings(double EvidenceThreshold = 0.9, bool Glean = false, bool Judge = true, double JudgeLow = 0.80, double JudgeHigh = 0.90,
    int MaxOutputTokens = ExtractStage.DefaultMaxOutputTokens, string Thinking = ExtractSettings.DefaultThinking)
{
    /// <summary>Measured on the Hound's chapters 1–3 with glean: output 41 % of input at <c>low</c>, 18 % at <c>off</c> with fewer
    /// items, 100 % at the model's default.</summary>
    public const string DefaultThinking = "medium";

    /// <summary>What the request carries for <see cref="Thinking"/>: <c>disabled</c>, an effort, a budget, or null (nothing sent).</summary>
    [JsonIgnore]
    public string? ThinkingHint => Thinking?.Trim().ToLowerInvariant() switch
    {
        "off" or "disabled" or "none" or "false" => "disabled",
        "low" or "medium" or "high" => Thinking!.Trim().ToLowerInvariant(),
        { } n when int.TryParse(n, out var budget) && budget > 0 => n,
        _ => null,
    };

    /// <summary>A value <c>Indexing:StageA:Thinking</c> and <c>--thinking</c> take.</summary>
    public static bool IsThinking(string? value) => value?.Trim().ToLowerInvariant() is { } v
        && (v is "off" or "disabled" or "none" or "false" or "low" or "medium" or "high" or "default" || (int.TryParse(v, out var budget) && budget > 0));

    /// <summary>"0.80-0.90" → (0.80, 0.90); null when it can't be read.</summary>
    public static (double Low, double High)? ParseBand(string? band)
    {
        var parts = (band ?? "").Split('-', StringSplitOptions.TrimEntries);
        return parts.Length == 2 && double.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var lo)
            && double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var hi) && lo >= 0 && lo < hi && hi <= 1 ? (lo, hi) : null;
    }
}

/// <param name="Shape">3: names, one sample, evidence by offset.</param>
public sealed record ExtractResult(int Shape, string BookId, int FirstChapter, int LastChapter, ExtractSettings Settings, int ChunkTokens, bool ChapterAligned,
    IReadOnlyList<ExtractChunk> Chunks, int Parallel, int PeakInFlight);

public static class ExtractStage
{
    public const string StageName = Stage1.StageName;
    public const string FileName = "extract.json";
    public const int Shape = 3;

    /// <summary>The output cap of an extraction call. A chapter-sized chunk can return several thousand tokens; a reply cut short
    /// can't be read, and the chunk is then asked again in halves.</summary>
    public const int DefaultMaxOutputTokens = 32000;

    /// <summary>The most characters of the previous part's event summaries put before a chunk (at most about 600 tokens).</summary>
    public const int PreviousEventsChars = 2400;

    /// <summary>The longest passage an item's evidence is cut to.</summary>
    public const int MaxEvidenceChars = 600;

    /// <summary>An event's summary is one sentence of at most this many characters; a longer one is cut at a word.</summary>
    public const int MaxSummaryChars = 140;

    /// <summary>The opening words of a quote (<see cref="AEv.Q"/>) run at most this long; code cuts the rest and keeps its length.</summary>
    public const int MaxQuoteChars = 80;

    public static readonly JsonSerializerOptions Json = Stage1.Json;

    private static string Flat(string s) => string.Join(' ', s.Split((char[])[' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries));

    // ---- Prompt ---------------------------------------------------------------------------------------------------------

    /// <summary>The system block: the skill and the chapter's narrator. The same text for every chunk with the same narrator, so it
    /// is read from the prompt cache after the first call.</summary>
    public static string SystemText(SkillFile skill, ChapterInfo chapter) =>
        skill.Text.TrimEnd() + "\n\n" + (chapter.Narrator is null
            ? "Narrator of this chapter: not named; \"I\" outside quotation marks is the unnamed narrator."
            : $"Narrator of this chapter: {chapter.Narrator}" + (chapter.NarratorForm switch
            {
                "letter" => ", in a letter he or she writes.",
                "diary" => ", in his or her diary.",
                _ => ".",
            }) + $" \"I\" outside quotation marks is {chapter.Narrator}; \"I\" inside a quotation is whoever speaks.");

    /// <summary>The three blocks of the user message: the heading with the previous part's events, the chunk text (the cache
    /// breakpoint ends here), and the request.</summary>
    public static List<AIContent> UserBlocks(ChunkInfo chunk, ChapterInfo chapter, string chunkText, IReadOnlyList<string> previousEvents)
    {
        var head = new StringBuilder($"Chunk {chunk.Index} (chapter {chunk.Chapter}: \"{chapter.Title}\").");
        if (previousEvents.Count > 0)
        {
            head.Append("\nEvents of the part of this chapter before it, for context only (don't return them again):");
            var used = 0;
            foreach (var p in previousEvents)
            {
                if ((used += p.Length + 3) > PreviousEventsChars) break;
                head.Append("\n- ").Append(p);
            }
        }
        return
        [
            new TextContent(head.ToString()),
            new TextContent($"<<<\n{chunkText}\n>>>"),
            new TextContent("Return the names, events, facts, perceptions, retractions, identity reveals and relations in this chunk. Offsets count characters from the first character after the opening <<< line."),
        ];
    }

    public const string GleanPrompt =
        "Some events and facts in this chunk may have been missed. Return only items that are not in the list above (your previous answer), in the same format: "
        + "never an item again, in the same or in other words. Names keep their numbers: in `mentions` list only names you have not listed yet, numbered on from your last one. "
        + "Number new events and facts from 1001. Return empty lists if nothing was missed.";

    /// <summary>"at:len:q" with <c>q</c> cut to <see cref="MaxQuoteChars"/>; <c>len</c> then covers at least the quote as it was given.</summary>
    public static string CutEvidence(string? ev)
    {
        if (AEv.Parse(ev) is not { } e || e.Q.Length <= MaxQuoteChars) return ev ?? "";
        return e.At >= 0 ? $"{e.At}:{Math.Max(e.Len, e.Q.Length)}:{e.Q[..MaxQuoteChars]}" : e.Q[..MaxQuoteChars];
    }

    /// <summary>A summary of at most <see cref="MaxSummaryChars"/> characters, cut at the last word that fits.</summary>
    public static string CutSummary(string? summary)
    {
        var s = (summary ?? "").Trim();
        if (s.Length <= MaxSummaryChars) return s;
        var cut = s.LastIndexOf(' ', MaxSummaryChars);
        return s[..(cut > MaxSummaryChars / 2 ? cut : MaxSummaryChars)].TrimEnd(' ', ',', ';', ':', '—', '-');
    }

    /// <summary>
    /// A reply as it is kept and sent back (the glean call carries the first answer): every quote's opening words cut to
    /// <see cref="MaxQuoteChars"/> and every event summary to <see cref="MaxSummaryChars"/>. The instructions ask for both; this makes it so.
    /// </summary>
    public static ExtractOutput Compact(ExtractOutput o) => new(o.Mentions ?? [],
        (o.Events ?? []).Select(e => e with { Summary = CutSummary(e.Summary), Ev = CutEvidence(e.Ev) }).ToList(),
        (o.Facts ?? []).Select(f => f with { Ev = CutEvidence(f.Ev) }).ToList(),
        (o.Perceptions ?? []).Select(p => p with { Ev = CutEvidence(p.Ev) }).ToList(),
        (o.Retractions ?? []).Select(r => r with { Ev = CutEvidence(r.Ev) }).ToList(),
        (o.Identities ?? []).Select(i => i with { Ev = CutEvidence(i.Ev) }).ToList(),
        (o.Relations ?? []).Select(r => r with { Ev = CutEvidence(r.Ev) }).ToList());

    /// <summary>The reply's names by their numbers (the first of a number wins), on top of <paramref name="before"/> (a glean keeps the first answer's).</summary>
    public static Dictionary<int, string> Names(ExtractOutput output, IReadOnlyDictionary<int, string>? before = null)
    {
        var names = before is null ? [] : new Dictionary<int, string>(before);
        foreach (var m in output.Mentions ?? [])
            if (m.Id > 0 && !string.IsNullOrWhiteSpace(m.Name)) names.TryAdd(m.Id, Flat(m.Name));
        return names;
    }

    // ---- Evidence -------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Finds an item's evidence in the text of a chunk (or of the part of it that was sent). The quote's opening words
    /// (<see cref="AEv.Q"/>) are looked for at the offset the model gave, then anywhere in the text, then by the evidence checker's
    /// fuzzy match; the passage kept runs <see cref="AEv.Len"/> characters from there (to the end of the word, at most
    /// <see cref="MaxEvidenceChars"/>), cut from the text itself. The score is 1 for an exact find, else the fuzzy score of the
    /// opening words. Null when the model gave no opening words.
    /// </summary>
    /// <param name="textStart">The book offset of <paramref name="text"/>'s first character.</param>
    public static NEvidence? Expand(AEv? ev, string text, int textStart)
    {
        var q = ev?.Q?.Trim() ?? "";
        if (q.Length == 0) return null;
        int start;
        double score;
        string method;
        if (ev!.At >= 0 && ev.At + q.Length <= text.Length && string.CompareOrdinal(text, ev.At, q, 0, q.Length) == 0) (start, score, method) = (ev.At, 1, "offset");
        else if (text.IndexOf(q, StringComparison.Ordinal) is var found and >= 0) (start, score, method) = (found, 1, "exact");
        else if (EvidenceChecker.Find(q, text, 0) is { } match) (start, score, method) = (match.CharStart, match.Score, match.Method);
        else return null;

        var length = Math.Clamp(Math.Max(ev.Len, q.Length), 1, MaxEvidenceChars);
        var end = Math.Min(text.Length, start + length);
        while (end < text.Length && end - start < MaxEvidenceChars && !char.IsWhiteSpace(text[end]) && char.IsLetterOrDigit(text[end - 1])) end++;
        while (end > start && char.IsWhiteSpace(text[end - 1])) end--;
        return end <= start ? null : new NEvidence(text[start..end], textStart + start, textStart + end, Math.Round(score, 4), method);
    }

    /// <summary>Every whole-word occurrence of <paramref name="name"/> in <paramref name="text"/> (case-sensitive), as [start, end) offsets.</summary>
    public static IEnumerable<(int Start, int End)> Occurrences(string name, string text)
    {
        if (name.Length == 0) yield break;
        for (var i = text.IndexOf(name, StringComparison.Ordinal); i >= 0; i = text.IndexOf(name, i + 1, StringComparison.Ordinal))
        {
            var before = i == 0 || !char.IsLetterOrDigit(text[i - 1]);
            var after = i + name.Length >= text.Length || !char.IsLetterOrDigit(text[i + name.Length]);
            if (before && after) yield return (i, i + name.Length);
        }
    }

    /// <summary>
    /// The mentions of a part of a chunk, from the names the model listed: every whole-word occurrence of each name in the text
    /// (the model lists a name once; code counts it). An occurrence inside a longer listed name's occurrence belongs to the longer
    /// name ("Baskerville" in "Sir Henry Baskerville" is not a mention of its own). A listed name that is nowhere in the text is
    /// rejected.
    /// </summary>
    public static (List<Mention> Mentions, List<RejectedMention> Rejected) Mentions(IEnumerable<AMention> listed, ChunkInfo chunk, string text, int textStart)
    {
        var mentions = new List<Mention>();
        var rejected = new List<RejectedMention>();
        var taken = new List<(int Start, int End)>();
        foreach (var g in listed.Where(m => !string.IsNullOrWhiteSpace(m.Name)).GroupBy(m => Flat(m.Name)).OrderByDescending(g => g.Key.Length).ThenBy(g => g.Key, StringComparer.Ordinal))
        {
            var first = g.First();
            var type = string.Equals(first.Type?.Trim(), "location", StringComparison.OrdinalIgnoreCase) ? MentionType.Location : MentionType.Character;
            var found = Occurrences(g.Key, text).ToList();
            if (found.Count == 0)
            {
                rejected.Add(new RejectedMention(g.Key, type, chunk.Index, chunk.Chapter, "first", "", "the name is not in the chunk as written", null));
                continue;
            }
            foreach (var (s, e) in found)
            {
                if (taken.Any(t => s >= t.Start && e <= t.End)) continue;
                taken.Add((s, e));
                mentions.Add(new Mention(0, g.Key, type, chunk.Index, chunk.Chapter, "first", textStart + s, textStart + e, g.Key, new EvidenceMatch("exact", 1, textStart + s, textStart + e)));
            }
        }
        return (mentions.OrderBy(m => m.CharStart).ToList(), rejected);
    }

    // ---- One reply into items -------------------------------------------------------------------------------------------

    /// <summary>An item waiting for its evidence verdict: what it is, the evidence found (null: none), and how to add it when kept.</summary>
    public sealed record Pending(string Item, string What, AEv? Given, NEvidence? Found, Action<NEvidence> Keep);

    /// <summary>One part's items, by evidence: kept, borderline (the judge band) and dropped.</summary>
    public sealed class Sorted
    {
        public List<NEvent> Events { get; } = [];
        public List<NFact> Facts { get; } = [];
        public List<NPerception> Perceptions { get; } = [];
        public List<NRetraction> Retractions { get; } = [];
        public List<NIdentity> Identities { get; } = [];
        public List<NRelation> Relations { get; } = [];
        public List<Pending> Borderline { get; } = [];
        public List<GraphReject> Rejected { get; } = [];
        public int Returned { get; set; }
    }

    /// <summary>
    /// Sorts one reply's items by their evidence (<see cref="Expand"/>): at or above the threshold they are kept; in the judge band
    /// they wait in <see cref="Sorted.Borderline"/>; below it, or with an offset and opening words found nowhere, they are dropped and
    /// logged as a quote mismatch. Ids become "e{n}" and "f{n}", with <paramref name="idOffset"/> added (a second part or a glean).
    /// A name's number becomes the name (<paramref name="names"/>; default: the reply's own list); a number no name has is left out.
    /// </summary>
    public static Sorted Sort(ExtractOutput output, ChunkInfo chunk, string text, int textStart, ExtractSettings settings, int idOffset = 0, IReadOnlyDictionary<int, string>? names = null)
    {
        var sorted = new Sorted();
        names ??= Names(output);
        string N(int id) => names.TryGetValue(id, out var name) ? name : "";
        string E(int id) => $"e{id + idOffset}";
        string F(int id) => $"f{id + idOffset}";
        void Take(string item, string what, string? evidence, Action<NEvidence> keep)
        {
            var given = AEv.Parse(evidence);
            sorted.Returned++;
            var found = Expand(given, text, textStart);
            if (found is not null && found.Score >= settings.EvidenceThreshold) { keep(found); return; }
            if (found is not null && settings.Judge && found.Score >= settings.JudgeLow && found.Score < settings.JudgeHigh) { sorted.Borderline.Add(new Pending(item, what, given, found, keep)); return; }
            sorted.Rejected.Add(new GraphReject(item, FailureTags.QuoteMismatch,
                found is null ? $"evidence not in the chunk (offset {given?.At}, \"{Flat(given?.Q ?? "")}\")" : $"evidence not in the chunk (best {found.Method} score {found.Score:0.###} < {settings.EvidenceThreshold})",
                chunk.Index, chunk.Chapter, 0, what));
        }

        foreach (var e in output.Events ?? [])
            Take("event", Flat(e.Summary ?? ""), e.Ev, ev => sorted.Events.Add(new NEvent(E(e.Id), (e.Kind ?? "").Trim().ToLowerInvariant(), (e.Summary ?? "").Trim(), string.IsNullOrWhiteSpace(e.When) ? null : e.When.Trim(),
                (e.Who ?? []).Where(w => N(w.N).Length > 0).Select(w => new NParticipant(N(w.N), string.IsNullOrWhiteSpace(w.Role) ? null : w.Role.Trim())).ToList(),
                N(e.Where).Length == 0 ? null : N(e.Where), e.In > 0 ? E(e.In) : null, ev)));
        foreach (var f in output.Facts ?? [])
            Take("fact", Flat(f.Text ?? ""), f.Ev, ev => sorted.Facts.Add(new NFact(F(f.Id), (f.Text ?? "").Trim(), (f.About ?? []).Select(N).Where(a => a.Length > 0).Distinct().ToList(),
                (f.By ?? []).Select(b => new XDiscovery(E(b.Event), (b.How ?? "").Trim().ToLowerInvariant())).ToList(), ev)));
        foreach (var p in output.Perceptions ?? [])
            Take("perception", $"{N(p.Who)} {p.Kind} {(p.Fact > 0 ? F(p.Fact) : N(p.Of))}", p.Ev, ev => sorted.Perceptions.Add(new NPerception(N(p.Who), p.Fact > 0 ? F(p.Fact) : null,
                p.Fact > 0 || N(p.Of).Length == 0 ? null : N(p.Of), (p.Kind ?? "").Trim().ToLowerInvariant(), p.Cause > 0 ? E(p.Cause) : null, ev)));
        foreach (var r in output.Retractions ?? [])
            Take("retraction", Flat(r.Text ?? ""), r.Ev, ev => sorted.Retractions.Add(new NRetraction(r.Fact > 0 ? F(r.Fact) : null, N(r.Who).Length == 0 ? null : N(r.Who), (r.Text ?? "").Trim(),
                (r.About ?? []).Select(N).Where(a => a.Length > 0).Distinct().ToList(), ev)));
        foreach (var i in output.Identities ?? [])
            Take("identity", $"{N(i.A)} = {N(i.B)}", i.Ev, ev => sorted.Identities.Add(new NIdentity(N(i.A), N(i.B), ev)));
        foreach (var r in output.Relations ?? [])
            Take("relation", $"{r.Type} {N(r.From)} → {N(r.To)}", r.Ev, ev => sorted.Relations.Add(new NRelation((r.Type ?? "").Trim().ToUpperInvariant(), N(r.From), N(r.To), string.IsNullOrWhiteSpace(r.Kind) ? null : r.Kind.Trim(), ev)));
        return sorted;
    }

    /// <summary>The judge prompt: the borderline items with the quote the model gave and the nearest passage of the chunk. No chunk text.</summary>
    public static string JudgePrompt(IReadOnlyList<Pending> items)
    {
        var sb = new StringBuilder("For each item below, the opening words you quoted as its evidence were not found exactly in the chunk; the nearest passage of the chunk is given. "
            + "Keep an item only when the passage found is the evidence you meant and supports the item. Return the numbers of the items to keep.\n");
        for (var i = 0; i < items.Count; i++)
            sb.Append($"{i + 1}. {items[i].Item}: \"{Flat(items[i].What)}\"\n   quoted: \"{Flat(items[i].Given?.Q ?? "")}\"\n   found:  \"{Flat(items[i].Found!.Quote)}\"\n");
        return sb.ToString();
    }

    // ---- The run --------------------------------------------------------------------------------------------------------

    private static ChatOptions Options(ExtractSettings settings)
    {
        var options = Stage1.StrictOptions();
        options.MaxOutputTokens = settings.MaxOutputTokens;
        return options;
    }

    /// <summary>A chunk's text cut in two at the paragraph break nearest its middle; null when it has none.</summary>
    public static (int Start, int End)[]? Halves(string bookText, int start, int end)
    {
        var middle = start + (end - start) / 2;
        var breaks = new List<int>();
        for (var i = bookText.IndexOf("\n\n", start, end - start, StringComparison.Ordinal); i >= 0 && i < end - 2; i = bookText.IndexOf("\n\n", i + 2, end - i - 2, StringComparison.Ordinal)) breaks.Add(i + 2);
        for (var i = bookText.IndexOf("\r\n\r\n", start, end - start, StringComparison.Ordinal); i >= 0 && i < end - 4; i = bookText.IndexOf("\r\n\r\n", i + 4, end - i - 4, StringComparison.Ordinal)) breaks.Add(i + 4);
        var cut = breaks.Where(b => b > start + 200 && b < end - 200).OrderBy(b => Math.Abs(b - middle)).Cast<int?>().FirstOrDefault();
        return cut is { } c ? [(start, c), (c, end)] : null;
    }

    public static async Task<(Stage1Result Mentions, ExtractResult Extract)> RunAsync(IChatClient client, LlmCallContext? context, SkillFile skill, Stage0Result stage0, string bookText,
        int firstChapter, int lastChapter, ExtractSettings settings, StageRunner runner, RunFolder? run, CancellationToken ct, Action<int, int>? progress = null,
        Action<ChunkInfo, string>? called = null)
    {
        if (context is not null)
        {
            context.BookId = stage0.BookId;
            context.SkillName = skill.Name;
            context.SkillVersion = skill.Version.ToString();
        }
        var chapters = stage0.Chapters.Where(c => c.Number >= firstChapter && c.Number <= lastChapter).ToList();
        var total = stage0.Chunks.Count(c => c.Chapter >= firstChapter && c.Chapter <= lastChapter);
        var done = 0;
        var options = Options(settings);

        // One call: the reply, or null when it can't be read (logged).
        async Task<T?> Ask<T>(List<ChatMessage> messages, ChunkInfo chunk, CacheHints? cache, string what) where T : class
        {
            try
            {
                return await runner.CallAsync(async () => (await client.GetResponseAsync<T>(messages, Json, options, useJsonSchemaResponseFormat: true, ct)).Result, ct, chunk.Chapter, chunk.Index, cache);
            }
            catch (JsonException ex)
            {
                run?.AppendFailure(new { type = FailureTags.UnreadableReply, stage = StageName, runId = run.RunId, bookId = stage0.BookId, chapter = chunk.Chapter, chunkIndex = chunk.Index, call = what, reason = ex.Message });
                return null;
            }
        }

        // One part of a chunk: the extraction call, an optional glean on the cached text, then the judge on the borderline items.
        async Task<(Sorted? Items, List<Mention> Mentions, List<RejectedMention> RejectedMentions, int Calls, int Judged, int JudgeKept)> Part(ChunkInfo chunk, ChapterInfo chapter,
            int start, int end, IReadOnlyList<string> previous, int idOffset)
        {
            var text = bookText[start..end];
            var system = new ChatMessage(ChatRole.System, SystemText(skill, chapter));
            var user = new ChatMessage(ChatRole.User, UserBlocks(chunk, chapter, text, previous));
            var cache = new CacheHints("1h", 1, settings.ThinkingHint);
            var calls = 1;
            called?.Invoke(chunk, "extract");
            var output = await Ask<ExtractOutput>([system, user], chunk, cache, "extract");
            if (output is null) return (null, [], [], calls, 0, 0);
            output = Compact(output);
            var names = Names(output);
            var sorted = Sort(output, chunk, text, start, settings, idOffset, names);
            var listed = (output.Mentions ?? []).ToList();
            if (settings.Glean)
            {
                calls++;
                called?.Invoke(chunk, "glean");
                var more = await Ask<ExtractOutput>([system, user, new ChatMessage(ChatRole.Assistant, JsonSerializer.Serialize(output, Json)), new ChatMessage(ChatRole.User, GleanPrompt)], chunk, cache, "glean");
                if (more is not null)
                {
                    more = Compact(more);
                    var extra = Sort(more, chunk, text, start, settings, idOffset, Names(more, names));
                    // The glean is asked for additions only; an item it gives again (the same passage, the same words) is left out.
                    static bool Again(NEvidence a, NEvidence b, string x, string y) => a.CharStart < b.CharEnd && b.CharStart < a.CharEnd && TextSimilarity.Score(x, y) >= 0.9;
                    sorted.Events.AddRange(extra.Events.Where(e => sorted.Events.All(x => x.Id != e.Id && !(x.Kind == e.Kind && Again(x.Evidence, e.Evidence, x.Summary, e.Summary)))));
                    sorted.Facts.AddRange(extra.Facts.Where(f => sorted.Facts.All(x => x.Id != f.Id && !Again(x.Evidence, f.Evidence, x.Text, f.Text))));
                    sorted.Perceptions.AddRange(extra.Perceptions);
                    sorted.Retractions.AddRange(extra.Retractions);
                    sorted.Identities.AddRange(extra.Identities);
                    sorted.Relations.AddRange(extra.Relations);
                    sorted.Borderline.AddRange(extra.Borderline);
                    sorted.Rejected.AddRange(extra.Rejected);
                    sorted.Returned += extra.Returned;
                    listed.AddRange(more.Mentions ?? []);
                }
            }
            var (mentions, rejectedMentions) = Mentions(listed, chunk, text, start);
            // The narrator is listed by the name the instructions give, which the chunk need not write: no failure.
            if (chapter.Narrator is { } narrator) rejectedMentions.RemoveAll(r => string.Equals(r.Text, narrator, StringComparison.OrdinalIgnoreCase));
            int judged = sorted.Borderline.Count, kept = 0;
            if (judged > 0)
            {
                calls++;
                called?.Invoke(chunk, "judge");
                var answer = await Ask<JudgeAnswer>([new ChatMessage(ChatRole.System, SystemText(skill, chapter)), new ChatMessage(ChatRole.User, JudgePrompt(sorted.Borderline))], chunk, new CacheHints("1h", Thinking: settings.ThinkingHint), "judge");
                var keep = (answer?.Keep ?? []).ToHashSet();
                for (var i = 0; i < sorted.Borderline.Count; i++)
                {
                    var b = sorted.Borderline[i];
                    if (keep.Contains(i + 1)) { b.Keep(b.Found!); kept++; }
                    else sorted.Rejected.Add(new GraphReject(b.Item, FailureTags.QuoteMismatch, $"evidence in the judge band ({b.Found!.Score:0.###}) and not kept by the judge", chunk.Index, chunk.Chapter, 0, b.What));
                }
            }
            return (sorted, mentions, rejectedMentions, calls, judged, kept);
        }

        // A chunk: its text as one part; when that reply can't be read (cut short by the output cap), once more in two halves.
        async Task<(ExtractChunk Chunk, List<Mention> Mentions, List<RejectedMention> Rejected, List<GraphReject> Dropped)> Chunk(ChunkInfo chunk, ChapterInfo chapter, IReadOnlyList<string> previous)
        {
            using var span = IndexingTelemetry.Source.StartActivity($"chunk {chunk.Index}");
            span?.SetTag(IndexingTelemetry.RunId, run?.RunId);
            span?.SetTag(IndexingTelemetry.Stage, StageName);
            span?.SetTag(IndexingTelemetry.BookId, stage0.BookId);
            span?.SetTag(IndexingTelemetry.Chapter, chunk.Chapter);
            span?.SetTag(IndexingTelemetry.ChunkIndex, chunk.Index);
            var whole = await Part(chunk, chapter, chunk.CharStart, chunk.CharEnd, previous, 0);
            var parts = new List<(Sorted? Items, List<Mention> Mentions, List<RejectedMention> RejectedMentions, int Calls, int Judged, int JudgeKept)> { whole };
            var unreadable = whole.Items is null ? 1 : 0;
            if (whole.Items is null && Halves(bookText, chunk.CharStart, chunk.CharEnd) is { } halves)
            {
                var a = await Part(chunk, chapter, halves[0].Start, halves[0].End, previous, 0);
                var b = await Part(chunk, chapter, halves[1].Start, halves[1].End, a.Items?.Events.Select(e => e.Summary).ToList() ?? previous, 5000);
                parts = [whole with { Mentions = [], RejectedMentions = [] }, a, b];
                unreadable += (a.Items is null ? 1 : 0) + (b.Items is null ? 1 : 0);
            }
            var items = parts.Select(p => p.Items).OfType<Sorted>().ToList();
            var dropped = items.SelectMany(s => s.Rejected).ToList();
            var result = new ExtractChunk(chunk.Index, chunk.Chapter, parts.Sum(p => p.Calls), Math.Max(1, parts.Count - 1), items.Sum(s => s.Returned), dropped.Count, parts.Sum(p => p.Judged), parts.Sum(p => p.JudgeKept),
                unreadable, items.SelectMany(s => s.Events).ToList(), items.SelectMany(s => s.Facts).ToList(), items.SelectMany(s => s.Perceptions).ToList(), items.SelectMany(s => s.Retractions).ToList(),
                items.SelectMany(s => s.Identities).ToList(), items.SelectMany(s => s.Relations).ToList());
            span?.SetTag("events.accepted", result.Events.Count);
            span?.SetTag("facts.accepted", result.Facts.Count);
            span?.SetTag("items.rejected", dropped.Count);
            progress?.Invoke(Interlocked.Increment(ref done), total);
            return (result, parts.SelectMany(p => p.Mentions).ToList(), parts.SelectMany(p => p.RejectedMentions).ToList(), dropped);
        }

        // Chapters run side by side (the runner's gate bounds the calls); the parts of one chapter run in order, each with the
        // events of the part before it.
        var perChapter = await StageRunner.ForEachAsync(chapters, async (chapter, _) =>
        {
            var results = new List<(ExtractChunk Chunk, List<Mention> Mentions, List<RejectedMention> Rejected, List<GraphReject> Dropped)>();
            IReadOnlyList<string> previous = [];
            foreach (var chunk in stage0.Chunks.Where(c => c.Chapter == chapter.Number).OrderBy(c => c.CharStart))
            {
                ct.ThrowIfCancellationRequested();
                var r = await Chunk(chunk, chapter, previous);
                results.Add(r);
                previous = r.Chunk.Events.Select(e => $"{e.Summary}{(e.Kind == EventKinds.Referenced ? " (mentioned, not narrated)" : "")}").ToList();
            }
            return results;
        });

        var all = perChapter.SelectMany(r => r).OrderBy(r => r.Chunk.Index).ToList();
        if (run is not null)
            foreach (var r in all.SelectMany(x => x.Dropped))
                run.AppendFailure(new { type = r.Tag, stage = StageName, runId = run.RunId, bookId = stage0.BookId, chapter = r.Chapter, chunkIndex = r.Chunk, item = r.Item, reason = r.Reason, what = r.What });
        var rejectedMentions = all.SelectMany(x => x.Rejected).ToList();
        if (run is not null)
            foreach (var r in rejectedMentions)
                run.AppendFailure(new { type = FailureTags.QuoteMismatch, stage = StageName, runId = run.RunId, bookId = stage0.BookId, chapter = r.Chapter, chunkIndex = r.Chunk, item = "mention", reason = r.Reason, what = r.Text });
        var (mentions, duplicates) = Stage1.Dedupe(all.SelectMany(x => x.Mentions));
        var byChunk = stage0.Chunks.ToDictionary(c => c.Index);
        var summaries = all.Select(x => new ChunkSummary(x.Chunk.Index, x.Chunk.Chapter, byChunk[x.Chunk.Index].CharStart, byChunk[x.Chunk.Index].CharEnd,
            x.Mentions.Count + x.Rejected.Count, 0, x.Mentions.Count, x.Rejected.Count)).ToList();
        var stage1 = new Stage1Result(stage0.BookId, firstChapter, lastChapter, settings.Glean, settings.EvidenceThreshold, summaries, mentions, rejectedMentions, duplicates, 1, 1, null);
        var extract = new ExtractResult(Shape, stage0.BookId, firstChapter, lastChapter, settings, stage0.ChunkSettings.ChunkTokens, stage0.ChunkSettings.ChapterAligned,
            all.Select(x => x.Chunk).ToList(), runner.Limit, runner.Peak);
        return (stage1, extract);
    }

    /// <summary>Counts for extract.json and the console.</summary>
    public static Dictionary<string, object> Counts(Stage1Result mentions, ExtractResult r) => new()
    {
        ["chunks"] = r.Chunks.Count,
        ["calls"] = r.Chunks.Sum(c => c.Calls),
        ["chunksAskedInHalves"] = r.Chunks.Count(c => c.Parts > 1),
        ["unreadableReplies"] = r.Chunks.Sum(c => c.Unreadable),
        ["returned"] = r.Chunks.Sum(c => c.Returned),
        ["dropped"] = r.Chunks.Sum(c => c.Dropped),
        ["judged"] = r.Chunks.Sum(c => c.Judged),
        ["judgeKept"] = r.Chunks.Sum(c => c.JudgeKept),
        ["names"] = mentions.Mentions.Select(m => (m.Type, m.Text)).Distinct().Count(),
        ["mentions"] = mentions.Mentions.Count,
        ["namesNotInText"] = mentions.Rejected.Count,
        ["events"] = r.Chunks.Sum(c => c.Events.Count),
        ["facts"] = r.Chunks.Sum(c => c.Facts.Count),
        ["perceptions"] = r.Chunks.Sum(c => c.Perceptions.Count),
        ["retractions"] = r.Chunks.Sum(c => c.Retractions.Count),
        ["identities"] = r.Chunks.Sum(c => c.Identities.Count),
        ["relations"] = r.Chunks.Sum(c => c.Relations.Count),
        ["parallel"] = r.Parallel,
        ["peakInFlight"] = r.PeakInFlight,
    };
}
