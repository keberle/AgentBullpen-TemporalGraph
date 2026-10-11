namespace TemporalGraph.Cli.Indexing;

// The short name of a character (the characters table's short_name, and the node's shortName property): the name the book
// calls them by most often, so the roster, the graph labels and the chat rows use one familiar form for everyone ("Athos",
// "D’Artagnan", "Richelieu", "Milady", "Holmes", "Sir Henry") instead of a mix of Stage 2's formal canonical names
// ("Monsieur Athos", "Armand Jean Duplessis, Cardinal de Richelieu") and whichever alias came first.

public static class ShortNames
{
    // Words that alone don't name anyone ("Monseigneur", "the Cardinal", "his Eminence").
    private static readonly HashSet<string> TitleWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "mr", "mrs", "miss", "ms", "dr", "sir", "lady", "lord", "the", "a", "an", "my", "his", "her", "of", "de", "la", "le", "du",
        "monsieur", "madame", "m", "mme", "mlle", "mademoiselle", "messieurs", "monseigneur", "duke", "duc", "duchess", "duchesse",
        "comte", "comtesse", "count", "countess", "baron", "baroness", "chevalier", "cardinal", "king", "queen", "prince", "princess",
        "captain", "father", "abbé", "abbe", "eminence", "majesty", "highness", "grace",
    };

    /// <summary>
    /// The mention text used most often for <paramref name="entity"/> that can stand as a name: it starts with a capital and isn't all
    /// capitals, isn't a possessive ("Athos’s"), isn't titles alone ("Monseigneur"), and is no other character's alias
    /// (<paramref name="owners"/>: alias, case-insensitive → the entities that have it). Ties: the first mentioned, then ordinal.
    /// None qualifies → the canonical name.
    /// </summary>
    public static string Pick(Entity entity, IReadOnlyDictionary<string, HashSet<int>> owners)
    {
        var best = entity.Mentions
            .GroupBy(m => m.Text)
            .Where(g => CanStand(g.Key) && owners.TryGetValue(g.Key, out var o) && o.Count == 1)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Min(m => m.CharStart)).ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => g.Key)
            .FirstOrDefault();
        return best ?? entity.CanonicalName;
    }

    /// <summary>Alias (case-insensitive) → the ids of the entities that have it, over <paramref name="entities"/>.</summary>
    public static Dictionary<string, HashSet<int>> Owners(IEnumerable<Entity> entities)
    {
        var owners = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entities)
            foreach (var a in e.Aliases.Concat(e.Mentions.Select(m => m.Text)))
            {
                if (!owners.TryGetValue(a, out var set)) owners[a] = set = [];
                set.Add(e.Id);
            }
        return owners;
    }

    private static bool CanStand(string text)
    {
        var t = text.Trim();
        if (t.Length == 0 || !char.IsUpper(t[0]) || !t.Any(char.IsLower)) return false;
        if (t.EndsWith("’s", StringComparison.Ordinal) || t.EndsWith("'s", StringComparison.Ordinal)) return false;
        return !t.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => w.Trim('.', ',')).All(TitleWords.Contains);
    }
}
