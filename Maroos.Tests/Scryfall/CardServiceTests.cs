using Maroos.Core.Scryfall;
using Maroos.Web.Services;
using Microsoft.Extensions.Caching.Memory;

namespace Maroos.Tests.Scryfall;

public sealed class CardServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly FakeScryfallHandler _handler = FakeScryfallHandler.WithFixtures();
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero));
    private readonly CardService _cards;

    public CardServiceTests() =>
        _cards = new CardService(_handler.CreateClient(), _db, new MemoryCache(new MemoryCacheOptions()), _clock);

    public void Dispose() => _db.Dispose();

    private int CardRequests => _handler.Requests.Count(r => r.PathAndQuery.StartsWith("/cards/") && Guid.TryParse(r.PathAndQuery[7..], out _));

    [Fact]
    public async Task GetCardAsync_fetches_once_then_serves_from_the_database()
    {
        var first = await _cards.GetCardAsync(Fixtures.LightningBoltId);
        var second = await _cards.GetCardAsync(Fixtures.LightningBoltId);

        Assert.Equal("Lightning Bolt", first!.Name);
        Assert.Equal(first.Id, second!.Id);
        Assert.Equal(1, CardRequests);

        using var db = _db.CreateDbContext();
        var cached = Assert.Single(db.CachedCards);
        Assert.Equal("Lightning Bolt", cached.Name);
        Assert.Equal(_clock.Now, cached.FetchedAt);
    }

    [Fact]
    public async Task GetCardAsync_refetches_after_24_hours()
    {
        await _cards.GetCardAsync(Fixtures.LightningBoltId);

        _clock.Advance(TimeSpan.FromHours(23));
        await _cards.GetCardAsync(Fixtures.LightningBoltId);
        Assert.Equal(1, CardRequests);

        _clock.Advance(TimeSpan.FromHours(2));
        await _cards.GetCardAsync(Fixtures.LightningBoltId);
        Assert.Equal(2, CardRequests);

        using var db = _db.CreateDbContext();
        Assert.Equal(_clock.Now, Assert.Single(db.CachedCards).FetchedAt);
    }

    [Fact]
    public async Task GetCardAsync_returns_null_and_caches_nothing_for_unknown_cards()
    {
        Assert.Null(await _cards.GetCardAsync(Guid.NewGuid()));

        using var db = _db.CreateDbContext();
        Assert.Empty(db.CachedCards);
    }

    [Fact]
    public async Task SearchAsync_and_AutocompleteAsync_reuse_recent_results()
    {
        await _cards.SearchAsync("lightning bolt");
        await _cards.SearchAsync("  Lightning Bolt ");
        await _cards.AutocompleteAsync("lightning b");
        await _cards.AutocompleteAsync("lightning b");

        Assert.Equal(2, _handler.Requests.Count);
    }
}
