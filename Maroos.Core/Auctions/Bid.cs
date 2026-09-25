// =====================================================================================================
// FILE: Maroos.Core/Auctions/Bid.cs
// PURPOSE: The Bid table's model and the rules every bid must pass.
//
// BIDDING RULES (BidRules.Validate):
//   1. the auction must not be cancelled and must still be open;
//   2. the seller can't bid on their own listing;
//   3. the current high bidder can't bid again (no bidding against yourself);
//   4. the first bid must be at least the starting price; each later bid must be at least
//      the high bid + the increment;
//   5. no bid above the $1,000,000 cap.
// The rules are pure (no database); BiddingService (Maroos.Web) applies them inside a
// serializable database transaction so two simultaneous bids can't both win.
// =====================================================================================================

namespace Maroos.Core.Auctions;

// Bid: one accepted bid (rejected bids are never saved).
public class Bid
{
    // Id: unique id of the bid.
    public Guid Id { get; set; } = Guid.NewGuid();
    // AuctionId: which auction (deleted with the auction).
    public Guid AuctionId { get; set; }
    // BidderId: the Identity user id of who bid.
    public string BidderId { get; set; } = "";
    // AmountCents: the bid in cents.
    public long AmountCents { get; set; }
    // CreatedAtUtc: when it was placed; breaks ties (earlier bid wins) and orders the history.
    public DateTime CreatedAtUtc { get; set; }
}

/// <summary>Why a bid was turned down (null when it's accepted).</summary>
public static class BidRules
{
    /// <summary>The lowest bid that can be placed next: the start price, or the high bid plus the increment.</summary>
    // MinimumNextBid: e.g. start $5, increment $1: first bid >= $5.00; after a $6.50 bid, the next must be >= $7.50.
    public static long MinimumNextBid(Auction auction, long? highBidCents) =>
        highBidCents is { } high ? high + auction.IncrementCents : auction.StartCents;

    /// <param name="highBidderId">Who holds the current high bid, if anyone.</param>
    // Validate: returns null if the bid is allowed, otherwise the reason shown to the bidder.
    //   auction      — the listing (freshly loaded inside the bid transaction)
    //   bidderId     — who is bidding
    //   amountCents  — how much
    //   highBidCents — the current high bid, or null if none yet
    //   highBidderId — who holds it
    //   nowUtc       — the current time (passed in so tests can control time)
    public static string? Validate(Auction auction, string bidderId, long amountCents, long? highBidCents, string? highBidderId, DateTime nowUtc)
    {
        // Rule 1.
        if (auction.Status == AuctionStatus.Cancelled)
            return "This auction was cancelled.";
        if (!auction.IsOpenAt(nowUtc))
            return "This auction has ended.";
        // Rule 2.
        if (auction.SellerId == bidderId)
            return "You can't bid on your own listing.";
        // Rule 3.
        if (highBidderId == bidderId)
            return "You're already the highest bidder.";

        // Rule 4.
        var minimum = MinimumNextBid(auction, highBidCents);
        if (amountCents < minimum)
            return $"Bids must be at least {Money.FormatUsd(minimum)}.";
        // Rule 5.
        if (amountCents > AuctionRules.MaxPriceCents)
            return $"Bids can't be more than {Money.FormatUsd(AuctionRules.MaxPriceCents)}.";

        // All rules passed.
        return null;
    }
}
