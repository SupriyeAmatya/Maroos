// =====================================================================================================
// FILE: Maroos.Core/Decks/DeckEntry.cs
// PURPOSE: Pairs a deck line (from the database) with that card's Scryfall data (from the cache),
// so stats, rules and the deck page have everything in one object.
// =====================================================================================================

using Maroos.Core.Scryfall;

namespace Maroos.Core.Decks;

/// <summary>A deck line plus its Scryfall card data (null if Scryfall no longer knows the card).</summary>
// DeckEntry:
//   Line — the DeckCard row (quantity, board, name, printing).
//   Card — the matching ScryfallCard, or null if Scryfall has no data for it (rare; rules flag it as "Unknown").
public sealed record DeckEntry(DeckCard Line, ScryfallCard? Card)
{
    // PrimaryType: which group this card appears under (Creature, Land...). See CardTypes.
    public string PrimaryType => CardTypes.PrimaryType(Card?.TypeLine);

    // IsLand: lands are left out of the mana curve and colour counts.
    public bool IsLand => PrimaryType == CardTypes.Land;
}
