using TemporalGraph.Api;
using TemporalGraph.Data;

namespace TemporalGraph.Api.Tests;

// The promoted Treasure Island graph behind the chat roster (books.json names resolved at load, short names, initials).
public sealed class TreasureIslandTests
{
    private const string Book = "treasure-island";

    private static LoadedBook Load() => new BookCatalog(BookResources.ReadManifest()).Get(Book);

    [Fact]
    public void Every_roster_name_resolves_and_shows_by_its_roster_spelling()
    {
        var book = Load();
        Assert.Empty(book.UnknownRosterNames);
        Assert.Equal(34, book.Chapters.Count);

        // Spoilers on, the end of the book: all eight, in roster order, by the roster's spelling (the short names were Jim, Silver,
        // Mr. Trelawney, Bill and Hands), with initials from the shown name (titles skipped).
        var end = ChatService.Characters(book, 34, spoilers: true);
        Assert.Equal(["Jim Hawkins", "Long John Silver", "Dr. Livesey", "Squire Trelawney", "Captain Smollett", "Ben Gunn", "Billy Bones", "Israel Hands"],
            end.Select(c => c.DisplayName));
        Assert.Equal(["JH", "LS", "L", "T", "S", "BG", "BB", "IH"], end.Select(c => c.Initials));
    }

    /// <summary>
    /// A roster character is shown by its books.json spelling once the text has used it (spoilers off: by the chapter; on:
    /// always), else by its short name. Three Treasure Island names, one Hound and one Musketeers name, around the chapter where the
    /// spelling first appears.
    /// </summary>
    [Theory]
    [InlineData("treasure-island", "Squire Trelawney", 1, false, "Squire Trelawney")]   // first line of the book; short name "Mr. Trelawney"
    [InlineData("treasure-island", "Billy Bones", 2, false, "Billy Bones")]             // short name "Bill"
    [InlineData("treasure-island", "Israel Hands", 10, false, "Israel Hands")]          // short name "Hands" (from ch. 12)
    [InlineData("treasure-island", "Jim Hawkins", 11, false, "Jim")]                    // the graph first has "Jim Hawkins" in ch. 12
    [InlineData("treasure-island", "Jim Hawkins", 12, false, "Jim Hawkins")]
    [InlineData("treasure-island", "Jim Hawkins", 5, true, "Jim Hawkins")]
    [InlineData("hound-of-the-baskervilles", "Laura Lyons", 10, false, "Laura Lyons")]  // short name "Mrs. Lyons"
    [InlineData("three-musketeers", "d'Artagnan", 1, false, "d'Artagnan")]               // the text's D’Artagnan, curly, matches
    [InlineData("three-musketeers", "Cardinal Richelieu", 60, false, "The Red Duke")]    // the short name, until the graph has the spelling (ch. 61)
    [InlineData("three-musketeers", "Cardinal Richelieu", 61, false, "Cardinal Richelieu")]
    public void A_roster_name_shows_as_spelled_once_the_text_has_used_it(string bookId, string rosterName, int chapter, bool spoilers, string expected)
    {
        var book = new BookCatalog(BookResources.ReadManifest()).Get(bookId);
        var canonical = book.Roster[book.RosterNames.ToList().IndexOf(rosterName)].Name;

        var shown = ChatService.Characters(book, chapter, spoilers).Single(c => c.Name == canonical);

        Assert.Equal(expected, shown.DisplayName);
    }

    [Fact]
    public void The_roster_grows_with_the_story()
    {
        var book = Load();
        string[] At(int chapter) => ChatService.Characters(book, chapter, spoilers: false).Select(c => c.Name).ToArray();

        // Ch. 6: the inn and the squire's party; Silver, Smollett and Hands come aboard in Part Two; Ben Gunn is met in ch. 15
        // (his entity also holds the Spy-glass's "Ben" of ch. 8, so he is offered from ch. 8).
        Assert.Equal(["Jim Hawkins", "Doctor Livesey", "Squire Trelawney", "Billy Bones"], At(6));
        Assert.Contains("Long John Silver", At(12));
        Assert.Contains("Captain Smollett", At(12));
        Assert.Contains("Israel Hands", At(12));
        Assert.Contains("Ben Gunn", At(15));
    }

    /// <summary>Every book's voice files are embedded: the guide, one card per roster name, and the example lines.</summary>
    [Theory]
    [InlineData("hound-of-the-baskervilles")]
    [InlineData("three-musketeers")]
    [InlineData("treasure-island")]
    public void The_voice_layer_is_embedded_for_every_book(string bookId)
    {
        var book = new BookCatalog(BookResources.ReadManifest()).Get(bookId);
        Assert.NotNull(book.Voice);
        Assert.False(string.IsNullOrWhiteSpace(book.Voice.Guide));
        Assert.Equal(book.Entry.Chat!.Count, book.Voice.Cards.Count);
        Assert.NotEmpty(book.Voice.Lines);
        Assert.DoesNotContain("<!--", book.Voice.Guide);
    }
}
