using System.Security.Claims;
using Bunit;
using Maroos.Core.Auctions;
using Maroos.Tests.Scryfall;
using Maroos.Web.Components.Pages;
using Maroos.Web.Data;
using Maroos.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using SkiaSharp;

namespace Maroos.Tests.Auctions;

public sealed class AuctionPagesTests : BunitContext
{
    private readonly TestDb _db = new();
    private readonly string _photoRoot = Path.Combine(Path.GetTempPath(), $"maroos-photos-{Guid.NewGuid():N}");
    private readonly AuctionService _auctions;
    private readonly string _seller;
    private readonly string _bob;

    public AuctionPagesTests()
    {
        var cards = new CardService(FakeScryfallHandler.WithFixtures().CreateClient(), _db, new MemoryCache(new MemoryCacheOptions()), TimeProvider.System);
        _auctions = new AuctionService(_db, cards, TimeProvider.System);
        Services.AddSingleton(cards);
        Services.AddSingleton(_auctions);
        Services.AddSingleton<TimeProvider>(TimeProvider.System);
        Services.AddSingleton(new AuctionPhotoStore(new PhotoStorageOptions { Path = _photoRoot }, new TestEnvironment()));

        _seller = AddUser("seller@example.com", "Card Shark");
        _bob = AddUser("bob@example.com", "Bob");
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        _db.Dispose();
        if (Directory.Exists(_photoRoot))
            Directory.Delete(_photoRoot, recursive: true);
    }

    private string AddUser(string email, string displayName)
    {
        using var db = _db.CreateDbContext();
        var user = new ApplicationUser { UserName = email, Email = email, DisplayName = displayName };
        db.Users.Add(user);
        db.SaveChanges();
        return user.Id;
    }

    private void SignInAs(string userId)
    {
        var auth = AddAuthorization();
        auth.SetAuthorized(userId);
        auth.SetClaims(new Claim(ClaimTypes.NameIdentifier, userId));
    }

    private static IRenderedComponent<ListCard> FindCard(IRenderedComponent<ListCard> page, string name)
    {
        var input = page.Find("input[type=search]");
        input.Input(name);
        input.KeyDown(new KeyboardEventArgs { Key = "Enter" });
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("#auction-printing option")));
        return page;
    }

    private Auction[] SavedAuctions()
    {
        using var db = _db.CreateDbContext();
        return db.Auctions.AsNoTracking().ToArray();
    }

    [Fact]
    public void Listing_a_card_saves_the_auction_and_opens_it()
    {
        SignInAs(_seller);
        var page = FindCard(Render<ListCard>(), "Lightning Bolt");

        Assert.Equal("Lightning Bolt", page.Find(".new-auction-preview img").GetAttribute("alt"));
        page.Find("#auction-condition").Change(CardCondition.LightlyPlayed.ToString());
        page.Find("#auction-foil").Change(true);
        page.Find("#auction-start").Change("2.50");
        page.Find("#auction-increment").Change("0.25");
        page.Find("#auction-duration").Change("3");
        page.FindAll("button").Single(b => b.TextContent.Trim() == "List card").Click();

        page.WaitForAssertion(() => Assert.Single(SavedAuctions()));
        var auction = SavedAuctions().Single();
        Assert.Equal((Fixtures.LightningBoltId, CardCondition.LightlyPlayed, true, 250L, 25L), (auction.ScryfallId, auction.Condition, auction.IsFoil, auction.StartCents, auction.IncrementCents));
        Assert.Equal(_seller, auction.SellerId);
        Assert.Null(auction.PhotoPath);

        var navigation = Services.GetRequiredService<NavigationManager>();
        Assert.Equal($"auctions/{auction.Id}", navigation.ToBaseRelativePath(navigation.Uri));
    }

    [Fact]
    public void A_photo_upload_is_saved_with_the_listing()
    {
        SignInAs(_seller);
        var page = FindCard(Render<ListCard>(), "Lightning Bolt");

        page.FindComponent<InputFile>().UploadFiles(
            InputFileContent.CreateFromBinary(AuctionPhotoStoreTests.Image(60, 80, SKEncodedImageFormat.Png), "my-bolt.png", contentType: "image/png"));
        page.Find("#auction-start").Change("5");
        page.FindAll("button").Single(b => b.TextContent.Trim() == "List card").Click();

        page.WaitForAssertion(() => Assert.Single(SavedAuctions()));
        var auction = SavedAuctions().Single();
        Assert.Matches(@"^uploads/[0-9a-f]{32}\.jpg$", auction.PhotoPath);
        Assert.True(File.Exists(Path.Combine(_photoRoot, Path.GetFileName(auction.PhotoPath!))));
    }

    [Fact]
    public void Wrong_file_types_are_refused_before_listing()
    {
        SignInAs(_seller);
        var page = FindCard(Render<ListCard>(), "Lightning Bolt");

        page.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromText("not an image", "notes.txt", contentType: "text/plain"));
        Assert.Contains("Upload a JPG or PNG photo.", page.Markup);

        page.Find("#auction-start").Change("5");
        page.FindAll("button").Single(b => b.TextContent.Trim() == "List card").Click();
        Assert.Empty(SavedAuctions());
    }

    [Fact]
    public void Missing_card_and_bad_prices_are_explained()
    {
        SignInAs(_seller);
        var page = Render<ListCard>();

        page.Find("#auction-start").Change("five dollars");
        page.Find("#auction-increment").Change("");
        page.FindAll("button").Single(b => b.TextContent.Trim() == "List card").Click();

        Assert.Equal(
            ["Find the card you're selling first.", "Enter a starting price, like 5.00.", "Enter a bid increment, like 1.00."],
            page.FindAll(".alert-danger li").Select(li => li.TextContent));
        Assert.Empty(SavedAuctions());
    }

    [Fact]
    public async Task Auction_page_shows_the_listing_and_seller_name_to_visitors_without_a_cancel_button()
    {
        AddAuthorization(); // anonymous visitor
        var auction = await _auctions.CreateAsync(_seller, new NewAuction(Fixtures.SolRingC21Id, CardCondition.NearMint, false, 1250, 100, 7));

        var page = Render<AuctionPage>(p => p.Add(a => a.Id, auction.Id));

        page.WaitForAssertion(() => Assert.Contains("Sol Ring", page.Find("h1").TextContent));
        Assert.Contains("$12.50", page.Find(".auction-price").TextContent);
        Assert.Contains("Card Shark", page.Markup);
        Assert.DoesNotContain("seller@example.com", page.Markup);
        Assert.Contains("Open", page.Find(".badge").TextContent);
        Assert.DoesNotContain("Cancel auction", page.Markup);
    }

    [Fact]
    public async Task Other_signed_in_users_cannot_cancel()
    {
        SignInAs(_bob);
        var auction = await _auctions.CreateAsync(_seller, new NewAuction(Fixtures.SolRingC21Id, CardCondition.NearMint, false, 1250, 100, 7));

        var page = Render<AuctionPage>(p => p.Add(a => a.Id, auction.Id));

        page.WaitForAssertion(() => Assert.Contains("Sol Ring", page.Find("h1").TextContent));
        Assert.DoesNotContain("Cancel auction", page.Markup);
    }

    [Fact]
    public async Task Seller_can_cancel_after_confirming()
    {
        SignInAs(_seller);
        var auction = await _auctions.CreateAsync(_seller, new NewAuction(Fixtures.SolRingC21Id, CardCondition.NearMint, false, 1250, 100, 7));
        var page = Render<AuctionPage>(p => p.Add(a => a.Id, auction.Id));
        page.WaitForAssertion(() => Assert.Contains("(you)", page.Markup));

        page.FindAll("button").Single(b => b.TextContent.Trim() == "Cancel auction").Click();
        Assert.Contains("It can't be reopened", page.Markup);
        page.FindAll("button.btn-danger").Single(b => b.TextContent.Trim() == "Cancel auction").Click();

        page.WaitForAssertion(() => Assert.Contains("Auction cancelled.", page.Markup));
        Assert.Equal("Cancelled", page.Find("h1 + .badge, .badge").TextContent.Trim());
        Assert.Equal(AuctionStatus.Cancelled, Assert.Single(SavedAuctions()).Status);
    }

    private sealed class TestEnvironment : IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ApplicationName { get; set; } = "Maroos.Tests";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public string EnvironmentName { get; set; } = "Test";
    }
}
