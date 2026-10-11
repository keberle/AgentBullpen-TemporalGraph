using System.Globalization;
using System.Text;
using TemporalGraph.Cli.Scoring;
using TemporalGraph.Data;

namespace TemporalGraph.Cli.Indexing;

// Stage 3 of the pipeline: "link". Code only, no model.
// Stage 1 returned its items with the names the text uses; Stage 2 resolved the book's names into entities. Here every name in
// an item is pointed at an entity, in this order:
//   0. the narrator: "I", "the narrator" and the chapter's narrator are the chapter's narrator: the one Stage 0 read from the
//      chapter titles, else the name the extraction most often gave the role "narrator" in that chapter;
//   1. chain membership: an entity that has a mention with exactly this text inside the item's evidence, else the nearest such
//      mention in the chunk (two people called "Baskerville" are told apart by where the name stands);
//   2. exact alias: the one entity that goes by this name (several: the one most mentioned in the chapter);
//   3. whole-word alias in the chapter: an entity mentioned in this chapter one of whose names contains this name as whole words,
//      or is contained in it ("Holmes" for "Mr. Sherlock Holmes's");
//   4. BookNLP's coreference at the mention: where the name stands in the evidence (else nearest to it in the chunk), the
//      coreference chain that covers it, when most of that chain's names belong to one entity ("the sea-cook" → Silver);
//   5. spelling: the alias of an entity mentioned in this or the previous chapter at Jaro-Winkler 0.92 or more;
//   6. a pronoun nothing resolved: the chapter's narrator for a first-person one, else the character last mentioned
//      before it in the paragraph; a fact whose subject came this way is flagged subjectSource = inferred_pronoun;
//   7. a name the book has not used by the end of the chunk (the extraction's own knowledge of the story, not the text): the
//      words the text itself uses there ("Miss Stapleton" for "Beryl Stapleton", "the lookout" for "Lookout") go through the
//      steps above; when they make a new entity it is of kind descriptive, so that an identity reveal can join it later;
//   8. a new entity with this one name, flagged unlinked (kind descriptive when the name is a description: "the stranger").
// A name is never dropped.
// The items then go through the same checks as before the one-pass pipeline (GraphStage3.Check: enumerations, endpoints, the evidence quote in
// the chunk) and the same run-wide assembly (the Fact dedupe: same subjects and text similarity at or above the threshold are
// one node whose DISCOVERED edges are the union). A fact left without a discovering event gets one by code, flagged
// inferred_by_code. The result is a stage-3.json of the full-schema shape, so Stages 4 and 5, build-db, the leak tests and the scorer
// read it as they did.

/// <param name="How"><c>narrator</c>, <c>chain</c>, <c>alias</c>, <c>word</c>, <c>coref</c>, <c>fuzzy</c>, <c>pronoun</c>, <c>described</c>, <c>new</c> or <c>dropped</c>
/// (<c>dropped</c>: only a name with no letter or digit in it).</param>
/// <param name="Was">What the earlier order did with the name (<c>dropped</c>, <c>new</c> or the same step), for the before/after count.</param>
/// <param name="Nearest">For a name steps 0–3 did not link: the nearest alias of any entity by Jaro-Winkler, and its score.</param>
public sealed record LinkedName(string Name, int Chunk, int? Entity, string How, string? Was = null, string? Nearest = null, double NearestScore = 0);

/// <summary>An entity Stage 3 added for a name no Stage 2 entity goes by (one name, its occurrences in the chunks that use it).</summary>
/// <param name="Kind"><c>descriptive</c> (<see cref="Entity.Descriptive"/>) or null.</param>
public sealed record UnlinkedEntity(int Id, string Name, MentionType Type, IReadOnlyList<(int Start, int End, int Chapter)> Mentions, string? Kind = null);

/// <param name="Inferred">Facts whose discovering event was given by code: the fact, the event, how, and the rule (<c>contains</c> or <c>nearest</c>).</param>
public sealed record LinkResult(GraphStage3Result Graph, IReadOnlyList<UnlinkedEntity> Unlinked, IReadOnlyDictionary<string, int> NamesByHow, IReadOnlyList<LinkedName> Dropped,
    IReadOnlyList<LinkedName> Names, IReadOnlyList<(string Fact, string Text, string Event, string How)> Inferred, IReadOnlyList<GraphReject>? Rejected = null);

/// <summary>
/// BookNLP's coreference chains read against Stage 2's entities: a chain stands for an entity when at least six in
/// ten of its names and noun phrases that are also mentions of an entity are mentions of that one. BookNLP alone over-merges, so
/// the vote keeps a chain that mixes two people from deciding anything.
/// </summary>
public sealed class ChainIndex
{
    private readonly List<ChainMention> _mentions;
    private readonly int[] _starts;
    private readonly Dictionary<int, int> _entityOf = [];

    public ChainIndex(IReadOnlyList<ChainMention> chains, IReadOnlyList<EntityRef> entities)
    {
        _mentions = chains.OrderBy(m => m.CharStart).ThenBy(m => m.CharEnd).ToList();
        _starts = _mentions.Select(m => m.CharStart).ToArray();
        var votes = new Dictionary<int, Dictionary<int, int>>();
        foreach (var e in entities)
            foreach (var m in e.Mentions)
                foreach (var c in Covering(m.Start, m.End).Where(c => c.Prop != "PRON"))
                {
                    var of = votes.TryGetValue(c.Chain, out var v) ? v : votes[c.Chain] = [];
                    of[e.Id] = of.GetValueOrDefault(e.Id) + 1;
                }
        foreach (var (chain, of) in votes)
        {
            var top = of.OrderByDescending(v => v.Value).ThenBy(v => v.Key).First();
            if (top.Value * 10 >= of.Values.Sum() * 6) _entityOf[chain] = top.Key;
        }
    }

    public int Chains => _entityOf.Count;

    /// <summary>The chain mentions that stand where [start, end) stands: the same span, or one inside the other with the longer at
    /// most twice the shorter and four characters ("Sir Henry" in "Sir Henry Baskerville", not a name inside a long clause).</summary>
    private IEnumerable<ChainMention> Covering(int start, int end)
    {
        var i = Array.BinarySearch(_starts, Math.Max(0, start - 2 * (end - start) - 4));
        if (i < 0) i = ~i;
        while (i > 0 && i < _starts.Length && _starts[i - 1] == _starts[i]) i--;
        for (; i < _mentions.Count && _mentions[i].CharStart < end; i++)
        {
            var m = _mentions[i];
            var inside = m.CharStart >= start && m.CharEnd <= end;
            var around = m.CharStart <= start && m.CharEnd >= end;
            if (!inside && !around) continue;
            int a = end - start, b = m.CharEnd - m.CharStart;
            if (Math.Max(a, b) <= 2 * Math.Min(a, b) + 4) yield return m;
        }
    }

    /// <summary>The entity of the chain that covers [start, end), and BookNLP's category of that mention; null when no chain with an entity does.</summary>
    public (int Entity, string Cat)? At(int start, int end)
    {
        foreach (var m in Covering(start, end).OrderBy(m => Math.Abs(m.CharEnd - m.CharStart - (end - start))))
            if (_entityOf.TryGetValue(m.Chain, out var entity)) return (entity, m.Cat);
        return null;
    }

    private static bool Fits(ChainMention m, MentionType? want) => want switch
    {
        MentionType.Location => m.Cat is "LOC" or "GPE" or "FAC",
        MentionType.Character => m.Cat is "PER",
        _ => true,
    };

    /// <summary>The shortest name or noun phrase (not a pronoun, at most 60 characters) that holds [start, end): "the lookout" around "lookout".</summary>
    public (int Start, int End)? PhraseAround(int start, int end) =>
        _mentions.Where(m => m.Prop != "PRON" && m.CharStart <= start && m.CharEnd >= end && m.CharEnd - m.CharStart <= 60)
            .OrderBy(m => m.CharEnd - m.CharStart).Select(m => ((int, int)?)(m.CharStart, m.CharEnd)).FirstOrDefault();

    /// <summary>The first noun phrase of the wanted kind inside [start, end) (else the first name there); null when there is none.</summary>
    public (int Start, int End)? PhraseIn(int start, int end, MentionType? want) =>
        _mentions.Where(m => m.Prop != "PRON" && m.CharStart >= start && m.CharEnd <= end && m.CharEnd - m.CharStart <= 60 && Fits(m, want))
            .OrderBy(m => m.Prop == "NOM" ? 0 : 1).ThenBy(m => m.CharStart).Select(m => ((int, int)?)(m.CharStart, m.CharEnd)).FirstOrDefault();

    /// <summary>BookNLP's category of the mention at [start, end) (LOC, GPE and FAC are places); null when it has none there.</summary>
    public string? CategoryAt(int start, int end) => Covering(start, end).OrderBy(m => Math.Abs(m.CharEnd - m.CharStart - (end - start))).Select(m => m.Cat).FirstOrDefault();
}

public sealed class Linker
{
    private static readonly HashSet<string> NarratorWords = ["i", "me", "myself", "the narrator", "narrator"];

    private static readonly HashSet<string> Pronouns =
        ["i", "me", "my", "myself", "he", "him", "his", "himself", "she", "her", "hers", "herself", "it", "its", "itself", "we", "us", "our", "ourselves", "they", "them", "their", "themselves", "you", "your", "yourself", "one", "who", "someone", "somebody"];

    private static readonly HashSet<string> FirstPerson = ["i", "me", "my", "myself", "we", "us", "our", "ourselves"];
    private static readonly HashSet<string> Things = ["it", "its", "itself"];

    /// <summary>The spelling step links at this Jaro-Winkler score or above.</summary>
    public const double FuzzyThreshold = 0.92;

    private readonly List<EntityRef> _entities;
    private readonly Stage0Result _stage0;
    private readonly string _bookText;
    private readonly ChainIndex? _chains;
    private readonly IReadOnlyDictionary<int, string> _narrators;
    private readonly Dictionary<int, int?> _narratorEntity = [];
    private readonly Dictionary<(string, MentionType), int> _added = [];
    private readonly Dictionary<int, List<(int Start, int End, int Chapter)>> _addedMentions = [];
    private readonly Dictionary<int, string> _addedWas = [];
    private readonly Dictionary<int, string> _addedKind = [];
    private readonly Dictionary<string, List<int>> _byAlias = [];
    private readonly Dictionary<string, List<int>> _byFolded = [];
    private readonly Dictionary<int, HashSet<int>> _chaptersOf = [];
    private readonly List<(string Key, string Alias, int Entity)> _aliasKeys = [];
    private readonly Dictionary<string, (string? Alias, double Score)> _nearest = [];
    private int _nextId;

    public List<LinkedName> Log { get; } = [];

    /// <param name="chains">BookNLP's mentions of the run (stage2/booknlp); null or empty: step 4 is skipped.</param>
    /// <param name="narrators">Chapter → the name the extraction gave the role "narrator" most often there.</param>
    public Linker(IReadOnlyList<EntityRef> entities, Stage0Result stage0, string bookText, IReadOnlyList<ChainMention>? chains = null, IReadOnlyDictionary<int, string>? narrators = null)
    {
        (_entities, _stage0, _bookText) = (entities.ToList(), stage0, bookText);
        _chains = chains is { Count: > 0 } ? new ChainIndex(chains, entities) : null;
        _narrators = narrators ?? new Dictionary<int, string>();
        _nextId = entities.Count == 0 ? 1 : entities.Max(e => e.Id) + 1;
        foreach (var e in entities) Index(e);
    }

    public IReadOnlyList<EntityRef> Entities => _entities;

    private int ChapterOf(int offset) => _stage0.Chapters.FirstOrDefault(c => offset >= c.CharStart && offset < c.CharEnd)?.Number ?? 0;

    private void Index(EntityRef e)
    {
        foreach (var alias in e.Aliases.Append(e.Name).Concat(e.Mentions.Select(m => m.Text)).Distinct())
        {
            var key = Key(alias);
            var list = _byAlias.TryGetValue(key, out var have) ? have : _byAlias[key] = [];
            if (list.Contains(e.Id)) continue;
            list.Add(e.Id);
            _aliasKeys.Add((key, alias, e.Id));
            var folded = _byFolded.TryGetValue(Fold(key), out var same) ? same : _byFolded[Fold(key)] = [];
            if (!folded.Contains(e.Id)) folded.Add(e.Id);
        }
        _chaptersOf[e.Id] = e.Mentions.Select(m => ChapterOf(m.Start)).ToHashSet();
    }

    /// <summary>A name as it is compared: the stage-2 normal form, a possessive ending left off.</summary>
    public static string Key(string name)
    {
        var n = name.Trim();
        foreach (var ending in new[] { "’s", "'s" })
            if (n.EndsWith(ending, StringComparison.Ordinal)) n = n[..^2];
        return Stage2.NormalizeName(n);
    }

    /// <summary>A name without its accents ("La Houdinière" as the extraction may write it: "La Houdiniere").</summary>
    public static string Fold(string s)
    {
        if (s.All(char.IsAscii)) return s;
        var sb = new StringBuilder(s.Length);
        foreach (var c in s.Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    private static readonly string[] Leading = ["the ", "a ", "an ", "my ", "his ", "her ", "our ", "their ", "your "];

    private static readonly HashSet<string> Honorifics =
        ["mr", "mrs", "miss", "ms", "m", "mme", "mlle", "madame", "monsieur", "mademoiselle", "dr", "doctor", "sir", "lady", "lord", "captain", "capt", "colonel", "major", "squire", "father", "mother"];

    /// <summary>
    /// The spelling step's score (step 5): Jaro-Winkler of two names without their leading article or possessive, so that "the maid"
    /// and "the lad" are compared as "maid" and "lad". 0 when what is left of either is under four letters, or when the two begin
    /// with different forms of address ("Mr. Vandeleur" is not "Mrs. Vandeleur").
    /// </summary>
    public static double Spelling(string a, string b)
    {
        static string Bare(string key) => Leading.FirstOrDefault(l => key.StartsWith(l, StringComparison.Ordinal)) is { } lead ? key[lead.Length..] : key;
        static string? Honorific(string key) => key.Split(' ')[0].TrimEnd('.') is var first && Honorifics.Contains(first) ? first : null;
        var (x, y) = (Bare(a), Bare(b));
        if (x.Length < 4 || y.Length < 4) return 0;
        if (Honorific(x) is { } hx && Honorific(y) is { } hy && hx != hy) return 0;
        return Stage2.JaroWinkler(x, y);
    }

    private static bool WholeWords(string inner, string outer) =>
        inner.Length > 0 && outer.Length > inner.Length && (" " + outer + " ").Contains(" " + inner + " ", StringComparison.Ordinal);

    /// <summary>The entity a name in an item of <paramref name="chunk"/> stands for; null when it stands for none (logged).</summary>
    /// <param name="want">The type the slot needs (a participant is a Character, a place a Location); null: either.</param>
    public int? Resolve(string? name, MentionType? want, NEvidence evidence, ChunkInfo chunk) => Linked(name, want, evidence, chunk)?.Entity;

    /// <summary>As <see cref="Resolve"/>, with how the name was linked; null for no name at all.</summary>
    public LinkedName? Linked(string? name, MentionType? want, NEvidence evidence, ChunkInfo chunk)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var found = Find(name.Trim(), want, evidence, chunk, narratorName: false);
        Log.Add(found);
        return found;
    }

    /// <summary>The kind of an entity this stage added (<c>descriptive</c>), else null.</summary>
    public string? KindOf(int id) => _addedKind.GetValueOrDefault(id);

    /// <summary>The narrator of a chapter: the entity of Stage 0's narrator, else of the name the extraction gave the role (step 0).</summary>
    public int? NarratorOf(ChunkInfo chunk)
    {
        var chapter = _stage0.Chapters.Single(c => c.Number == chunk.Chapter);
        if (GraphStage3.NarratorEntity(chapter, _entities) is { } told) return told.Id;
        if (_narratorEntity.TryGetValue(chunk.Chapter, out var known)) return known;
        int? entity = null;
        if (_narrators.TryGetValue(chunk.Chapter, out var name) && !NarratorWords.Contains(Key(name)))
        {
            var found = Find(name, MentionType.Character, new NEvidence("", chunk.CharStart, chunk.CharStart, 1, "narrator"), chunk, narratorName: true);
            // A name only this step would make a new entity for is no narrator: nothing ties it to anyone.
            entity = found.How is "new" or "dropped" ? null : found.Entity;
        }
        return _narratorEntity[chunk.Chapter] = entity;
    }

    private (string? Alias, double Score) Nearest(string key)
    {
        if (_nearest.TryGetValue(key, out var have)) return have;
        (string? Alias, double Score) best = (null, 0);
        foreach (var (k, alias, _) in _aliasKeys)
        {
            var score = Stage2.JaroWinkler(key, k);
            if (score > best.Score) best = (alias, score);
        }
        return _nearest[key] = (best.Alias, Math.Round(best.Score, 4));
    }

    private LinkedName Find(string name, MentionType? want, NEvidence evidence, ChunkInfo chunk, bool narratorName, bool described = false)
    {
        bool Fits(EntityRef e) => want is null || e.Type == want;
        EntityRef Entity(int id) => _entities.First(e => e.Id == id);
        var chapter = _stage0.Chapters.Single(c => c.Number == chunk.Chapter);
        var key = Key(name);
        // A later use of a name this stage made an entity for counts as the first did in the earlier order (a drop, or a new entity).
        LinkedName Linked(int? id, string how) => new(name, chunk.Index, id, how, id is { } linked && _addedWas.TryGetValue(linked, out var first) ? first : how);

        // 0. The narrator.
        var asNarrator = !narratorName && want != MentionType.Location && (NarratorWords.Contains(key) || (chapter.Narrator is { } narrator && Key(narrator) == key));
        if (asNarrator && GraphStage3.NarratorEntity(chapter, _entities) is { } told) return Linked(told.Id, "narrator");

        // 1. Chain membership: a mention with exactly this text, inside the evidence, else the nearest one in the chunk.
        var here = _entities.Where(Fits).SelectMany(e => e.Mentions.Where(m => m.Start >= chunk.CharStart && m.End <= chunk.CharEnd && Key(m.Text) == key).Select(m => (e.Id, m.Start, m.End))).ToList();
        if (here.Count > 0)
        {
            var inside = here.Where(m => m.Start >= evidence.CharStart && m.End <= evidence.CharEnd).ToList();
            if (inside.Count > 0) return Linked(inside.GroupBy(m => m.Id).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).First().Key, "chain");
            return Linked(here.OrderBy(m => Math.Abs(m.Start - evidence.CharStart)).ThenBy(m => m.Id).First().Id, "chain");
        }

        // 2. Exact alias.
        int InChapter(int id) => Entity(id).Mentions.Count(m => m.Start >= chapter.CharStart && m.End <= chapter.CharEnd);
        if (_byAlias.TryGetValue(key, out var ids))
        {
            var fitting = ids.Distinct().Where(id => Fits(Entity(id))).ToList();
            if (fitting.Count > 0)
                return Linked(fitting.OrderByDescending(InChapter).ThenByDescending(id => Entity(id).Mentions.Count).ThenBy(id => id).First(), "alias");
        }
        // The same without accents: the extraction does not always keep them.
        else if (_byFolded.TryGetValue(Fold(key), out var folded) && folded.Where(id => Fits(Entity(id))).ToList() is { Count: > 0 } unaccented)
            return Linked(unaccented.OrderByDescending(InChapter).ThenByDescending(id => Entity(id).Mentions.Count).ThenBy(id => id).First(), "alias");

        // 3. A whole-word alias of an entity mentioned in this chapter (never for a pronoun: "her" is not "her father").
        var pronoun = Pronouns.Contains(key);
        var near = pronoun ? [] : _entities.Where(e => Fits(e) && _chaptersOf[e.Id].Contains(chunk.Chapter))
            .Select(e => (e, Best: e.Aliases.Append(e.Name).Select(Key).Where(a => WholeWords(key, a) || WholeWords(a, key)).Select(a => Math.Min(a.Length, key.Length)).DefaultIfEmpty(0).Max()))
            .Where(x => x.Best > 2).OrderByDescending(x => x.Best).ThenByDescending(x => InChapter(x.e.Id)).ThenBy(x => x.e.Id).ToList();
        if (near.Count > 0) return Linked(near[0].e.Id, "word");

        // What the earlier order did from here: a new entity when the name stands in the chunk as written, else a drop.
        var found = ExtractStage.Occurrences(name, _bookText[chunk.CharStart..chunk.CharEnd]).Select(o => (Start: chunk.CharStart + o.Start, End: chunk.CharStart + o.End, chunk.Chapter)).ToList();
        var was = found.Count == 0 || key.Length < 2 ? "dropped" : "new";
        var (nearest, nearestScore) = Nearest(key);
        LinkedName Late(int? id, string how) => new(name, chunk.Index, id, how, was, nearest, nearestScore);

        // 0 again: the narrator the titles do not name, by the role the extraction gave.
        if (asNarrator && NarratorOf(chunk) is { } byRole) return Late(byRole, "narrator");

        // 4. BookNLP's coreference where the name stands: inside the evidence first, else nearest to it.
        if (_chains is not null)
            foreach (var at in found.OrderBy(f => f.Start >= evidence.CharStart && f.End <= evidence.CharEnd ? 0 : 1).ThenBy(f => Math.Abs(f.Start - evidence.CharStart)))
                if (_chains.At(at.Start, at.End) is { } chain && _entities.FirstOrDefault(e => e.Id == chain.Entity) is { } e && Fits(e)) return Late(e.Id, "coref");

        // 5. Spelling: an alias of an entity mentioned in this chapter or the one before, at Jaro-Winkler 0.92 or more.
        if (!pronoun)
        {
            var spelled = _aliasKeys.Where(a => _chaptersOf.TryGetValue(a.Entity, out var ch) && (ch.Contains(chunk.Chapter) || ch.Contains(chunk.Chapter - 1)) && Fits(Entity(a.Entity)))
                .Select(a => (a.Entity, Score: Spelling(key, a.Key))).Where(a => a.Score >= FuzzyThreshold)
                .OrderByDescending(a => a.Score).ThenByDescending(a => InChapter(a.Entity)).ThenBy(a => a.Entity).ToList();
            if (spelled.Count > 0) return Late(spelled[0].Entity, "fuzzy");
        }

        // The narrator's name the extraction gave (step 0, by role) makes no entity: nothing ties it to anyone.
        if (narratorName) return Late(null, "dropped");

        // 6. A pronoun nothing resolved: the narrator for a first-person one, else the character last mentioned before it
        // in the paragraph.
        if (pronoun && Antecedent(key, want, evidence, chunk, found) is { } antecedent) return Late(antecedent, "pronoun");

        // 7. A name the book has not used by the end of this chunk, a pronoun with no one to stand for, or no name to speak of: the
        // words the text uses there, through the steps above; else an entity that says no more than the text does.
        if (!described && (pronoun || key.Length < 2 || (found.Count == 0 && NotYetInTheBook(name, chunk))))
        {
            if (!name.Any(char.IsLetterOrDigit)) return Late(null, "dropped");
            if (Described(name, want, evidence, chunk) is { } phrase && Key(phrase) != key && !Pronouns.Contains(Key(phrase)))
                return Late(Find(phrase, want, evidence, chunk, narratorName: false, described: true).Entity, "described");
            var unnamed = $"{(want == MentionType.Location ? "an unnamed place" : "an unnamed person")} (chapter {chunk.Chapter})";
            return Late(Add(unnamed, want ?? MentionType.Character, [(evidence.CharStart, evidence.CharEnd, chunk.Chapter)], was, Indexing.Entity.Descriptive), "described");
        }

        // 8. A new entity with this one name, flagged unlinked; of kind descriptive when the name is the text's words for someone it
        // does not name (step 7), or a description with no proper name in it ("the stranger", "my father").
        var type = want ?? (found.Count > 0 && _chains?.CategoryAt(found[0].Start, found[0].End) is "LOC" or "GPE" or "FAC" ? MentionType.Location : MentionType.Character);
        // A name the chunk does not write as given (the extraction's own wording) stands where the item's evidence does.
        if (found.Count == 0) found = [(evidence.CharStart, evidence.CharEnd, chunk.Chapter)];
        return Late(Add(name, type, found, was, described || !Named(name) ? Indexing.Entity.Descriptive : null), "new");
    }

    /// <summary>Whether a name holds a proper name: a capitalised word after its first ("my father" and "The stranger" do not, "Mrs. Hudson" and "Paris" do).</summary>
    private static bool Named(string name)
    {
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var lead = words.Length > 1 && Leading.Contains(words[0].ToLowerInvariant() + " ");
        return words.Skip(lead ? 1 : 0).Any(w => char.IsUpper(w[0]));
    }

    private int Add(string name, MentionType type, List<(int Start, int End, int Chapter)> found, string was, string? kind)
    {
        var key = Key(name);
        if (!_added.TryGetValue((key, type), out var added))
        {
            added = _added[(key, type)] = _nextId++;
            _addedMentions[added] = [];
            _addedWas[added] = was;
            if (kind is not null) _addedKind[added] = kind;
        }
        _addedMentions[added].AddRange(found.Where(f => _addedMentions[added].All(m => m.Start != f.Start)));
        var entity = new EntityRef(added, _entities.FirstOrDefault(x => x.Id == added)?.Name ?? name, type, [], _addedMentions[added].Select(m => (m.Start, m.End, name)).ToList());
        _entities.RemoveAll(x => x.Id == added);
        _entities.Add(entity);
        _byAlias[key] = (_byAlias.TryGetValue(key, out var known) ? known : []).Append(added).Distinct().ToList();
        _chaptersOf[added] = _addedMentions[added].Select(m => m.Chapter).ToHashSet();
        return added;
    }

    /// <summary>
    /// Who a pronoun nothing resolved stands for. A first-person one is the chapter's narrator. Any other is the entity
    /// last mentioned before the pronoun in its paragraph (a Character, or a Location where the slot needs one; "it" takes either);
    /// with none there, the last one before it in the chunk, then in the book, then the first after it in the chunk.
    /// </summary>
    private int? Antecedent(string key, MentionType? want, NEvidence evidence, ChunkInfo chunk, List<(int Start, int End, int Chapter)> found)
    {
        if (want != MentionType.Location && FirstPerson.Contains(key) && NarratorOf(chunk) is { } teller) return teller;
        // Where the pronoun stands: in the evidence when it is written there, else where the evidence begins.
        var at = found.Where(f => f.Start >= evidence.CharStart && f.End <= evidence.CharEnd).Select(f => (int?)f.Start).FirstOrDefault() ?? evidence.CharStart;
        var type = want ?? (Things.Contains(key) ? null : MentionType.Character);
        var paragraph = chunk.CharStart;
        for (var i = Math.Min(at, _bookText.Length) - 1; i > chunk.CharStart; i--)
            if (_bookText[i] == '\n' && (_bookText[i - 1] == '\n' || (i >= 2 && _bookText[i - 1] == '\r' && _bookText[i - 2] == '\n'))) { paragraph = i + 1; break; }
        var mentions = _entities.Where(e => (type is null || e.Type == type) && !Pronouns.Contains(Key(e.Name)))
            .SelectMany(e => e.Mentions.Select(m => (e.Id, m.Start, m.End))).ToList();
        (int Id, int Start, int End)? Last(int from) => mentions.Where(m => m.Start >= from && m.End <= at).OrderByDescending(m => m.Start).ThenBy(m => m.Id).Select(m => ((int, int, int)?)m).FirstOrDefault();
        return (Last(paragraph) ?? Last(chunk.CharStart) ?? Last(0)
            ?? mentions.Where(m => m.Start >= at && m.End <= chunk.CharEnd).OrderBy(m => m.Start).ThenBy(m => m.Id).Select(m => ((int, int, int)?)m).FirstOrDefault())?.Id;
    }

    private static readonly HashSet<string> SmallWords = ["the", "and", "of", "from", "with", "for", "old", "young", "man", "woman"];

    /// <summary>
    /// The text's own words for someone the extraction named with a name the book has not used. A word of the given name
    /// that the item's evidence writes (in any case, accents aside), widened to the noun phrase around it (BookNLP's, else a
    /// leading article or form of address): "Miss Stapleton" for "Beryl Stapleton", "the lookout" for "Lookout". With no such
    /// word, the first noun phrase of the right kind BookNLP has in the evidence; else the whole name as the chunk writes it in
    /// another case, nearest the evidence. Null when the text offers nothing.
    /// </summary>
    private string? Described(string name, MentionType? want, NEvidence evidence, ChunkInfo chunk)
    {
        int from = Math.Max(evidence.CharStart, chunk.CharStart), to = Math.Min(evidence.CharEnd, chunk.CharEnd);
        if (to <= from) return null;
        var span = _bookText[from..to];
        var plain = Fold(span).ToLowerInvariant();
        if (plain.Length == span.Length)
            foreach (var word in name.Split((char[])[' ', '-', '’', '\'', '.', ','], StringSplitOptions.RemoveEmptyEntries)
                         .Select(w => Fold(w).ToLowerInvariant()).Where(w => w.Length >= 3 && !Honorifics.Contains(w) && !SmallWords.Contains(w) && !Pronouns.Contains(w)).OrderByDescending(w => w.Length))
                foreach (var (start, end) in ExtractStage.Occurrences(word, plain))
                {
                    var (s, e) = _chains?.PhraseAround(from + start, from + end) ?? Widen(from + start, from + end, chunk);
                    return Bare(_bookText[s..e]);
                }
        if (_chains?.PhraseIn(from, to, want) is { } phrase) return Bare(_bookText[phrase.Start..phrase.End]);
        // The whole name in another case or without its accents, where the chunk writes it nearest the evidence ("the lookout").
        var chunkText = _bookText[chunk.CharStart..chunk.CharEnd];
        var chunkPlain = Fold(chunkText).ToLowerInvariant();
        var whole = Fold(name).ToLowerInvariant();
        if (chunkPlain.Length == chunkText.Length && whole.Length >= 3 && !Pronouns.Contains(whole))
            foreach (var (start, end) in ExtractStage.Occurrences(whole, chunkPlain).OrderBy(o => Math.Abs(chunk.CharStart + o.Start - from)))
            {
                var (s, e) = _chains?.PhraseAround(chunk.CharStart + start, chunk.CharStart + end) ?? Widen(chunk.CharStart + start, chunk.CharStart + end, chunk);
                return Bare(_bookText[s..e]);
            }
        return null;

        static string Bare(string text) => text.EndsWith("’s", StringComparison.Ordinal) || text.EndsWith("'s", StringComparison.Ordinal) ? text[..^2].Trim() : text.Trim();
    }

    /// <summary>[start, end) with the word before it when that is an article, a possessive or a form of address ("the", "Miss").</summary>
    private (int Start, int End) Widen(int start, int end, ChunkInfo chunk)
    {
        var i = start;
        while (i > chunk.CharStart && (_bookText[i - 1] == ' ' || _bookText[i - 1] == '.')) i--;
        var wordEnd = i;
        while (i > chunk.CharStart && char.IsLetter(_bookText[i - 1])) i--;
        var before = _bookText[i..wordEnd].ToLowerInvariant();
        return before.Length > 0 && (Honorifics.Contains(before) || Leading.Contains(before + " ")) ? (i, end) : (start, end);
    }

    /// <summary>
    /// A name the chunk does not write as given and one of whose capitalised words stands nowhere in the book up to the end of the
    /// chunk (accents aside): the extraction brought it from outside the text ("Beryl Stapleton" in the chapter that only says
    /// "Miss Stapleton"; the book gives "Beryl" eight chapters later). An entity under that name would put the later name before
    /// the reader, so step 7 names it as the text does.
    /// </summary>
    private bool NotYetInTheBook(string name, ChunkInfo chunk)
    {
        var sofar = Fold(_bookText[..chunk.CharEnd]).AsSpan();
        foreach (var word in Fold(name).Split((char[])[' ', '-', '’', '\'', '.', ','], StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length >= 3 && char.IsUpper(w[0])))
        {
            var seen = false;
            for (var i = sofar.IndexOf(word, StringComparison.Ordinal); i >= 0 && !seen;)
            {
                var before = i == 0 || !char.IsLetter(sofar[i - 1]);
                var after = i + word.Length >= sofar.Length || !char.IsLetter(sofar[i + word.Length]);
                seen = before && after;
                var next = sofar[(i + 1)..].IndexOf(word, StringComparison.Ordinal);
                i = next < 0 ? -1 : i + 1 + next;
            }
            if (!seen) return true;
        }
        return false;
    }

    public IReadOnlyList<UnlinkedEntity> Unlinked =>
        _added.OrderBy(a => a.Value).Select(a => new UnlinkedEntity(a.Value, _entities.First(e => e.Id == a.Value).Name, a.Key.Item2, _addedMentions[a.Value].OrderBy(m => m.Start).ToList(), KindOf(a.Value))).ToList();
}

public static class LinkStage
{
    public const string StageName = GraphStage3.StageName;

    /// <summary>One chunk's items with their names resolved: the shape <see cref="GraphStage3.Check"/> reads. A participant whose name
    /// stands for no one keeps the id −1 (the check drops it, and the event when no one is left); a subject without an entity is left out.
    /// A fact one of whose subjects was a pronoun given by rule carries <c>inferred_pronoun</c>.</summary>
    public static ChunkGraph Link(ExtractChunk chunk, ChunkInfo info, Linker linker)
    {
        int Who(string? name, NEvidence ev) => linker.Resolve(name, MentionType.Character, ev, info) ?? -1;
        return new ChunkGraph(
            chunk.Events.Select(e => new XEvent(e.Id, e.Kind, e.Summary, e.When,
                e.Participants.Select(p => new XParticipant(Who(p.Name, e.Evidence), p.Role)).ToList(),
                e.Location is null ? null : linker.Resolve(e.Location, MentionType.Location, e.Evidence, info), e.Evidence.Quote, e.MentionedIn)).ToList(),
            chunk.Facts.Select(f =>
            {
                var subjects = f.Subjects.Select(s => linker.Linked(s, null, f.Evidence, info)).Where(l => l?.Entity is not null).ToList();
                return new XFact(f.Id, f.Text, subjects.Select(l => l!.Entity!.Value).Distinct().ToList(), f.Evidence.Quote, f.DiscoveredBy,
                    subjects.Any(l => l!.How == "pronoun") ? GFact.InferredPronoun : null);
            }).ToList(),
            chunk.Perceptions.Select(p => new XPerception(Who(p.Character, p.Evidence), p.Fact, p.TargetCharacter is null ? null : Who(p.TargetCharacter, p.Evidence), p.Kind, p.CauseEvent, p.Evidence.Quote)).ToList(),
            chunk.Retractions.Select(r => new XRetraction(r.Fact, r.Character is null ? null : Who(r.Character, r.Evidence), r.Text,
                r.Subjects.Select(s => linker.Resolve(s, null, r.Evidence, info)).OfType<int>().Distinct().ToList(), r.Evidence.Quote)).ToList(),
            chunk.Identities.Select(i => new XIdentity(Who(i.A, i.Evidence), Who(i.B, i.Evidence), i.Evidence.Quote)).ToList(),
            chunk.Relations.Select(r => new XRelation(r.Type, linker.Resolve(r.Source, r.Type == Ontology.RelatedTo ? MentionType.Character : null, r.Evidence, info) ?? -1,
                linker.Resolve(r.Target, r.Type == Ontology.RelatedTo ? MentionType.Character : MentionType.Location, r.Evidence, info) ?? -1, r.Kind, r.Evidence.Quote)).ToList());
    }

    /// <summary>Chapter → the name the extraction gave the role "narrator" most often in that chapter (a chapter where it named
    /// none: the book's most common one). "the narrator" itself is not a name.</summary>
    public static Dictionary<int, string> NarratorsByRole(ExtractResult extract)
    {
        static bool Named(NParticipant p) => string.Equals(p.Role?.Trim(), "narrator", StringComparison.OrdinalIgnoreCase)
            && Linker.Key(p.Name) is not ("i" or "me" or "myself" or "the narrator" or "narrator");
        static string? Top(IEnumerable<NParticipant> all) => all.Where(Named).GroupBy(p => Linker.Key(p.Name))
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).Select(g => g.GroupBy(p => p.Name).OrderByDescending(n => n.Count()).First().Key).FirstOrDefault();
        var book = Top(extract.Chunks.SelectMany(c => c.Events).SelectMany(e => e.Participants));
        var result = new Dictionary<int, string>();
        foreach (var chapter in extract.Chunks.GroupBy(c => c.Chapter))
            if ((Top(chapter.SelectMany(c => c.Events).SelectMany(e => e.Participants)) ?? book) is { } name) result[chapter.Key] = name;
        return result;
    }

    /// <param name="chains">BookNLP's mentions of the run; null: the coreference step is skipped.</param>
    /// <param name="log">A line per name the first steps did not link and per discovery given by code (run.log).</param>
    public static LinkResult Run(ExtractResult extract, IReadOnlyList<EntityRef> entities, Stage0Result stage0, string bookText, double threshold,
        double dedupeThreshold = GraphStage3.DefaultDedupeThreshold, Runs.RunFolder? run = null, IReadOnlyList<ChainMention>? chains = null, Action<string>? log = null)
    {
        var linker = new Linker(entities, stage0, bookText, chains, NarratorsByRole(extract));
        var chunks = stage0.Chunks.ToDictionary(c => c.Index);
        var linked = extract.Chunks.OrderBy(c => c.Index).Select(c => (Chunk: c, Info: chunks[c.Index], Graph: Link(c, chunks[c.Index], linker))).ToList();

        // The checks run against every entity, the new ones included (a name linked in a later chunk does not change an earlier one).
        var voted = new List<GraphStage3.ChunkItems>();
        var summaries = new List<GraphStage3Chunk>();
        var rejected = new List<GraphReject>();
        foreach (var (chunk, info, graph) in linked)
        {
            var sample = GraphStage3.Check(graph, info, bookText[info.CharStart..info.CharEnd], linker.Entities, threshold, inferDiscovery: true, narrator: linker.NarratorOf(info));
            var items = GraphStage3.Vote([sample], info, 1, 1);
            voted.Add(items);
            var chunkRejected = sample.Rejected.Concat(items.Rejected).ToList();
            rejected.AddRange(chunkRejected);
            summaries.Add(new GraphStage3Chunk(chunk.Index, chunk.Chapter, chunk.Unreadable, chunk.Returned, items.Events.Count, items.Facts.Count, items.Perceptions.Count, chunkRejected.Count));
        }
        var a = GraphStage3.Assemble(voted, dedupeThreshold);
        rejected.AddRange(a.Rejected);
        var dropped = linker.Log.Where(l => l.How == "dropped").DistinctBy(l => (l.Name, l.Chunk)).ToList();
        var inferred = a.Facts.SelectMany(f => f.Discoveries.Where(d => d.Discovery is not null).Select(d => (f.Id, f.Text, d.Event, d.How))).ToList();
        if (run is not null)
        {
            foreach (var r in rejected)
                run.AppendFailure(new { type = r.Tag, stage = StageName, runId = run.RunId, bookId = stage0.BookId, chapter = r.Chapter, chunkIndex = r.Chunk, item = r.Item, reason = r.Reason, what = r.What });
            foreach (var (kept, merged, score) in a.Merges)
                run.AppendFailure(new { type = FailureTags.Duplicate, stage = StageName, runId = run.RunId, bookId = stage0.BookId, item = "fact", rule = "same subjects, text similarity at or above the threshold", kept, dropped = merged, similarity = score });
            foreach (var d in dropped)
                run.AppendFailure(new { type = FailureTags.BadEndpoint, stage = StageName, runId = run.RunId, bookId = stage0.BookId, chunkIndex = d.Chunk, item = "name", reason = "no letter or digit in the name: nothing to link and nothing to name a new entity by", what = d.Name, nearestAlias = d.Nearest, nearestScore = d.NearestScore });
        }
        if (log is not null)
        {
            // Every name the first steps did not link, with where it went, the chunk and the nearest alias.
            foreach (var g in linker.Log.Where(l => l.How is "coref" or "fuzzy" or "pronoun" or "described" or "new" or "dropped" || l.Was is "dropped" or "new").GroupBy(l => (l.Name, l.Chunk, l.Entity)))
            {
                var l = g.First();
                var to = l.Entity is { } id ? $"{linker.Entities.First(e => e.Id == id).Name} ({id}{(linker.KindOf(id) is { } kind ? $", kind={kind}" : "")}){(l.How == "pronoun" ? " · subject=inferred_pronoun" : "")}" : "nothing";
                var nearest = g.FirstOrDefault(x => x.Nearest is not null);
                log($"{StageName}: name \"{l.Name}\" · chunk {l.Chunk} · {g.Count()}× · {l.How} → {to} · nearest alias {(nearest is null ? "none" : $"\"{nearest.Nearest}\" ({nearest.NearestScore:0.00})")}");
            }
            foreach (var (fact, text, e, how) in inferred) log($"{StageName}: fact {fact} \"{text}\" had no discovering event · given {e} ({how}) · discovery=inferred_by_code");
        }
        var graphResult = new GraphStage3Result(GraphStage3.Shape, stage0.BookId, extract.FirstChapter, extract.LastChapter, threshold, 1, 1, 1, dedupeThreshold, summaries,
            a.Events, a.Facts, a.Perceptions, a.Retractions, a.Identities, a.Relations, rejected.Count,
            rejected.GroupBy(r => r.Tag).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count()), a.OverlapDuplicates, a.FactsMerged, a.RelationsMerged);
        // Only the new entities something kept points at: a name used by a dropped item adds no node.
        var used = a.Events.SelectMany(e => e.Participants.Select(p => p.Character).Concat(e.Location is { } l ? [l] : [])).Concat(a.Facts.SelectMany(f => f.Subjects))
            .Concat(a.Perceptions.SelectMany(p => p.TargetCharacter is { } t ? new[] { p.Character, t } : [p.Character])).Concat(a.Retractions.SelectMany(r => r.Subjects.Concat(r.Character is { } c ? [c] : [])))
            .Concat(a.Identities.SelectMany(i => new[] { i.A, i.B })).Concat(a.Relations.SelectMany(r => new[] { r.Source, r.Target })).ToHashSet();
        return new LinkResult(graphResult, linker.Unlinked.Where(u => used.Contains(u.Id)).ToList(),
            linker.Log.GroupBy(l => l.How).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count()), dropped, linker.Log, inferred, rejected);
    }
}
