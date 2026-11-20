using Maroos.Core;
using Maroos.Core.Auctions;

namespace Maroos.Tests.Auctions;

public class AuctionRulesTests
{
    private static NewAuction Valid() => new(Guid.NewGuid(), CardCondition.NearMint, false, 500, 100, 7);

    [Fact]
    public void A_valid_listing_has_no_errors() => Assert.Empty(AuctionRules.Validate(Valid()));

    [Theory]
    [InlineData(0L, 100L, 7, "The starting price must be more than $0.")]
    [InlineData(-5L, 100L, 7, "The starting price must be more than $0.")]
    [InlineData(100_000_001L, 100L, 7, "The starting price can't be more than $1,000,000.00.")]
    [InlineData(500L, 0L, 7, "The bid increment must be more than $0.")]
    [InlineData(500L, 100L, 0, "Auctions run between 1 and 7 days.")]
    [InlineData(500L, 100L, 8, "Auctions run between 1 and 7 days.")]
    public void Rejects_bad_prices_and_durations(long start, long increment, int days, string error)
    {
        var request = Valid() with { StartCents = start, IncrementCents = increment, DurationDays = days };

        Assert.Equal([error], AuctionRules.Validate(request));
    }

    [Fact]
    public void Requires_a_card_and_a_known_condition()
    {
        var request = Valid() with { ScryfallId = Guid.Empty, Condition = (CardCondition)99 };

        Assert.Equal(["Pick the card you're selling.", "Pick the card's condition."], AuctionRules.Validate(request));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(7, 7)]
    public void Durations_of_one_to_seven_days_are_allowed(int days, int expected)
    {
        Assert.Empty(AuctionRules.Validate(Valid() with { DurationDays = days }));
        Assert.Equal(expected, days);
    }

    [Theory]
    [InlineData(0, "Ended")]
    [InlineData(-60, "Ended")]
    [InlineData(30, "under a minute")]
    [InlineData(45 * 60, "45m")]
    [InlineData(3 * 3600 + 12 * 60, "3h 12m")]
    [InlineData(2 * 86400 + 4 * 3600 + 59 * 60, "2d 4h")]
    public void TimeLeft_reads_naturally(int secondsLeft, string expected)
    {
        var now = new DateTime(2026, 9, 23, 12, 0, 0, DateTimeKind.Utc);

        Assert.Equal(expected, AuctionRules.TimeLeft(now, now.AddSeconds(secondsLeft)));
    }

    [Theory]
    [InlineData(CardCondition.NearMint, "NM", "Near Mint")]
    [InlineData(CardCondition.Damaged, "DMG", "Damaged")]
    public void Conditions_have_codes_and_labels(CardCondition condition, string code, string label)
    {
        Assert.Equal(code, AuctionRules.ConditionCode(condition));
        Assert.Equal(label, AuctionRules.ConditionLabel(condition));
    }

    [Fact]
    public void An_auction_is_open_until_its_end_time_while_active()
    {
        var now = new DateTime(2026, 9, 23, 12, 0, 0, DateTimeKind.Utc);
        var auction = new Auction { Status = AuctionStatus.Active, EndsAtUtc = now.AddMinutes(1) };

        Assert.True(auction.IsOpenAt(now));
        Assert.False(auction.IsOpenAt(now.AddMinutes(1)));
        Assert.False(new Auction { Status = AuctionStatus.Cancelled, EndsAtUtc = now.AddDays(1) }.IsOpenAt(now));
    }
}

public class MoneyTests
{
    [Theory]
    [InlineData("5", 500L)]
    [InlineData("5.5", 550L)]
    [InlineData("5.05", 505L)]
    [InlineData(" $1,250.00 ", 125000L)]
    [InlineData("0.01", 1L)]
    [InlineData("0", 0L)]
    public void TryParseUsd_reads_typed_dollar_amounts(string text, long cents)
    {
        Assert.True(Money.TryParseUsd(text, out var parsed));
        Assert.Equal(cents, parsed);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("abc")]
    [InlineData("-5")]
    [InlineData("5.001")]
    [InlineData("1e3")]
    [InlineData("99999999999999999999")]
    public void TryParseUsd_rejects_anything_else(string? text) => Assert.False(Money.TryParseUsd(text, out _));

    [Theory]
    [InlineData(500L, "5.00")]
    [InlineData(125050L, "1250.50")]
    public void FormatInput_writes_plain_amounts(long cents, string text) => Assert.Equal(text, Money.FormatInput(cents));
}
