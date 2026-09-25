// =====================================================================================================
// FILE: Maroos.Core/Email/OutbidRule.cs
// PURPOSE: Decides whether an "You've been outbid" email goes out after a bid. Used by the web app's
// BiddingService right after the bid is committed.
// =====================================================================================================

namespace Maroos.Core.Email;

/// <summary>
/// Who hears about being outbid: only the previous high bidder, never about their own bid, and only if they want
/// auction emails. That also means at most one email per auction until they bid again: once outbid, you can only
/// become the high bidder again by bidding.
/// </summary>
public static class OutbidRule
{
    // previousHighBidderId: who led before this bid (null for the first bid); newBidderId: who just bid;
    // previousBidderOptedIn: that person's "Email me about my bids and auctions" switch.
    public static bool ShouldNotify(string? previousHighBidderId, string newBidderId, bool previousBidderOptedIn) =>
        previousHighBidderId is not null && previousHighBidderId != newBidderId && previousBidderOptedIn;
}
