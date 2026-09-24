using Maroos.Core.Auctions;
using Maroos.Tests.Email;
using Maroos.Tests.Scryfall;
using Maroos.Web.Data;
using Maroos.Web.Services;
using Maroos.Web.Services.Email;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maroos.Tests.Auctions;

public sealed class AuctionServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero));
    private readonly AuctionService _auctions;
    private readonly string _seller;
    private readonly string _bob;

    public AuctionServiceTests()
    {
        var cards = new CardService(FakeScryfallHandler.WithFixtures().CreateClient(), _db, new MemoryCache(new MemoryCacheOptions()), _clock);
        _auctions = new AuctionService(_db, cards, new AuctionEvents(NullLogger<AuctionEvents>.Instance),
            new EmailOutbox(_db, TestEmail.Options, new EmailSignal(), _clock), _clock);
        _seller = AddUser("seller@example.com", "Card Shark");
        _bob = AddUser("bob@example.com", displayName: null);
    }

    public void Dispose() => _db.Dispose();

    private string AddUser(string email, string? displayName)
    {
        using var db = _db.CreateDbContext();
        var user = new ApplicationUser { UserName = email, Email = email, DisplayName = displayName };
        db.Users.Add(user);
        db.SaveChanges();
        return user.Id;
    }

    private Task<Auction> ListAsync(Guid? card = null, long start = 500, int days = 7, string? seller = null, CardCondition condition = CardCondition.NearMint) =>
        _auctions.CreateAsync(seller ?? _seller, new NewAuction(card ?? Fixtures.LightningBoltId, condition, false, start, 100, days));

    [Fact]
    public async Task Create_fills_in_the_card_from_scryfall_and_ends_after_the_duration()
    {
        var auction = await _auctions.CreateAsync(_seller,
            new NewAuction(Fixtures.SolRingC21Id, CardCondition.LightlyPlayed, true, 1250, 50, 3, "uploads/photo.jpg"));

        Assert.Equal("Sol Ring", auction.CardName);
        Assert.Equal("c21", auction.SetCode);
        Assert.Equal("Commander 2021", auction.SetName);
        Assert.Equal("263", auction.CollectorNumber);
        Assert.Equal(new DateTime(2026, 9, 26, 12, 0, 0), auction.EndsAtUtc);
        Assert.Equal(AuctionStatus.Active, auction.Status);

        var view = (await _auctions.GetAsync(auction.Id, viewerId: null))!;
        Assert.Equal(1250, view.CurrentCents);
        Assert.True(view.Auction.IsFoil);
        Assert.Equal("uploads/photo.jpg", view.Auction.PhotoPath);
    }

    [Fact]
    public async Task Create_rejects_invalid_listings_and_unknown_cards()
    {
        var invalid = await Assert.ThrowsAsync<AuctionValidationException>(() => ListAsync(start: 0, days: 9));
        Assert.Equal(["The starting price must be more than $0.", "Auctions run between 1 and 7 days."], invalid.Errors);

        var unknown = await Assert.ThrowsAsync<AuctionValidationException>(() => ListAsync(card: Guid.NewGuid()));
        Assert.Equal(["Scryfall has no card with that id."], unknown.Errors);

        using var db = _db.CreateDbContext();
        Assert.Empty(db.Auctions);
    }

    [Fact]
    public async Task List_shows_open_auctions_ending_soonest_first()
    {
        var later = await ListAsync(days: 7);
        var sooner = await ListAsync(days: 1);
        var cancelled = await ListAsync(days: 3);
        await _auctions.CancelAsync(cancelled.Id, _seller);

        var listed = await _auctions.ListAsync(new AuctionFilter());

        Assert.Equal([sooner.Id, later.Id], listed.Select(a => a.Id));
        Assert.All(listed, a => Assert.NotNull(a.ImageUrl));
    }

    [Fact]
    public async Task List_leaves_out_auctions_past_their_end_time()
    {
        await ListAsync(days: 1);
        var stillOpen = await ListAsync(days: 3);

        _clock.Advance(TimeSpan.FromDays(2));

        Assert.Equal([stillOpen.Id], (await _auctions.ListAsync(new AuctionFilter())).Select(a => a.Id));
    }

    [Fact]
    public async Task List_filters_by_name_set_and_max_price_and_sorts_by_price()
    {
        var cheapBolt = await ListAsync(Fixtures.LightningBoltId, start: 100);
        var pricyBolt = await ListAsync(Fixtures.LightningBoltId, start: 900);
        var solRing = await ListAsync(Fixtures.SolRingC21Id, start: 500);

        Assert.Equal([cheapBolt.Id, pricyBolt.Id], Ids(await _auctions.ListAsync(new AuctionFilter(CardName: "BOLT", Sort: AuctionSort.PriceLow))));
        Assert.Equal([solRing.Id], Ids(await _auctions.ListAsync(new AuctionFilter(SetCode: "C21"))));
        Assert.Equal([solRing.Id, cheapBolt.Id], Ids(await _auctions.ListAsync(new AuctionFilter(MaxCents: 500, Sort: AuctionSort.PriceHigh))));

        var sets = await _auctions.ListSetsAsync();
        Assert.Contains(sets, s => s.Code == "c21" && s.Name == "Commander 2021" && s.Count == 1);
        Assert.Equal(2, sets.Single(s => s.Code != "c21").Count);
    }

    [Fact]
    public async Task Seller_photo_is_shown_in_the_list_instead_of_the_scryfall_image()
    {
        await _auctions.CreateAsync(_seller, new NewAuction(Fixtures.LightningBoltId, CardCondition.NearMint, false, 500, 100, 7, "uploads/mine.jpg"));

        Assert.Equal("uploads/mine.jpg", Assert.Single(await _auctions.ListAsync(new AuctionFilter())).ImageUrl);
    }

    [Fact]
    public async Task Get_shows_the_sellers_display_name_never_their_email()
    {
        var mine = await ListAsync();
        var bobs = await ListAsync(seller: _bob);

        var view = (await _auctions.GetAsync(mine.Id, _seller))!;
        Assert.Equal("Card Shark", view.SellerName);
        Assert.True(view.IsSeller);
        Assert.True(view.CanCancel);

        var bobsView = (await _auctions.GetAsync(bobs.Id, viewerId: null))!;
        Assert.Equal(ApplicationUser.FallbackDisplayName, bobsView.SellerName);
        Assert.DoesNotContain("@", bobsView.SellerName);
        Assert.False(bobsView.IsSeller);
        Assert.False(bobsView.CanCancel);

        Assert.Null(await _auctions.GetAsync(Guid.NewGuid(), _seller));
    }

    [Fact]
    public async Task Only_the_seller_can_cancel_and_only_while_open()
    {
        var auction = await ListAsync(days: 1);

        await Assert.ThrowsAsync<AuctionAccessException>(() => _auctions.CancelAsync(auction.Id, _bob));
        Assert.Equal(AuctionStatus.Active, (await _auctions.GetAsync(auction.Id, null))!.Auction.Status);

        await _auctions.CancelAsync(auction.Id, _seller);
        var view = (await _auctions.GetAsync(auction.Id, _seller))!;
        Assert.Equal(AuctionStatus.Cancelled, view.Auction.Status);
        Assert.False(view.IsOpen);
        Assert.False(view.CanCancel);

        var ended = await ListAsync(days: 1);
        _clock.Advance(TimeSpan.FromDays(1));
        var error = await Assert.ThrowsAsync<AuctionValidationException>(() => _auctions.CancelAsync(ended.Id, _seller));
        Assert.Equal("This auction has already ended.", error.Message);
    }

    private static List<Guid> Ids(IEnumerable<AuctionSummary> auctions) => auctions.Select(a => a.Id).ToList();
}
