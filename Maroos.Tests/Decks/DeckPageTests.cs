using System.Security.Claims;
using Bunit;
using Maroos.Core.Decks;
using Maroos.Tests.Scryfall;
using Maroos.Web.Components.Pages;
using Maroos.Web.Data;
using Maroos.Web.Services;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace Maroos.Tests.Decks;

public sealed class DeckPageTests : BunitContext
{
    private readonly TestDb _db = new();
    private readonly DeckService _decks;
    private readonly string _alice;
    private readonly string _bob;

    public DeckPageTests()
    {
        var cards = new CardService(FakeScryfallHandler.WithFixtures().CreateClient(), _db, new MemoryCache(new MemoryCacheOptions()), TimeProvider.System);
        _decks = new DeckService(_db, cards, TimeProvider.System);
        Services.AddSingleton(cards);
        Services.AddSingleton(_decks);
        JSInterop.SetupVoid("navigator.clipboard.writeText", _ => true).SetVoidResult();

        _alice = AddUser("alice@example.com");
        _bob = AddUser("bob@example.com");
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        _db.Dispose();
    }

    private string AddUser(string email)
    {
        using var db = _db.CreateDbContext();
        var user = new ApplicationUser { UserName = email, Email = email };
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

    private async Task<Guid> DeckAsync(bool isPublic, string list)
    {
        var deck = await _decks.CreateAsync(_alice, "Burn", "modern", isPublic);
        await _decks.ImportAsync(deck.Id, _alice, list, replace: false);
        return deck.Id;
    }

    private IRenderedComponent<DeckPage> RenderDeck(Guid id)
    {
        var page = Render<DeckPage>(p => p.Add(d => d.Id, id));
        page.WaitForAssertion(() => Assert.DoesNotContain("Loading deck", page.Markup));
        return page;
    }

    private static string Heading(IRenderedComponent<DeckPage> page, string name) =>
        page.FindAll(".deck-group-heading").Select(h => h.TextContent.Trim()).Single(h => h.StartsWith(name));

    [Fact]
    public async Task Owner_sees_cards_grouped_by_type_with_counts()
    {
        SignInAs(_alice);
        var id = await DeckAsync(isPublic: false, "4 Lightning Bolt\n4 Delver of Secrets\n1 Sol Ring\n51 Mountain\nSideboard\n3 Sol Ring");

        var page = RenderDeck(id);

        Assert.Equal(["Creatures (4)", "Instants (4)", "Artifacts (1)", "Lands (51)", "Sideboard (3)"],
            page.FindAll(".deck-group-heading").Select(h => h.TextContent.Trim()));
        Assert.Contains("Main 60", page.Find(".deck-header").TextContent);
        Assert.Contains("Sideboard 3", page.Find(".deck-header").TextContent);
    }

    [Fact]
    public async Task Plus_and_minus_buttons_change_the_count()
    {
        SignInAs(_alice);
        var id = await DeckAsync(isPublic: false, "4 Lightning Bolt");
        var page = RenderDeck(id);

        page.Find("button[aria-label='Add one Lightning Bolt']").Click();
        page.WaitForAssertion(() => Assert.Equal("Instants (5)", Heading(page, "Instants")));

        page.Find("button[aria-label='Remove one Lightning Bolt']").Click();
        page.Find("button[aria-label='Remove one Lightning Bolt']").Click();
        page.WaitForAssertion(() => Assert.Equal("Instants (3)", Heading(page, "Instants")));
    }

    [Fact]
    public async Task Visitors_can_view_a_public_deck_but_not_edit_it()
    {
        SignInAs(_bob);
        var id = await DeckAsync(isPublic: true, "4 Lightning Bolt");

        var page = RenderDeck(id);

        Assert.Equal("Instants (4)", Heading(page, "Instants"));
        Assert.Empty(page.FindAll(".deck-row-actions"));
        Assert.Empty(page.FindAll(".deck-add"));
        var toolbar = page.FindAll(".deck-toolbar button").Select(b => b.TextContent.Trim()).ToList();
        Assert.Equal(["Export"], toolbar);
    }

    [Fact]
    public async Task Private_decks_look_missing_to_everyone_else()
    {
        SignInAs(_bob);
        var id = await DeckAsync(isPublic: false, "4 Lightning Bolt");

        var page = RenderDeck(id);

        Assert.Contains("Deck not found", page.Markup);
        Assert.DoesNotContain("Lightning Bolt", page.Markup);
    }

    [Fact]
    public async Task Importing_a_pasted_list_adds_the_cards_and_reports_problems()
    {
        SignInAs(_alice);
        var id = (await _decks.CreateAsync(_alice, "Burn", "modern")).Id;
        var page = RenderDeck(id);

        page.FindAll(".deck-toolbar button").Single(b => b.TextContent.Trim() == "Import").Click();
        page.Find("#import-text").Change("4 Lightning Bolt\n20 Mountain\n1 Not A Real Card");
        page.FindAll(".deck-panel button").Single(b => b.TextContent.Trim() == "Import").Click();

        page.WaitForAssertion(() => Assert.Contains("Imported 24 cards.", page.Find(".deck-status").TextContent));
        Assert.Contains("Not found on Scryfall: Not A Real Card", page.Find(".deck-status").TextContent);
        Assert.Equal("Lands (20)", Heading(page, "Lands"));
    }

    [Fact]
    public async Task Adding_a_card_by_name_puts_it_on_the_chosen_board()
    {
        SignInAs(_alice);
        var id = (await _decks.CreateAsync(_alice, "Burn", "modern")).Id;
        var page = RenderDeck(id);

        page.Find(".deck-add-board").Change(Board.Side.ToString());
        var input = page.Find(".deck-add input[type=search]");
        input.Input("Lightning Bolt");
        input.KeyDown(new KeyboardEventArgs { Key = "Enter" });

        page.WaitForAssertion(() => Assert.Contains("Added Lightning Bolt to the sideboard.", page.Find(".deck-status").TextContent));
        Assert.Equal("Sideboard (1)", Heading(page, "Sideboard"));
        Assert.Equal("", page.Find(".deck-add input[type=search]").GetAttribute("value") ?? "");
    }

    [Fact]
    public async Task Adding_an_unknown_card_says_so()
    {
        SignInAs(_alice);
        var id = (await _decks.CreateAsync(_alice, "Burn", "modern")).Id;
        var page = RenderDeck(id);

        var input = page.Find(".deck-add input[type=search]");
        input.Input("Not A Real Card");
        input.KeyDown(new KeyboardEventArgs { Key = "Enter" });

        page.WaitForAssertion(() => Assert.Contains("Scryfall has no card named “Not A Real Card”.", page.Find(".deck-status").TextContent));
    }

    [Fact]
    public async Task Export_shows_the_list_and_copies_it()
    {
        SignInAs(_bob);
        var id = await DeckAsync(isPublic: true, "4 Lightning Bolt\nSideboard\n1 Sol Ring (C21) 263");
        var page = RenderDeck(id);

        page.FindAll(".deck-toolbar button").Single(b => b.TextContent.Trim() == "Export").Click();
        Assert.Equal("4 Lightning Bolt\n\nSideboard\n1 Sol Ring", page.Find("#export-text").GetAttribute("value")!.ReplaceLineEndings("\n"));

        page.Find("#export-printings").Change(true);
        Assert.Contains("1 Sol Ring (C21) 263", page.Find("#export-text").GetAttribute("value"));

        page.FindAll(".deck-panel button").Single(b => b.TextContent.Trim() == "Copy").Click();
        var copied = JSInterop.VerifyInvoke("navigator.clipboard.writeText").Arguments.Single() as string;
        Assert.Contains("1 Sol Ring (C21) 263", copied);
        Assert.Contains("Deck list copied.", page.Find(".deck-status").TextContent);
    }

    [Fact]
    public async Task A_banned_card_shows_a_warning_and_flags_its_row()
    {
        SignInAs(_alice);
        var deck = await _decks.CreateAsync(_alice, "Burn", "historic");
        await _decks.ImportAsync(deck.Id, _alice, "4 Lightning Bolt\n56 Mountain", replace: false);

        var page = RenderDeck(deck.Id);

        var issues = page.Find("#deck-issues");
        Assert.Contains("Not legal in Historic yet", issues.TextContent);
        Assert.Equal(["Lightning Bolt is banned in Historic."], page.FindAll("#deck-issues li").Select(li => li.TextContent));
        Assert.Contains("1 issue", page.Find(".deck-legal-badge").TextContent);

        var boltRow = page.FindAll(".deck-row").Single(r => r.QuerySelector(".deck-row-name")!.TextContent == "Lightning Bolt");
        Assert.Contains("deck-row-issue", boltRow.ClassName);
        Assert.Equal("Banned", boltRow.QuerySelector(".deck-row-flag")!.TextContent);
    }

    [Fact]
    public async Task A_legal_deck_says_so_and_its_curve_matches_a_manual_count()
    {
        SignInAs(_alice);
        var id = await DeckAsync(isPublic: false, "4 Lightning Bolt\n4 Delver of Secrets\n52 Mountain");

        var page = RenderDeck(id);

        Assert.Empty(page.FindAll("#deck-issues"));
        Assert.Contains("Legal in Modern", page.Find(".deck-legal-badge").TextContent);

        // Manual count: Lightning Bolt (MV 1) ×4 + Delver of Secrets (MV 1) ×4; Mountains aren't on the curve.
        Assert.Equal(["0", "8", "0", "0", "0", "0", "0", "0"], page.FindAll(".curve-bar").Select(b => b.GetAttribute("data-count")));
        Assert.Equal("Lands", CardTypes.GroupHeading(CardTypes.Land));
        Assert.Contains("52", page.FindAll(".deck-types tr").Single(r => r.GetAttribute("data-type") == CardTypes.Land).TextContent);
    }

    [Fact]
    public async Task Changing_the_format_rechecks_legality()
    {
        SignInAs(_alice);
        var id = await DeckAsync(isPublic: false, "1 Sol Ring\n59 Mountain");
        var page = RenderDeck(id);
        Assert.Equal(["Sol Ring isn't legal in Modern."], page.FindAll("#deck-issues li").Select(li => li.TextContent));

        page.FindAll(".deck-toolbar button").Single(b => b.TextContent.Trim() == "Settings").Click();
        page.Find("#settings-format").Change("vintage");
        page.FindAll(".deck-panel button").Single(b => b.TextContent.Trim() == "Save").Click();

        page.WaitForAssertion(() => Assert.Empty(page.FindAll("#deck-issues")));
        Assert.Contains("Legal in Vintage", page.Find(".deck-legal-badge").TextContent);
    }

    [Fact]
    public async Task Owner_can_rename_and_make_the_deck_public()
    {
        SignInAs(_alice);
        var id = (await _decks.CreateAsync(_alice, "Burn", "modern")).Id;
        var page = RenderDeck(id);

        page.FindAll(".deck-toolbar button").Single(b => b.TextContent.Trim() == "Settings").Click();
        page.Find("#settings-name").Change("Izzet Tempo");
        page.Find("#settings-format").Change("legacy");
        page.Find("#settings-public").Change(true);
        page.FindAll(".deck-panel button").Single(b => b.TextContent.Trim() == "Save").Click();

        page.WaitForAssertion(() => Assert.Equal("Izzet Tempo", page.Find("h1").TextContent));
        Assert.Contains("Legacy", page.Find(".deck-header").TextContent);
        Assert.Contains("Public", page.Find(".deck-header").TextContent);
    }
}
