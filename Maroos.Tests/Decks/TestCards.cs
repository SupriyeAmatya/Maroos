// =====================================================================================================
// FILE: Maroos.Tests/Decks/TestCards.cs
// PURPOSE: Quick builders for made-up cards, so deck stats and rules tests can describe exactly the deck
// they need ("20 Mountains + 40 one-mana red instants") without any Scryfall data.
// =====================================================================================================

using Maroos.Core.Decks;
using Maroos.Core.Scryfall;

namespace Maroos.Tests.Decks;

/// <summary>Builds deck entries without Scryfall, for stats and rules tests.</summary>
public static class TestCards
{
    // Card: a fake Scryfall card. Every parameter has a default (a $1 red one-mana instant, legal in the
    // main formats), so a test only names what matters, e.g. Card("Bolt", cmc: 1, usd: "2.50").
    public static ScryfallCard Card(
        string name,
        string typeLine = "Instant",
        decimal cmc = 1,
        string? manaCost = "{R}",
        string[]? colors = null,
        string? usd = "1.00",
        string? usdFoil = null,
        string? oracleText = null,
        Dictionary<string, string>? legalities = null,
        string[]? colorIdentity = null) =>
        new(
            Guid.NewGuid(), name, manaCost, cmc, typeLine, oracleText, colors ?? ["R"], "tst", "Test Set", "1",
            null, new ScryfallPrices(usd, usdFoil, null, null),
            legalities ?? new() { ["modern"] = "legal", ["legacy"] = "legal", ["vintage"] = "legal", ["commander"] = "legal" },
            null,
            colorIdentity ?? colors ?? ["R"]);

    // Entry: a deck line (quantity + board) paired with its card. card may be null to simulate
    // "Scryfall no longer knows this card".
    public static DeckEntry Entry(int quantity, ScryfallCard? card, Board board = Board.Main, string? name = null) =>
        new(new DeckCard { ScryfallId = card?.Id ?? Guid.NewGuid(), CardName = name ?? card!.Name, Quantity = quantity, Board = board }, card);

    // Land: a colorless zero-cost basic land (Mountain by default).
    public static ScryfallCard Land(string name = "Mountain", string typeLine = "Basic Land — Mountain") =>
        Card(name, typeLine, cmc: 0, manaCost: "", colors: [], usd: "0.10");
}
