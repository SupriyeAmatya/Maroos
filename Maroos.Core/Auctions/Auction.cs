namespace Maroos.Core.Auctions;

public enum CardCondition
{
    NearMint = 0,
    LightlyPlayed = 1,
    ModeratelyPlayed = 2,
    HeavilyPlayed = 3,
    Damaged = 4,
}

public enum AuctionStatus
{
    Active = 0,
    Ended = 1,
    Cancelled = 2,
}

/// <summary>One card (a specific Scryfall printing) up for auction. Money is in cents.</summary>
public class Auction
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string SellerId { get; set; } = "";

    public Guid ScryfallId { get; set; }
    public string CardName { get; set; } = "";
    public string SetCode { get; set; } = "";
    public string SetName { get; set; } = "";
    public string? CollectorNumber { get; set; }

    public CardCondition Condition { get; set; }
    public bool IsFoil { get; set; }

    /// <summary>Site-relative URL of the seller's photo (e.g. <c>uploads/….jpg</c>), or null to show Scryfall's image.</summary>
    public string? PhotoPath { get; set; }

    public long StartCents { get; set; }
    public long IncrementCents { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public DateTime EndsAtUtc { get; set; }
    public AuctionStatus Status { get; set; }
    public string? WinnerId { get; set; }

    /// <summary>Open for bids: active and not past its end time (the closer may not have marked it Ended yet).</summary>
    public bool IsOpenAt(DateTime nowUtc) => Status == AuctionStatus.Active && EndsAtUtc > nowUtc;
}
