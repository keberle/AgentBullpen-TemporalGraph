namespace TemporalGraph.Query.Tests;

/// <summary>
/// A small hand-built book graph. Chapter 1 is [0, 1000), chapter 2 [1000, 3000).
/// Edge directions follow the indexing pipeline (ScratchDb.Build): ABOUT Fact → entity, STATES and PARTICIPATED_IN
/// entity → node, LOCATED_AT entity → place or Event → place.
/// </summary>
public static class Fixture
{
    private static Dictionary<string, object?> P(params (string Key, object? Value)[] kv) => kv.ToDictionary(x => x.Key, x => x.Value);
    private static List<object?> L(params string[] items) => items.Cast<object?>().ToList();

    public static readonly BookGraph Graph = new(
    [
        new(1, ["Character"], P(("name", "Sherlock Holmes"), ("aliases", L("Sherlock Holmes", "Holmes", "Mr. Holmes")), ("evidenceStart", 10L), ("evidenceEnd", 25L))),
        new(2, ["Character"], P(("name", "Dr. Watson"), ("aliases", L("Dr. Watson", "Watson")), ("evidenceStart", 20L))),
        new(3, ["Character"], P(("name", "Dr. Mortimer"), ("aliases", L("Mortimer", "Dr. Mortimer")), ("evidenceStart", 500L))),
        new(4, ["Location"], P(("name", "Baker Street"), ("aliases", L("Baker Street")), ("evidenceStart", 30L))),
        new(5, ["Location"], P(("name", "Baskerville Hall"), ("aliases", L("Baskerville Hall", "the Hall")), ("evidenceStart", 900L))),
        new(6, ["Fact"], P(("text", "Holmes is late in the mornings."), ("chapter", 1L), ("evidenceStart", 12L))),
        new(7, ["Fact"], P(("text", "Mortimer's stick dates from 1884."), ("chapter", 1L), ("evidenceStart", 510L))),
        new(8, ["Event"], P(("text", "Mortimer visits Baker Street."), ("chapter", 1L), ("evidenceStart", 600L))),
        new(9, ["Character"], P(("name", "Selden"), ("aliases", L("Selden")), ("evidenceStart", 2000L))),
    ],
    [
        new(101, "MET", 1, 2, P(("revealedPos", 25L))),
        new(102, "MET", 3, 1, P(("revealedPos", 520L))),
        new(103, "ABOUT", 6, 1, P(("revealedPos", 12L), ("storyFrom", "1889~"), ("storyFromLo", "1889-01-01"), ("storyFromHi", "1889-12-31"))),
        new(104, "ABOUT", 7, 3, P(("revealedPos", 510L), ("storyFrom", "1884"), ("storyFromLo", "1884-01-01"), ("storyFromHi", "1884-12-31"))),
        new(105, "ABOUT", 7, 1, P(("revealedPos", 510L))),
        new(106, "PARTICIPATED_IN", 3, 8, P(("revealedPos", 600L), ("fabulaSeq", 2.0))),
        new(107, "PARTICIPATED_IN", 1, 8, P(("revealedPos", 600L), ("fabulaSeq", 2.0))),
        new(108, "LOCATED_AT", 1, 4, P(("revealedPos", 30L))),
        new(109, "LOCATED_AT", 8, 4, P(("revealedPos", 600L))),
        new(110, "RELATED_TO", 2, 1, P(("revealedPos", 40L), ("kind", "friend"))),
        new(111, "STATES", 3, 7, P(("revealedPos", 510L))),
        new(112, "MET", 9, 2, P(("revealedPos", 2100L))),
    ],
    new Dictionary<int, (int, int)> { [1] = (0, 1000), [2] = (1000, 3000) });

    public static QueryResult Run(string query, params (string Name, object? Value)[] parameters) =>
        CypherEngine.Execute(Graph, query, parameters.ToDictionary(p => p.Name, p => p.Value), TestContext.Current.CancellationToken);

    /// <summary>Rows as text, one string per row, values joined with " | " (CypherValues.Display).</summary>
    public static List<string> Rows(QueryResult r) => r.Rows.Select(row => string.Join(" | ", row.Select(CypherValues.Display))).ToList();
}
