using Maroos.Core.Watchlist;

namespace Maroos.Tests.Auctions;

public sealed class WatchServiceTests : IDisposable
{
    private readonly AuctionWorld _world = new();
    private readonly string _alice;
    private readonly string _bob;

    public WatchServiceTests()
    {
        _alice = _world.AddUser("Alice");
        _bob = _world.AddUser("Bob");
    }

    public void Dispose() => _world.Dispose();

    [Fact]
    public async Task Toggling_adds_then_removes_a_watch()
    {
        var auction = await _world.ListAsync(_alice);

        Assert.True(await _world.Watches.ToggleAsync(_bob, WatchTarget.Auction, auction.Id));
        Assert.True(await _world.Watches.IsWatchingAsync(_bob, WatchTarget.Auction, auction.Id));
        Assert.False(await _world.Watches.IsWatchingAsync(_alice, WatchTarget.Auction, auction.Id));

        Assert.False(await _world.Watches.ToggleAsync(_bob, WatchTarget.Auction, auction.Id));
        Assert.False(await _world.Watches.IsWatchingAsync(_bob, WatchTarget.Auction, auction.Id));
    }

    [Fact]
    public async Task You_can_watch_public_decks_and_your_own_but_not_someone_elses_private_deck()
    {
        var publicDeck = await _world.Decks.CreateAsync(_alice, "Burn", "modern", isPublic: true);
        var privateDeck = await _world.Decks.CreateAsync(_alice, "Secret", "modern", isPublic: false);

        Assert.True(await _world.Watches.ToggleAsync(_bob, WatchTarget.Deck, publicDeck.Id));
        Assert.True(await _world.Watches.ToggleAsync(_alice, WatchTarget.Deck, privateDeck.Id));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => _world.Watches.ToggleAsync(_bob, WatchTarget.Deck, privateDeck.Id));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => _world.Watches.ToggleAsync(_bob, WatchTarget.Auction, Guid.NewGuid()));
    }

    [Fact]
    public async Task The_watchlist_shows_current_prices_and_drops_decks_that_went_private_or_were_deleted()
    {
        var auction = await _world.ListAsync(_alice, startCents: 500);
        await _world.Bidding.PlaceBidAsync(auction.Id, _bob, 650);
        var staysPublic = await _world.Decks.CreateAsync(_alice, "Burn", "modern", isPublic: true);
        var goesPrivate = await _world.Decks.CreateAsync(_alice, "Tempo", "legacy", isPublic: true);
        var deleted = await _world.Decks.CreateAsync(_alice, "Old", "modern", isPublic: true);
        foreach (var id in new[] { staysPublic.Id, goesPrivate.Id, deleted.Id })
            await _world.Watches.ToggleAsync(_bob, WatchTarget.Deck, id);
        await _world.Watches.ToggleAsync(_bob, WatchTarget.Auction, auction.Id);

        await _world.Decks.UpdateSettingsAsync(goesPrivate.Id, _alice, "Tempo", "legacy", isPublic: false);
        await _world.Decks.DeleteAsync(deleted.Id, _alice);

        var list = await _world.Watches.ListAsync(_bob);
        var deck = Assert.Single(list.Decks);
        Assert.Equal(("Burn", "Alice"), (deck.Name, deck.OwnerName));
        var watched = Assert.Single(list.Auctions);
        Assert.Equal((650L, true), (watched.CurrentCents, watched.IsOpen));

        await using var db = _world.Db.CreateDbContext();
        Assert.DoesNotContain(db.Watches, w => w.TargetId == deleted.Id);
    }
}
