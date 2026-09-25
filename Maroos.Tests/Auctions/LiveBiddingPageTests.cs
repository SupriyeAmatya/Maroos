// =====================================================================================================
// FILE: Maroos.Tests/Auctions/LiveBiddingPageTests.cs
// PURPOSE: Phase 6 "done when": two users on the same auction page see each other's bids live, a too-low
//   bid is rejected, and the page shows the winner the moment time runs out.
// Each test's name says what it proves; the '// TEST:' line above each one repeats it in plain words.
// =====================================================================================================

using System.Security.Claims;
using Bunit;
using Maroos.Core.Auctions;
using Maroos.Core.Watchlist;
using Maroos.Web.Components.Pages;
using Maroos.Web.Components.Shared;
using Maroos.Web.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Maroos.Tests.Auctions;

/// <summary>
/// The phase 6 "done when": two browsers logged in as different users see each other's bids live,
/// a too-low bid is rejected, and an ended auction shows the winner.
/// </summary>
public sealed class LiveBiddingPageTests : IDisposable
{
    // Each page gets its own database connection, like the real site (pages tick and reload in parallel).
    private readonly AuctionWorld _world = new(new FileTestDb());
    private readonly List<BunitContext> _browsers = [];
    private readonly string _seller;
    private readonly string _bob;
    private readonly string _carol;

    public LiveBiddingPageTests()
    {
        _seller = _world.AddUser("Card Shark", "seller@example.com");
        _bob = _world.AddUser("Bob", "bob@example.com");
        _carol = _world.AddUser("Carol", "carol@example.com");
    }

    public void Dispose()
    {
        foreach (var browser in _browsers)
            browser.Dispose();
        _world.Dispose();
    }

    /// <summary>A separate "browser" signed in as <paramref name="userId"/> (or anonymous), sharing the same server.</summary>
    private BunitContext Browser(string? userId)
    {
        var browser = new BunitContext();
        browser.Services.AddSingleton(_world.Cards);
        browser.Services.AddSingleton(_world.Auctions);
        browser.Services.AddSingleton(_world.Bidding);
        browser.Services.AddSingleton(_world.Watches);
        browser.Services.AddSingleton(_world.Events);
        browser.Services.AddSingleton<TimeProvider>(_world.Clock);
        browser.Services.AddLogging();
        var auth = browser.AddAuthorization();
        if (userId is not null)
        {
            auth.SetAuthorized(userId);
            auth.SetClaims(new Claim(ClaimTypes.NameIdentifier, userId));
        }
        _browsers.Add(browser);
        return browser;
    }

    // Helper: Open.
    private IRenderedComponent<AuctionPage> Open(BunitContext browser, Guid auctionId)
    {
        var page = browser.Render<AuctionPage>(p => p.Add(a => a.Id, auctionId));
        page.WaitForAssertion(() => Assert.DoesNotContain("Loading auction", page.Markup));
        return page;
    }

    // Helper: Bid.
    private static void Bid(IRenderedComponent<AuctionPage> page, string amount)
    {
        page.Find("#bid-amount").Change(amount);
        page.FindAll("button").Single(b => b.TextContent.Trim() == "Place bid").Click();
    }

    // Helper: Message.
    private static string Message(IRenderedComponent<AuctionPage> page) => page.Find(".auction-message").TextContent.Trim();

    // TEST: Two users see each others bids live.
    [Fact]
    public async Task Two_users_see_each_others_bids_live()
    {
        var auction = await _world.ListAsync(_seller, startCents: 500, incrementCents: 100);
        var bobsPage = Open(Browser(_bob), auction.Id);
        var carolsPage = Open(Browser(_carol), auction.Id);
        Assert.Equal("5.00", bobsPage.Find("#bid-amount").GetAttribute("value"));

        Bid(bobsPage, "5.00");
        bobsPage.WaitForAssertion(() => Assert.Equal("Your bid of $5.00 is the highest.", Message(bobsPage)));
        Assert.Contains("You're the highest bidder", bobsPage.Markup);

        // Carol's page updates without a refresh.
        carolsPage.WaitForAssertion(() => Assert.Equal("$5.00", carolsPage.Find(".auction-price").TextContent));
        Assert.Equal("New bid: $5.00 by Bob.", Message(carolsPage));
        Assert.Equal("6.00", carolsPage.Find("#bid-amount").GetAttribute("value"));

        Bid(carolsPage, "6.00");
        bobsPage.WaitForAssertion(() => Assert.Equal("You've been outbid: Carol bid $6.00.", Message(bobsPage)));
        Assert.Equal("$6.00", bobsPage.Find(".auction-price").TextContent);
        Assert.Equal("2", bobsPage.Find(".auction-bid-count").TextContent);
        Assert.Equal(["$6.00", "$5.00"], bobsPage.FindAll(".bid-history-amount").Select(a => a.TextContent));
        Assert.Contains("Bob (you)", bobsPage.Find(".bid-history").TextContent);
    }

    // TEST: A too low bid is rejected and nothing is saved.
    [Fact]
    public async Task A_too_low_bid_is_rejected_and_nothing_is_saved()
    {
        var auction = await _world.ListAsync(_seller, startCents: 500);
        var page = Open(Browser(_bob), auction.Id);

        Bid(page, "4.99");
        page.WaitForAssertion(() => Assert.Equal("Bids must be at least $5.00.", Message(page)));

        Bid(page, "five");
        Assert.Equal("Enter your bid in dollars, like 12.50.", Message(page));
        Assert.Equal("0", page.Find(".auction-bid-count").TextContent);
    }

    // TEST: The countdown ticks every second.
    [Fact]
    public async Task The_countdown_ticks_every_second()
    {
        var auction = await _world.ListAsync(_seller, days: 1);
        var page = Open(Browser(_bob), auction.Id);
        page.WaitForAssertion(() => Assert.Equal("1d 00:00:00", page.Find(".auction-countdown").TextContent));

        _world.Clock.Advance(TimeSpan.FromSeconds(5));

        // The page ticks on a real one-second timer; allow for a busy test machine.
        page.WaitForAssertion(() => Assert.Equal("23:59:55", page.Find(".auction-countdown").TextContent), TimeSpan.FromSeconds(5));
    }

    // TEST: When time runs out the open page shows the winner and contact details.
    [Fact]
    public async Task When_time_runs_out_the_open_page_shows_the_winner_and_contact_details()
    {
        var auction = await _world.ListAsync(_seller, days: 1);
        await _world.Bidding.PlaceBidAsync(auction.Id, _bob, 500);
        var bobsPage = Open(Browser(_bob), auction.Id);
        var sellersPage = Open(Browser(_seller), auction.Id);
        var carolsPage = Open(Browser(_carol), auction.Id);

        _world.Clock.Advance(TimeSpan.FromDays(1));

        bobsPage.WaitForAssertion(() => Assert.Equal("Won by Bob for $5.00.", bobsPage.Find(".auction-result").TextContent.Trim()), TimeSpan.FromSeconds(3));
        Assert.Equal("Ended", bobsPage.Find(".auction-status").TextContent);
        Assert.Contains("You won! Email the seller at seller@example.com", bobsPage.Find(".auction-contact").TextContent);
        Assert.Empty(bobsPage.FindAll("#bid-amount"));

        sellersPage.WaitForAssertion(() => Assert.Contains("Contact the winner, Bob, at bob@example.com", sellersPage.Find(".auction-contact").TextContent), TimeSpan.FromSeconds(3));

        carolsPage.WaitForAssertion(() => Assert.Equal("Won by Bob for $5.00.", carolsPage.Find(".auction-result").TextContent.Trim()), TimeSpan.FromSeconds(3));
        Assert.Empty(carolsPage.FindAll(".auction-contact"));
        Assert.DoesNotContain("@example.com", carolsPage.Markup);
    }

    // TEST: Sellers and visitors get no bid form.
    [Fact]
    public async Task Sellers_and_visitors_get_no_bid_form()
    {
        var auction = await _world.ListAsync(_seller);

        var sellersPage = Open(Browser(_seller), auction.Id);
        Assert.Contains("This is your listing, so you can't bid on it.", sellersPage.Markup);
        Assert.Empty(sellersPage.FindAll("#bid-amount"));

        var visitorsPage = Open(Browser(null), auction.Id);
        Assert.Contains($"Account/Login?ReturnUrl=%2Fauctions%2F{auction.Id}", visitorsPage.Find(".auction-action a").GetAttribute("href"));
        Assert.Empty(visitorsPage.FindAll("#bid-amount"));
    }

    // TEST: Once someone bids the seller can no longer cancel.
    [Fact]
    public async Task Once_someone_bids_the_seller_can_no_longer_cancel()
    {
        var auction = await _world.ListAsync(_seller);
        var sellersPage = Open(Browser(_seller), auction.Id);
        Assert.Contains("Cancel auction", sellersPage.Markup);

        await _world.Bidding.PlaceBidAsync(auction.Id, _bob, 500);

        sellersPage.WaitForAssertion(() => Assert.DoesNotContain("Cancel auction", sellersPage.Markup));
        Assert.Contains("Auctions with bids can't be cancelled.", sellersPage.Markup);
    }

    // TEST: The heart button toggles the watchlist.
    [Fact]
    public async Task The_heart_button_toggles_the_watchlist()
    {
        var auction = await _world.ListAsync(_seller);
        var page = Open(Browser(_bob), auction.Id);
        var heart = page.FindComponent<WatchButton>();
        Assert.Equal("false", heart.Find("button").GetAttribute("aria-pressed"));

        heart.Find("button").Click();
        heart.WaitForAssertion(() => Assert.Equal("true", heart.Find("button").GetAttribute("aria-pressed")));
        Assert.True(await _world.Watches.IsWatchingAsync(_bob, WatchTarget.Auction, auction.Id));

        heart.Find("button").Click();
        heart.WaitForAssertion(() => Assert.Equal("false", heart.Find("button").GetAttribute("aria-pressed")));
        Assert.False(await _world.Watches.IsWatchingAsync(_bob, WatchTarget.Auction, auction.Id));

        var visitorsHeart = Open(Browser(null), auction.Id).FindComponent<WatchButton>();
        Assert.StartsWith("Account/Login?ReturnUrl=", visitorsHeart.Find("a").GetAttribute("href"));
    }
}
