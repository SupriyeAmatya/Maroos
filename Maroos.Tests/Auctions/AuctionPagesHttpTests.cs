// =====================================================================================================
// FILE: Maroos.Tests/Auctions/AuctionPagesHttpTests.cs
// PURPOSE: Auction pages over real HTTP (MaroosWebFactory): listing needs an account, visitors can view but
//   not cancel, GET filters work, uploaded photos are served as JPEG with nosniff.
// Each test's name says what it proves; the '// TEST:' line above each one repeats it in plain words.
// =====================================================================================================

using Maroos.Core.Auctions;
using Maroos.Tests.Auth;
using Maroos.Tests.Scryfall;
using Maroos.Web.Data;
using Maroos.Web.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;
using static Maroos.Tests.Auth.PageForms;

namespace Maroos.Tests.Auctions;

// The phase 5 "done when": list a card, see it on /auctions, and a logged-out visitor can view but not edit it.
public class AuctionPagesHttpTests : IClassFixture<MaroosWebFactory>, IAsyncLifetime
{
    private readonly MaroosWebFactory _factory;

    public AuctionPagesHttpTests(MaroosWebFactory factory) => _factory = factory;

    public Task InitializeAsync() => _factory.InitializeDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>Registers through the real page and returns the signed-in browser and the new user's id.</summary>
    private async Task<(HttpClient Browser, string UserId)> RegisterSellerAsync(string displayName)
    {
        var email = $"{Guid.NewGuid():N}@example.com";
        var browser = _factory.NewBrowser();
        // Register, follow the confirmation link from the queued email, and log in.
        (await _factory.RegisterAndConfirmAsync(browser, email, displayName)).EnsureSuccessStatusCode();

        using var scope = _factory.Services.CreateScope();
        var user = await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().FindByEmailAsync(email);
        return (browser, user!.Id);
    }

    // Helper: List async.
    private async Task<Auction> ListAsync(string sellerId, string? photoPath = null)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AuctionService>().CreateAsync(sellerId,
            new NewAuction(Fixtures.SolRingC21Id, CardCondition.NearMint, false, 1250, 100, 7, photoPath));
    }

    // TEST: Listing a card needs an account.
    [Fact]
    public async Task Listing_a_card_needs_an_account()
    {
        var response = await _factory.NewBrowser().GetAsync("/auctions/new");

        Assert.Equal("/Account/Login", response.RequestMessage!.RequestUri!.AbsolutePath);
    }

    // TEST: A listed card shows on auctions and visitors can view but not cancel it.
    [Fact]
    public async Task A_listed_card_shows_on_auctions_and_visitors_can_view_but_not_cancel_it()
    {
        var (seller, sellerId) = await RegisterSellerAsync("Card Shark");
        var auction = await ListAsync(sellerId);
        var visitor = _factory.NewBrowser();

        var list = await visitor.GetStringAsync("/auctions");
        Assert.Contains($"href=\"auctions/{auction.Id}\"", list);
        Assert.Contains("Sol Ring", list);
        Assert.Contains("$12.50", list);

        var detail = await visitor.GetStringAsync($"/auctions/{auction.Id}");
        Assert.Contains("Sol Ring", detail);
        Assert.Contains("Card Shark", detail);
        Assert.DoesNotContain("Cancel auction", detail);

        Assert.Contains("Cancel auction", await seller.GetStringAsync($"/auctions/{auction.Id}"));
    }

    // TEST: Filters apply from the query string.
    [Fact]
    public async Task Filters_apply_from_the_query_string()
    {
        var (_, sellerId) = await RegisterSellerAsync("Filter Tester");
        var auction = await ListAsync(sellerId);

        Assert.Contains($"auctions/{auction.Id}", await _factory.NewBrowser().GetStringAsync("/auctions?set=c21&max=12.50"));
        Assert.DoesNotContain($"auctions/{auction.Id}", await _factory.NewBrowser().GetStringAsync("/auctions?max=12.49"));
        Assert.Contains("isn't a price", await _factory.NewBrowser().GetStringAsync("/auctions?max=lots"));
    }

    // TEST: Uploaded photos are served as jpegs with nosniff.
    [Fact]
    public async Task Uploaded_photos_are_served_as_jpegs_with_nosniff()
    {
        using var scope = _factory.Services.CreateScope();
        var path = await scope.ServiceProvider.GetRequiredService<AuctionPhotoStore>()
            .SaveAsync(new MemoryStream(AuctionPhotoStoreTests.Image(30, 40, SKEncodedImageFormat.Png)));

        var response = await _factory.NewBrowser().GetAsync("/" + path);

        response.EnsureSuccessStatusCode();
        Assert.Equal("image/jpeg", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
    }

    // TEST: Registering needs a display name.
    [Fact]
    public async Task Registering_needs_a_display_name()
    {
        var response = await RegisterAsync(_factory.NewBrowser(), $"{Guid.NewGuid():N}@example.com", displayName: "");

        Assert.Equal("/Account/Register", response.RequestMessage!.RequestUri!.AbsolutePath);
        Assert.Contains("Choose a display name.", await response.Content.ReadAsStringAsync());
    }
}
