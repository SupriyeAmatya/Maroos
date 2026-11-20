using Maroos.Core.Scryfall;

namespace Maroos.Core.Decks;

/// <summary>A deck line plus its Scryfall card data (null if Scryfall no longer knows the card).</summary>
public sealed record DeckEntry(DeckCard Line, ScryfallCard? Card)
{
    public string PrimaryType => CardTypes.PrimaryType(Card?.TypeLine);

    public bool IsLand => PrimaryType == CardTypes.Land;
}
