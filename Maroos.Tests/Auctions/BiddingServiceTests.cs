using Maroos.Core.Auctions;
using Maroos.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace Maroos.Tests.Auctions;

/// <summary>Bidding rules on an in-memory SQLite database (plan phase 6, step 8).</summary>
public sealed class BiddingServiceTests : IDisposable
{
    private readonly AuctionWorld _world = new();
    private readonly string _seller;
    private readonly string _bob;
    private readonly string _carol;

    public BiddingServiceTests()
    {
        _seller = _world.AddUser("Card Shark");
        _bob = _world.AddUser("Bob");
        _carol = _world.AddUser("Carol");
    }

    public void Dispose() => _world.Dispose();

    [Fact]
    public async Task Bids_must_beat_the_high_bid_by_the_increment()
    {
        var auction = await _world.ListAsync(_seller, startCents: 500, incrementCents: 100);

        Assert.Equal("Bids must be at least $5.00.", (await _world.Bidding.PlaceBidAsync(auction.Id, _bob, 400)).Error);
        Assert.True((await _world.Bidding.PlaceBidAsync(auction.Id, _bob, 500)).Accepted);
        Assert.Equal("Bids must be at least $6.00.", (await _world.Bidding.PlaceBidAsync(auction.Id, _carol, 550)).Error);
        Assert.True((await _world.Bidding.PlaceBidAsync(auction.Id, _carol, 600)).Accepted);

        var view = (await _world.Auctions.GetAsync(auction.Id, _bob))!;
        Assert.Equal(600, view.CurrentCents);
        Assert.Equal(2, view.BidCount);
        Assert.Equal(_carol, view.HighBidderId);
        Assert.Equal(700, view.MinimumNextBid);
    }

    [Fact]
    public async Task Seller_cannot_bid_and_high_bidder_cannot_raise_themselves()
    {
        var auction = await _world.ListAsync(_seller);

        Assert.Equal("You can't bid on your own listing.", (await _world.Bidding.PlaceBidAsync(auction.Id, _seller, 900)).Error);
        await _world.Bidding.PlaceBidAsync(auction.Id, _bob, 500);
        Assert.Equal("You're already the highest bidder.", (await _world.Bidding.PlaceBidAsync(auction.Id, _bob, 900)).Error);
    }

    [Fact]
    public async Task Ended_cancelled_and_missing_auctions_reject_bids()
    {
        var ended = await _world.ListAsync(_seller, days: 1);
        var cancelled = await _world.ListAsync(_seller, days: 3);
        await _world.Auctions.CancelAsync(cancelled.Id, _seller);
        _world.Clock.Advance(TimeSpan.FromDays(1));

        Assert.Equal("This auction has ended.", (await _world.Bidding.PlaceBidAsync(ended.Id, _bob, 900)).Error);
        Assert.Equal("This auction was cancelled.", (await _world.Bidding.PlaceBidAsync(cancelled.Id, _bob, 900)).Error);
        Assert.Equal("This auction doesn't exist.", (await _world.Bidding.PlaceBidAsync(Guid.NewGuid(), _bob, 900)).Error);

        using var db = _world.Db.CreateDbContext();
        Assert.Empty(db.Bids);
    }

    [Fact]
    public async Task An_accepted_bid_is_announced_with_the_bidders_public_name_and_a_rejected_one_is_not()
    {
        var auction = await _world.ListAsync(_seller);
        var heard = new List<AuctionEvent>();
        using var _ = _world.Events.Subscribe(auction.Id, e => { heard.Add(e); return Task.CompletedTask; });

        await _world.Bidding.PlaceBidAsync(auction.Id, _bob, 100);
        await _world.Bidding.PlaceBidAsync(auction.Id, _bob, 750);

        var bid = Assert.IsType<BidPlaced>(Assert.Single(heard));
        Assert.Equal((750L, "Bob", 1), (bid.AmountCents, bid.BidderName, bid.BidCount));
    }

    [Fact]
    public async Task History_lists_bids_highest_first_and_marks_the_viewers_own()
    {
        var auction = await _world.ListAsync(_seller);
        await _world.Bidding.PlaceBidAsync(auction.Id, _bob, 500);
        _world.Clock.Advance(TimeSpan.FromMinutes(1));
        await _world.Bidding.PlaceBidAsync(auction.Id, _carol, 800);

        var history = await _world.Bidding.HistoryAsync(auction.Id, _bob);

        Assert.Equal([("Carol", 800L, false), ("Bob", 500L, true)], history.Select(h => (h.BidderName, h.AmountCents, h.IsViewer)));
    }

    [Fact]
    public async Task My_bids_says_winning_outbid_won_lost_or_cancelled()
    {
        var winning = await _world.ListAsync(_seller, days: 3);
        var outbid = await _world.ListAsync(_seller, days: 3);
        var won = await _world.ListAsync(_seller, days: 1);
        var lost = await _world.ListAsync(_seller, days: 1);

        await _world.Bidding.PlaceBidAsync(winning.Id, _bob, 500);
        await _world.Bidding.PlaceBidAsync(outbid.Id, _bob, 500);
        await _world.Bidding.PlaceBidAsync(outbid.Id, _carol, 600);
        await _world.Bidding.PlaceBidAsync(won.Id, _bob, 500);
        await _world.Bidding.PlaceBidAsync(lost.Id, _bob, 500);
        await _world.Bidding.PlaceBidAsync(lost.Id, _carol, 900);
        _world.Clock.Advance(TimeSpan.FromDays(2));

        var mine = (await _world.Bidding.ListForBidderAsync(_bob)).ToDictionary(b => b.AuctionId);

        Assert.Equal(MyBidState.Winning, mine[winning.Id].State);
        Assert.Equal((MyBidState.Outbid, 500L, 600L), (mine[outbid.Id].State, mine[outbid.Id].MyHighCents, mine[outbid.Id].CurrentCents));
        Assert.Equal(MyBidState.Won, mine[won.Id].State);
        Assert.Equal(MyBidState.Lost, mine[lost.Id].State);
        // Open auctions come first.
        Assert.Equal([MyBidState.Winning, MyBidState.Outbid], (await _world.Bidding.ListForBidderAsync(_bob)).Take(2).Select(b => b.State).Order());
    }
}

/// <summary>
/// "Two bids at the same moment": real parallel transactions on a SQLite file, where each bid gets its own
/// connection. The serializable transaction must re-read the high bid so the rules hold for every bid saved.
/// </summary>
public sealed class ConcurrentBiddingTests : IDisposable
{
    private readonly AuctionWorld _world = new(new FileTestDb());

    public void Dispose() => _world.Dispose();

    [Fact]
    public async Task Two_bids_of_the_same_amount_at_the_same_moment_cannot_both_win()
    {
        var seller = _world.AddUser("Card Shark");
        var bob = _world.AddUser("Bob");
        var carol = _world.AddUser("Carol");
        var auction = await _world.ListAsync(seller, startCents: 500, incrementCents: 100);

        var results = await Task.WhenAll(
            Task.Run(() => _world.Bidding.PlaceBidAsync(auction.Id, bob, 500)),
            Task.Run(() => _world.Bidding.PlaceBidAsync(auction.Id, carol, 500)));

        Assert.Single(results, r => r.Accepted);
        Assert.Equal("Bids must be at least $6.00.", Assert.Single(results, r => !r.Accepted).Error);
        await using var db = _world.Db.CreateDbContext();
        Assert.Equal(1, await db.Bids.CountAsync());
    }

    [Fact]
    public async Task A_burst_of_parallel_bids_never_breaks_the_increment_rule()
    {
        var seller = _world.AddUser("Card Shark");
        var bidders = Enumerable.Range(1, 8).Select(i => _world.AddUser($"Bidder {i}")).ToList();
        var auction = await _world.ListAsync(seller, startCents: 500, incrementCents: 100);

        // Everyone bids amounts that collide with each other: many must be rejected.
        await Task.WhenAll(bidders.SelectMany((bidder, i) => new[] { 500L, 600L, 700L }
            .Select(amount => Task.Run(() => _world.Bidding.PlaceBidAsync(auction.Id, bidder, amount + i % 2 * 50)))));

        await using var db = _world.Db.CreateDbContext();
        var saved = await db.Bids.Where(b => b.AuctionId == auction.Id).OrderBy(b => b.CreatedAtUtc).ThenBy(b => b.AmountCents).ToListAsync();
        Assert.NotEmpty(saved);
        Assert.True(saved[0].AmountCents >= 500);
        for (var i = 1; i < saved.Count; i++)
        {
            Assert.True(saved[i].AmountCents >= saved[i - 1].AmountCents + 100,
                $"bid {saved[i].AmountCents} was accepted over {saved[i - 1].AmountCents}");
            Assert.NotEqual(saved[i - 1].BidderId, saved[i].BidderId);
        }
    }
}
