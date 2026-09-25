// =====================================================================================================
// FILE: Maroos.Tests/Auctions/HubAndProfileHttpTests.cs
// PURPOSE: Over HTTP: the SignalR hub delivers BidPlaced/AuctionEnded to a real hub client; /profile needs
//   login and shows each tab.
// Each test's name says what it proves; the '// TEST:' line above each one repeats it in plain words.
// =====================================================================================================

using Maroos.Core.Auctions;
using Maroos.Core.Watchlist;
using Maroos.Tests.Auth;
using Maroos.Tests.Scryfall;
using Maroos.Web.Data;
using Maroos.Web.Services;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using static Maroos.Tests.Auth.PageForms;

namespace Maroos.Tests.Auctions;

public class HubAndProfileHttpTests : IClassFixture<MaroosWebFactory>, IAsyncLifetime
{
    private readonly MaroosWebFactory _factory;

    public HubAndProfileHttpTests(MaroosWebFactory factory) => _factory = factory;

    public Task InitializeAsync() => _factory.InitializeDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    // Helper: Register async.
    private async Task<(HttpClient Browser, string UserId)> RegisterAsync(string displayName) =>
        await RegisterWithEmailAsync(displayName) is var (browser, id, _) ? (browser, id) : default;

    // Helper: Register with email async.
    private async Task<(HttpClient Browser, string UserId, string Email)> RegisterWithEmailAsync(string displayName)
    {
        var email = $"{Guid.NewGuid():N}@example.com";
        var browser = _factory.NewBrowser();
        // Register, follow the confirmation link from the queued email, and log in.
        (await _factory.RegisterAndConfirmAsync(browser, email, displayName)).EnsureSuccessStatusCode();
        using var scope = _factory.Services.CreateScope();
        var user = await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().FindByEmailAsync(email);
        return (browser, user!.Id, email);
    }

    private async Task<T> WithServiceAsync<TService, T>(Func<TService, Task<T>> use) where TService : notnull
    {
        using var scope = _factory.Services.CreateScope();
        return await use(scope.ServiceProvider.GetRequiredService<TService>());
    }

    // Helper: List async.
    private Task<Auction> ListAsync(string sellerId) =>
        WithServiceAsync<AuctionService, Auction>(s => s.CreateAsync(sellerId,
            new NewAuction(Fixtures.SolRingC21Id, CardCondition.NearMint, false, 500, 100, 7)));

    // Helper: Hub client.
    private HubConnection HubClient() =>
        new HubConnectionBuilder()
            .WithUrl(new Uri(_factory.Server.BaseAddress, AuctionHub.Path.TrimStart('/')), options =>
            {
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
            })
            .Build();

    // TEST: Hub clients in an auctions group hear new bids and endings.
    [Fact]
    public async Task Hub_clients_in_an_auctions_group_hear_new_bids_and_endings()
    {
        var (_, seller) = await RegisterAsync("Hub Seller");
        var (_, bidder) = await RegisterAsync("Hub Bidder");
        var auction = await ListAsync(seller);
        var other = await ListAsync(seller);

        await using var connection = HubClient();
        var bidHeard = new TaskCompletionSource<(long Amount, string Name, int Count)>();
        var endHeard = new TaskCompletionSource<(string? Winner, long? Cents)>();
        connection.On<long, string, int>("BidPlaced", (amount, name, count) => bidHeard.TrySetResult((amount, name, count)));
        connection.On<string?, long?>("AuctionEnded", (winner, cents) => endHeard.TrySetResult((winner, cents)));
        await connection.StartAsync();
        await connection.InvokeAsync("JoinAuction", auction.Id);

        // A bid on a different auction isn't heard.
        await WithServiceAsync<BiddingService, BidResult>(b => b.PlaceBidAsync(other.Id, bidder, 900));
        await WithServiceAsync<BiddingService, BidResult>(b => b.PlaceBidAsync(auction.Id, bidder, 750));

        Assert.Equal((750L, "Hub Bidder", 1), await bidHeard.Task.WaitAsync(TimeSpan.FromSeconds(10)));

        await _factory.Services.GetRequiredService<AuctionEvents>().PublishAsync(new AuctionEnded(auction.Id, "Hub Bidder", 750));
        Assert.Equal(("Hub Bidder", (long?)750), await endHeard.Task.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    // TEST: Profile needs an account.
    [Fact]
    public async Task Profile_needs_an_account()
    {
        var response = await _factory.NewBrowser().GetAsync("/profile");

        Assert.Equal("/Account/Login", response.RequestMessage!.RequestUri!.AbsolutePath);
    }

    // TEST: Profile tabs show decks listings bids and the watchlist.
    [Fact]
    public async Task Profile_tabs_show_decks_listings_bids_and_the_watchlist()
    {
        var (sellerBrowser, seller, sellerEmail) = await RegisterWithEmailAsync("Profile Seller");
        var (bidderBrowser, bidder) = await RegisterAsync("Profile Bidder");
        var auction = await ListAsync(seller);
        await WithServiceAsync<BiddingService, BidResult>(b => b.PlaceBidAsync(auction.Id, bidder, 650));
        var deck = await WithServiceAsync<DeckService, Maroos.Core.Decks.Deck>(d => d.CreateAsync(seller, "Profile Burn", "modern", isPublic: true));
        await WithServiceAsync<WatchService, bool>(w => w.ToggleAsync(bidder, WatchTarget.Auction, auction.Id));
        await WithServiceAsync<WatchService, bool>(w => w.ToggleAsync(bidder, WatchTarget.Deck, deck.Id));

        var decksTab = await sellerBrowser.GetStringAsync("/profile");
        Assert.Contains("Profile Seller", decksTab);
        Assert.Contains($"href=\"decks/{deck.Id}\"", decksTab);

        var listingsTab = await sellerBrowser.GetStringAsync("/profile?tab=listings");
        Assert.Contains($"href=\"auctions/{auction.Id}\"", listingsTab);
        Assert.Contains("$6.50", listingsTab);

        var bidsTab = await bidderBrowser.GetStringAsync("/profile?tab=bids");
        Assert.Contains($"href=\"auctions/{auction.Id}\"", bidsTab);
        Assert.Contains("data-state=\"Winning\"", bidsTab);

        var watchTab = await bidderBrowser.GetStringAsync("/profile?tab=watchlist");
        Assert.Contains($"href=\"auctions/{auction.Id}\"", watchTab);
        Assert.Contains($"href=\"decks/{deck.Id}\"", watchTab);
        Assert.Contains("by Profile Seller", watchTab);
        // The bidder sees the seller's display name, never their email.
        Assert.DoesNotContain(sellerEmail, watchTab);
        Assert.DoesNotContain(sellerEmail, bidsTab);
    }
}
