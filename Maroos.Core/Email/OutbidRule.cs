namespace Maroos.Core.Email;

/// <summary>
/// Who hears about being outbid: only the previous high bidder, never about their own bid, and only if they want
/// auction emails. That also means at most one email per auction until they bid again: once outbid, you can only
/// become the high bidder again by bidding.
/// </summary>
public static class OutbidRule
{
    public static bool ShouldNotify(string? previousHighBidderId, string newBidderId, bool previousBidderOptedIn) =>
        previousHighBidderId is not null && previousHighBidderId != newBidderId && previousBidderOptedIn;
}
