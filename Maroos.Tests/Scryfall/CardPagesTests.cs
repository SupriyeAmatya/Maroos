using Bunit;
using Maroos.Web.Components.Pages;
using Maroos.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace Maroos.Tests.Scryfall;

public sealed class CardPagesTests : BunitContext
{
    private readonly TestDb _db = new();

    public CardPagesTests()
    {
        var cards = new CardService(
            FakeScryfallHandler.WithFixtures().CreateClient(),
            _db,
            new MemoryCache(new MemoryCacheOptions()),
            TimeProvider.System);
        Services.AddSingleton(cards);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        _db.Dispose();
    }

    [Fact]
    public void Search_page_shows_a_linked_image_for_each_result()
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo("search?q=lightning%20bolt");

        var page = Render<Search>();

        page.WaitForAssertion(() =>
        {
            var links = page.FindAll(".card-grid-item");
            Assert.Equal(2, links.Count);
            Assert.Contains(links, a => a.GetAttribute("href") == $"card/{Fixtures.LightningBoltId}");
            Assert.All(links, a => Assert.NotNull(a.QuerySelector("img")));
        });
        Assert.Contains("2 cards", page.Markup);
    }

    [Fact]
    public void Pressing_enter_in_the_search_box_navigates_to_the_results_url()
    {
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("search");
        var page = Render<Search>();
        var input = page.Find("input[type=search]");

        input.Input("lightning bolt");
        input.KeyDown(new KeyboardEventArgs { Key = "Enter" });

        page.WaitForAssertion(() => Assert.Equal("search?q=lightning%20bolt", navigation.ToBaseRelativePath(navigation.Uri)));
        page.WaitForAssertion(() => Assert.Equal(2, page.FindAll(".card-grid-item").Count));
    }

    [Fact]
    public void Typing_shows_suggestions_and_arrow_keys_plus_enter_open_the_highlighted_card()
    {
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("search");
        var page = Render<Search>();
        var input = page.Find("input[type=search]");

        input.Input("lightning b");
        page.WaitForAssertion(() => Assert.Equal(5, page.FindAll("#card-suggestions li").Count), TimeSpan.FromSeconds(2));
        Assert.Equal("true", page.Find("input[type=search]").GetAttribute("aria-expanded"));

        // Find the input fresh for each key: every render gives its event handlers new ids.
        page.Find("input[type=search]").KeyDown(new KeyboardEventArgs { Key = "ArrowDown" });
        page.WaitForAssertion(() => Assert.Equal("Lightning Blow", page.Find("#card-suggestions li.active").TextContent.Trim()));
        page.Find("input[type=search]").KeyDown(new KeyboardEventArgs { Key = "ArrowDown" });
        page.WaitForAssertion(() => Assert.Equal("Lightning Bolt", page.Find("#card-suggestions li.active").TextContent.Trim()));
        Assert.Equal(page.Find("#card-suggestions li.active").Id, page.Find("input[type=search]").GetAttribute("aria-activedescendant"));

        page.Find("input[type=search]").KeyDown(new KeyboardEventArgs { Key = "Enter" });

        // The exact-name search has one match, so it lands on that card's page.
        page.WaitForAssertion(() =>
            Assert.Equal($"card/{Fixtures.LightningBoltId}", navigation.ToBaseRelativePath(navigation.Uri)));
    }

    [Fact]
    public void Clicking_a_suggestion_opens_that_card()
    {
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("search");
        var page = Render<Search>();

        page.Find("input[type=search]").Input("lightning b");
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("#card-suggestions li")), TimeSpan.FromSeconds(2));
        page.FindAll("#card-suggestions li").Single(li => li.TextContent.Trim() == "Lightning Bolt").MouseDown();

        // Exact-name search (!"Lightning Bolt") has one match, so it lands on the card page.
        page.WaitForAssertion(() =>
            Assert.Equal($"card/{Fixtures.LightningBoltId}", navigation.ToBaseRelativePath(navigation.Uri)));
    }

    [Fact]
    public void Escape_closes_the_suggestions()
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo("search");
        var page = Render<Search>();
        var input = page.Find("input[type=search]");

        input.Input("lightning b");
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("#card-suggestions li")), TimeSpan.FromSeconds(2));
        page.Find("input[type=search]").KeyDown(new KeyboardEventArgs { Key = "Escape" });

        page.WaitForAssertion(() => Assert.Empty(page.FindAll("#card-suggestions")));
    }

    [Fact]
    public void Search_page_says_when_nothing_matches()
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo("search?q=zzzznotacardzzzz");

        var page = Render<Search>();

        page.WaitForAssertion(() => Assert.Contains("No cards found", page.Markup));
    }

    [Fact]
    public void Card_page_shows_oracle_text_prices_and_legalities()
    {
        var page = Render<Card>(p => p.Add(c => c.Id, Fixtures.LightningBoltId));

        page.WaitForAssertion(() => Assert.Contains("Lightning Bolt", page.Find("h1").TextContent));
        Assert.Contains("deals 3 damage", page.Find(".oracle-text").TextContent);
        Assert.Equal("{R}", page.Find("h1 .mana-symbol").GetAttribute("alt"));
        Assert.Contains("$0.72", page.Markup);
        Assert.Contains("$3.86", page.Markup);

        var modern = page.FindAll(".legality-table tr").Single(r => r.QuerySelector("th")!.TextContent == "Modern");
        Assert.Equal("Legal", modern.QuerySelector(".badge")!.TextContent);
        var historic = page.FindAll(".legality-table tr").Single(r => r.QuerySelector("th")!.TextContent == "Historic");
        Assert.Equal("Banned", historic.QuerySelector(".badge")!.TextContent);
    }

    [Fact]
    public void Card_page_shows_both_faces_of_a_double_faced_card()
    {
        var page = Render<Card>(p => p.Add(c => c.Id, Fixtures.DelverId));

        page.WaitForAssertion(() => Assert.Equal(2, page.FindAll(".card-face").Count));
        Assert.Equal(2, page.FindAll(".card-detail-images img").Count);
        Assert.Contains("Insectile Aberration", page.Markup);
    }

    [Fact]
    public void Card_page_says_when_the_card_does_not_exist()
    {
        var page = Render<Card>(p => p.Add(c => c.Id, Guid.NewGuid()));

        page.WaitForAssertion(() => Assert.Contains("Card not found", page.Markup));
    }
}
