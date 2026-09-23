using Maroos.Core.Auctions;

namespace Maroos.Tests.Auctions;

public class BidRulesTests
{
    private static readonly DateTime Now = new(2026, 9, 23, 12, 0, 0, DateTimeKind.Utc);

    private static Auction Open() => new()
    {
        SellerId = "seller",
        StartCents = 500,
        IncrementCents = 100,
        Status = AuctionStatus.Active,
        EndsAtUtc = Now.AddDays(1),
    };

    [Fact]
    public void The_first_bid_must_meet_the_starting_price_and_later_ones_the_increment()
    {
        Assert.Equal(500, BidRules.MinimumNextBid(Open(), null));
        Assert.Equal(1300, BidRules.MinimumNextBid(Open(), 1200));

        Assert.Null(BidRules.Validate(Open(), "bob", 500, null, null, Now));
        Assert.Equal("Bids must be at least $5.00.", BidRules.Validate(Open(), "bob", 499, null, null, Now));
        Assert.Null(BidRules.Validate(Open(), "bob", 1300, 1200, "carol", Now));
        Assert.Equal("Bids must be at least $13.00.", BidRules.Validate(Open(), "bob", 1299, 1200, "carol", Now));
    }

    [Fact]
    public void The_seller_and_the_current_high_bidder_cannot_bid()
    {
        Assert.Equal("You can't bid on your own listing.", BidRules.Validate(Open(), "seller", 900, null, null, Now));
        Assert.Equal("You're already the highest bidder.", BidRules.Validate(Open(), "bob", 2000, 1200, "bob", Now));
    }

    [Fact]
    public void Ended_and_cancelled_auctions_take_no_bids()
    {
        Assert.Equal("This auction has ended.", BidRules.Validate(Open(), "bob", 900, null, null, Now.AddDays(1)));

        var cancelled = Open();
        cancelled.Status = AuctionStatus.Cancelled;
        Assert.Equal("This auction was cancelled.", BidRules.Validate(cancelled, "bob", 900, null, null, Now));
    }

    [Fact]
    public void Bids_are_capped_at_the_price_limit()
    {
        Assert.Equal("Bids can't be more than $1,000,000.00.", BidRules.Validate(Open(), "bob", AuctionRules.MaxPriceCents + 1, null, null, Now));
    }

    [Theory]
    [InlineData(0, "Ended")]
    [InlineData(1, "00:00:01")]
    [InlineData(0.2, "00:00:01")]
    [InlineData(59, "00:00:59")]
    [InlineData(3 * 3600 + 5 * 60 + 9, "03:05:09")]
    [InlineData(86400, "1d 00:00:00")]
    [InlineData(2 * 86400 + 4 * 3600 + 13 * 60 + 9, "2d 04:13:09")]
    public void Countdown_ticks_in_seconds(double secondsLeft, string expected) =>
        Assert.Equal(expected, AuctionRules.Countdown(Now, Now.AddSeconds(secondsLeft)));
}
