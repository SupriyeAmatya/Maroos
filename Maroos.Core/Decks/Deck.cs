// =====================================================================================================
// FILE: Maroos.Core/Decks/Deck.cs
// PURPOSE: The data model for decks — two database tables:
//   Decks      — one row per deck (name, owner, format, public/private, timestamps)
//   DeckCards  — one row per card printing per board in a deck (e.g. "4x Lightning Bolt, main deck")
// EF Core maps these classes to tables; relationships and limits are configured in ApplicationDbContext.
// =====================================================================================================

namespace Maroos.Core.Decks;

// Board: which part of the deck a card is in. Stored as a number (0/1/2) in the database.
//   Main      — the deck you play.
//   Side      — the sideboard (spare cards swapped in between games in constructed formats).
//   Commander — the commander zone (Commander-style formats).
public enum Board
{
    Main = 0,
    Side = 1,
    Commander = 2,
}

// Deck: one deck someone built.
public class Deck
{
    // MaxNameLength: deck names are cut to 100 characters (also the database column size).
    public const int MaxNameLength = 100;

    // Id: the deck's unique id, created automatically when a Deck object is made.
    public Guid Id { get; set; } = Guid.NewGuid();
    // OwnerId: the Identity user id of whoever created it. Only this user may change the deck.
    public string OwnerId { get; set; } = "";
    // Name: what the owner called the deck, e.g. "Mono-Red Burn".
    public string Name { get; set; } = "";

    // Format: which format the deck is for. Used to check legality and deck-size rules.
    /// <summary>A Scryfall legality key, e.g. <c>modern</c> or <c>commander</c>.</summary>
    public string Format { get; set; } = DeckFormats.Default;

    // IsPublic: true = anyone with the link can view it; false = only the owner (others see "not found").
    public bool IsPublic { get; set; }
    // CreatedAt / UpdatedAt: UTC timestamps. UpdatedAt changes on every edit and sorts "My decks".
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // CopiedFromDeckId: the original deck when this one is a copy (set by DeckService.CopyAsync). The database
    // clears it (ON DELETE SET NULL) when the original is deleted, so a copy never points at a missing deck.
    /// <summary>The deck this one was copied from, if any. Cleared (set to null) when that deck is deleted.</summary>
    public Guid? CopiedFromDeckId { get; set; }

    // Cards: the deck's card lines. `[]` starts it as an empty list. EF fills it when asked to Include(d => d.Cards).
    public List<DeckCard> Cards { get; set; } = [];
}

// DeckCard: "N copies of this printing on this board". Its database key is (DeckId, ScryfallId, Board),
// so the same printing can appear once in the main deck and once in the sideboard.
/// <summary>One card (a specific Scryfall printing) on one board of a deck.</summary>
public class DeckCard
{
    // MaxQuantity: a sanity cap on copies per line.
    public const int MaxQuantity = 999;

    // DeckId: which deck this line belongs to (foreign key to Decks.Id; deleted with the deck).
    public Guid DeckId { get; set; }
    // ScryfallId: which printing (links to the card data cached from Scryfall).
    public Guid ScryfallId { get; set; }
    // Board: main deck, sideboard or commander zone.
    public Board Board { get; set; }
    // CardName: the card's name, stored so exports and rules work even without Scryfall data.
    public string CardName { get; set; } = "";
    // Quantity: number of copies.
    public int Quantity { get; set; }
    // SetCode / CollectorNumber: identify the exact printing (e.g. "c21" / "263") for exports "with set codes".
    public string? SetCode { get; set; }
    public string? CollectorNumber { get; set; }
}
