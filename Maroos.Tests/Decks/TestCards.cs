using Maroos.Core.Decks;
using Maroos.Core.Scryfall;

namespace Maroos.Tests.Decks;

/// <summary>Builds deck entries without Scryfall, for stats and rules tests.</summary>
public static class TestCards
{
    public static ScryfallCard Card(
        string name,
        string typeLine = "Instant",
        decimal cmc = 1,
        string? manaCost = "{R}",
        string[]? colors = null,
        string? usd = "1.00",
        string? usdFoil = null,
        string? oracleText = null,
        Dictionary<string, string>? legalities = null) =>
        new(
            Guid.NewGuid(), name, manaCost, cmc, typeLine, oracleText, colors ?? ["R"], "tst", "Test Set", "1",
            null, new ScryfallPrices(usd, usdFoil, null, null),
            legalities ?? new() { ["modern"] = "legal", ["legacy"] = "legal", ["vintage"] = "legal", ["commander"] = "legal" },
            null);

    public static DeckEntry Entry(int quantity, ScryfallCard? card, Board board = Board.Main, string? name = null) =>
        new(new DeckCard { ScryfallId = card?.Id ?? Guid.NewGuid(), CardName = name ?? card!.Name, Quantity = quantity, Board = board }, card);

    public static ScryfallCard Land(string name = "Mountain", string typeLine = "Basic Land — Mountain") =>
        Card(name, typeLine, cmc: 0, manaCost: "", colors: [], usd: "0.10");
}
