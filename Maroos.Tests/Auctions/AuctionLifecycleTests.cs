using Maroos.Core.Auctions;
using Maroos.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace Maroos.Tests.Auctions;

public sealed class AuctionLifecycleTests : IDisposable
{
    private readonly AuctionWorld _world = new();
    private readonly string _seller;
    private readonly string _bob;
    private readonly string _carol;

    public AuctionLifecycleTests()
    {
        _seller = _world.AddUser("Card Shark", "seller@example.com");
        _bob = _world.AddUser("Bob", "bob@example.com");
        _carol = _world.AddUser("Carol", "carol@example.com");
    }

    public void Dispose() => _world.Dispose();

    [Fact]
    public async Task The_list_shows_the_high_bid_and_filters_and_sorts_by_it()
    {
        var bidOn = await _world.ListAsync(_seller, startCents: 100);
        var untouched = await _world.ListAsync(_seller, startCents: 800);
        await _world.Bidding.PlaceBidAsync(bidOn.Id, _bob, 1500);

        var listed = await _world.Auctions.ListAsync(new AuctionFilter(Sort: AuctionSort.PriceLow));
        Assert.Equal([(untouched.Id, 800L, 0), (bidOn.Id, 1500L, 1)], listed.Select(a => (a.Id, a.CurrentCents, a.BidCount)));

        Assert.Equal([untouched.Id], (await _world.Auctions.ListAsync(new AuctionFilter(MaxCents: 1000))).Select(a => a.Id));
    }

    [Fact]
    public async Task Auctions_with_bids_cannot_be_cancelled()
    {
        var auction = await _world.ListAsync(_seller);
        await _world.Bidding.PlaceBidAsync(auction.Id, _bob, 500);

        Assert.False((await _world.Auctions.GetAsync(auction.Id, _seller))!.CanCancel);
        var error = await Assert.ThrowsAsync<AuctionValidationException>(() => _world.Auctions.CancelAsync(auction.Id, _seller));
        Assert.Equal("Someone has already bid, so this auction can't be cancelled.", error.Message);
    }

    [Fact]
    public async Task The_closer_ends_expired_auctions_records_the_winner_and_announces_it()
    {
        var sold = await _world.ListAsync(_seller, days: 1);
        var unsold = await _world.ListAsync(_seller, days: 1);
        var stillOpen = await _world.ListAsync(_seller, days: 3);
        await _world.Bidding.PlaceBidAsync(sold.Id, _bob, 500);
        await _world.Bidding.PlaceBidAsync(sold.Id, _carol, 700);
        var heard = new List<AuctionEvent>();
        using var _ = _world.Events.Subscribe(sold.Id, e => { heard.Add(e); return Task.CompletedTask; });

        _world.Clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal(2, await _world.Auctions.CloseExpiredAsync());
        Assert.Equal(0, await _world.Auctions.CloseExpiredAsync());

        await using var db = _world.Db.CreateDbContext();
        var byId = await db.Auctions.ToDictionaryAsync(a => a.Id);
        Assert.Equal((AuctionStatus.Ended, _carol), (byId[sold.Id].Status, byId[sold.Id].WinnerId));
        Assert.Equal((AuctionStatus.Ended, (string?)null), (byId[unsold.Id].Status, byId[unsold.Id].WinnerId));
        Assert.Equal(AuctionStatus.Active, byId[stillOpen.Id].Status);

        var ended = Assert.IsType<AuctionEnded>(Assert.Single(heard));
        Assert.Equal(("Carol", (long?)700), (ended.WinnerName, ended.WinningCents));
    }

    [Fact]
    public async Task An_ended_auction_shows_the_winner_right_away_even_before_the_closer_runs()
    {
        var auction = await _world.ListAsync(_seller, days: 1);
        await _world.Bidding.PlaceBidAsync(auction.Id, _bob, 500);

        Assert.Null((await _world.Auctions.GetAsync(auction.Id, null))!.WinnerName);

        _world.Clock.Advance(TimeSpan.FromDays(1));
        var view = (await _world.Auctions.GetAsync(auction.Id, null))!;
        Assert.False(view.IsOpen);
        Assert.Equal("Bob", view.WinnerName);
        Assert.Equal(AuctionStatus.Active, view.Auction.Status); // not closed yet
    }

    [Fact]
    public async Task Only_the_winner_and_the_seller_see_each_others_email_after_the_auction_ends()
    {
        var auction = await _world.ListAsync(_seller, days: 1);
        await _world.Bidding.PlaceBidAsync(auction.Id, _bob, 500);
        await _world.Bidding.PlaceBidAsync(auction.Id, _carol, 600);

        Assert.Null((await _world.Auctions.GetAsync(auction.Id, _carol))!.ContactEmail); // still open

        _world.Clock.Advance(TimeSpan.FromDays(1));
        await _world.Auctions.CloseExpiredAsync();

        var winner = (await _world.Auctions.GetAsync(auction.Id, _carol))!;
        Assert.True(winner.ViewerIsWinner);
        Assert.Equal("seller@example.com", winner.ContactEmail);

        Assert.Equal("carol@example.com", (await _world.Auctions.GetAsync(auction.Id, _seller))!.ContactEmail);
        Assert.Null((await _world.Auctions.GetAsync(auction.Id, _bob))!.ContactEmail);
        Assert.Null((await _world.Auctions.GetAsync(auction.Id, null))!.ContactEmail);
    }

    [Fact]
    public async Task Cancelled_and_unsold_auctions_have_no_winner_or_contact()
    {
        var cancelled = await _world.ListAsync(_seller, days: 1);
        await _world.Auctions.CancelAsync(cancelled.Id, _seller);
        var unsold = await _world.ListAsync(_seller, days: 1);
        _world.Clock.Advance(TimeSpan.FromDays(1));

        foreach (var id in new[] { cancelled.Id, unsold.Id })
        {
            var view = (await _world.Auctions.GetAsync(id, _seller))!;
            Assert.Null(view.WinnerName);
            Assert.Null(view.ContactEmail);
        }
    }

    [Fact]
    public async Task A_seller_sees_all_their_listings_with_bids_and_buyers()
    {
        var open = await _world.ListAsync(_seller, days: 3);
        var sold = await _world.ListAsync(_seller, days: 1);
        await _world.Bidding.PlaceBidAsync(sold.Id, _bob, 900);
        await _world.ListAsync(_bob);
        _world.Clock.Advance(TimeSpan.FromDays(1));

        var listings = (await _world.Auctions.ListForSellerAsync(_seller)).ToDictionary(l => l.Id);

        Assert.Equal(2, listings.Count);
        Assert.True(listings[open.Id].IsOpen);
        Assert.Null(listings[open.Id].WinnerName);
        Assert.Equal((false, 900L, 1, "Bob"), (listings[sold.Id].IsOpen, listings[sold.Id].CurrentCents, listings[sold.Id].BidCount, listings[sold.Id].WinnerName));
    }
}
