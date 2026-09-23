namespace Maroos.Core.Auctions;

public class Bid
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AuctionId { get; set; }
    public string BidderId { get; set; } = "";
    public long AmountCents { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

/// <summary>Why a bid was turned down (null when it's accepted).</summary>
public static class BidRules
{
    /// <summary>The lowest bid that can be placed next: the start price, or the high bid plus the increment.</summary>
    public static long MinimumNextBid(Auction auction, long? highBidCents) =>
        highBidCents is { } high ? high + auction.IncrementCents : auction.StartCents;

    /// <param name="highBidderId">Who holds the current high bid, if anyone.</param>
    public static string? Validate(Auction auction, string bidderId, long amountCents, long? highBidCents, string? highBidderId, DateTime nowUtc)
    {
        if (auction.Status == AuctionStatus.Cancelled)
            return "This auction was cancelled.";
        if (!auction.IsOpenAt(nowUtc))
            return "This auction has ended.";
        if (auction.SellerId == bidderId)
            return "You can't bid on your own listing.";
        if (highBidderId == bidderId)
            return "You're already the highest bidder.";

        var minimum = MinimumNextBid(auction, highBidCents);
        if (amountCents < minimum)
            return $"Bids must be at least {Money.FormatUsd(minimum)}.";
        if (amountCents > AuctionRules.MaxPriceCents)
            return $"Bids can't be more than {Money.FormatUsd(AuctionRules.MaxPriceCents)}.";

        return null;
    }
}
