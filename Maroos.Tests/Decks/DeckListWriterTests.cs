// =====================================================================================================
// FILE: Maroos.Tests/Decks/DeckListWriterTests.cs
// PURPOSE: DeckListWriter (Maroos.Core): writing a deck back to text, with or without printings, and that
//   writing then parsing gives the same deck.
// Each test's name says what it proves; the '// TEST:' line above each one repeats it in plain words.
// =====================================================================================================

using Maroos.Core.Decks;

namespace Maroos.Tests.Decks;

public class DeckListWriterTests
{
    // Helper: Card.
    private static DeckCard Card(int quantity, string name, Board board = Board.Main, string? set = null, string? number = null) =>
        new() { ScryfallId = Guid.NewGuid(), Quantity = quantity, CardName = name, Board = board, SetCode = set, CollectorNumber = number };

    // TEST: Writes main then sideboard sorted by name.
    [Fact]
    public void Writes_main_then_sideboard_sorted_by_name()
    {
        var text = DeckListWriter.Write(
        [
            Card(20, "Mountain"),
            Card(2, "Pyroblast", Board.Side),
            Card(4, "Lightning Bolt"),
        ]);

        Assert.Equal("4 Lightning Bolt\n20 Mountain\n\nSideboard\n2 Pyroblast", text.ReplaceLineEndings("\n"));
    }

    // TEST: Writes commander and deck headers for commander decks.
    [Fact]
    public void Writes_commander_and_deck_headers_for_commander_decks()
    {
        var text = DeckListWriter.Write([Card(1, "Sol Ring"), Card(1, "Krenko, Mob Boss", Board.Commander)]);

        Assert.Equal("Commander\n1 Krenko, Mob Boss\n\nDeck\n1 Sol Ring", text.ReplaceLineEndings("\n"));
    }

    // TEST: Can include set codes.
    [Fact]
    public void Can_include_set_codes()
    {
        var text = DeckListWriter.Write([Card(1, "Sol Ring", set: "c21", number: "263")], includePrintings: true);

        Assert.Equal("1 Sol Ring (C21) 263", text);
    }

    // TEST: Output parses back to the same cards.
    [Fact]
    public void Output_parses_back_to_the_same_cards()
    {
        DeckCard[] deck =
        [
            Card(1, "Krenko, Mob Boss", Board.Commander, "ddt", "52"),
            Card(1, "Sol Ring", Board.Main, "c21", "263"),
            Card(1, "Fire // Ice", Board.Main, "mh2", "290"),
            Card(2, "Pyroblast", Board.Side, "ice", "213"),
        ];

        foreach (var withPrintings in new[] { false, true })
        {
            var parsed = DeckListParser.Parse(DeckListWriter.Write(deck, withPrintings));

            Assert.Empty(parsed.Unreadable);
            Assert.Equal(
                deck.Select(c => (c.Quantity, c.CardName, c.Board)).Order(),
                parsed.Lines.Select(l => (l.Quantity, l.Name, l.Board)).Order());
        }
    }
}
