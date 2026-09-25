// =====================================================================================================
// FILE: Maroos.Core/Auctions/Auction.cs
// PURPOSE: The data model for one auction listing (the Auctions database table), plus the two
// small enums it uses: the card's condition and the auction's status.
// =====================================================================================================

namespace Maroos.Core.Auctions;

// CardCondition: the standard trading-card condition grades, best to worst.
// Shown as codes (NM, LP, MP, HP, DMG) on tiles and full names on the listing page (see AuctionRules).
public enum CardCondition
{
    NearMint = 0,
    LightlyPlayed = 1,
    ModeratelyPlayed = 2,
    HeavilyPlayed = 3,
    Damaged = 4,
}

// AuctionStatus: where the auction is in its life.
//   Active    — listed; open for bids until EndsAtUtc.
//   Ended     — past its end time; set by the AuctionCloser background job (WinnerId filled in if anyone bid).
//   Cancelled — the seller withdrew it (only allowed while open and before anyone bids).
public enum AuctionStatus
{
    Active = 0,
    Ended = 1,
    Cancelled = 2,
}

/// <summary>One card (a specific Scryfall printing) up for auction. Money is in cents.</summary>
public class Auction
{
    // Id: unique id of the listing (appears in its URL: /auctions/{Id}).
    public Guid Id { get; set; } = Guid.NewGuid();
    // SellerId: the Identity user id of the person selling.
    public string SellerId { get; set; } = "";

    // The exact printing being sold, copied from Scryfall when the listing is created
    // (so listings still read correctly even if Scryfall data changes later):
    public Guid ScryfallId { get; set; }
    public string CardName { get; set; } = "";
    // SetCode: e.g. "c21" — also used by the "Set" filter on /auctions.
    public string SetCode { get; set; } = "";
    public string SetName { get; set; } = "";
    public string? CollectorNumber { get; set; }

    // Condition and foil, chosen by the seller.
    public CardCondition Condition { get; set; }
    public bool IsFoil { get; set; }

    /// <summary>Site-relative URL of the seller's photo (e.g. <c>uploads/….jpg</c>), or null to show Scryfall's image.</summary>
    public string? PhotoPath { get; set; }

    // StartCents: the lowest first bid allowed. IncrementCents: how much each new bid must beat the last by.
    // Both in cents (long): $5.00 = 500.
    public long StartCents { get; set; }
    public long IncrementCents { get; set; }

    // Timing (always UTC so every server and time zone agrees).
    public DateTime CreatedAtUtc { get; set; }
    public DateTime EndsAtUtc { get; set; }
    public AuctionStatus Status { get; set; }
    // WinnerId: the high bidder, recorded when the auction is closed (null if nobody bid, or not closed yet).
    public string? WinnerId { get; set; }

    /// <summary>Open for bids: active and not past its end time (the closer may not have marked it Ended yet).</summary>
    // IsOpenAt: THE check for "can people bid right now?". Always use this rather than Status alone:
    // the closer runs once a minute, so for up to a minute an auction can still say Active after its end time.
    public bool IsOpenAt(DateTime nowUtc) => Status == AuctionStatus.Active && EndsAtUtc > nowUtc;
}
