// =====================================================================================================
// FILE: Maroos.Tests/Decks/DeckPageTests.cs
// PURPOSE: bUnit tests of DeckPage.razor: owner vs visitor controls, add card, +/-, import add/replace with
//   problem lines, export, settings, delete, private decks hidden.
// Each test's name says what it proves; the '// TEST:' line above each one repeats it in plain words.
// =====================================================================================================

using System.Security.Claims;
using Bunit;
using Maroos.Core.Decks;
using Maroos.Tests.Scryfall;
using Maroos.Web.Components.Pages;
using Maroos.Web.Data;
using Maroos.Web.Services;
using Microsoft.AspNetCore.Components;
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
        Services.AddSingleton(new WatchService(_db, TimeProvider.System));
        JSInterop.SetupVoid("navigator.clipboard.writeText", _ => true).SetVoidResult();

        _alice = AddUser("alice@example.com", "Alice the Brewer");
        _bob = AddUser("bob@example.com");
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        _db.Dispose();
    }

    // Helper: Add user (optionally with a display name, which the copy credit shows).
    private string AddUser(string email, string? displayName = null)
    {
        using var db = _db.CreateDbContext();
        var user = new ApplicationUser { UserName = email, Email = email, DisplayName = displayName };
        db.Users.Add(user);
        db.SaveChanges();
        return user.Id;
    }

    // Helper: Sign in as.
    private void SignInAs(string userId)
    {
        var auth = AddAuthorization();
        auth.SetAuthorized(userId);
        auth.SetClaims(new Claim(ClaimTypes.NameIdentifier, userId));
    }

    // Helper: Deck async.
    private async Task<Guid> DeckAsync(bool isPublic, string list)
    {
        var deck = await _decks.CreateAsync(_alice, "Burn", "modern", isPublic);
        await _decks.ImportAsync(deck.Id, _alice, list, replace: false);
        return deck.Id;
    }

    // Helper: Render deck, optionally with a query such as "?view=stacks". It navigates there first, because
    // [SupplyParameterFromQuery] reads its values from the current address.
    private IRenderedComponent<DeckPage> RenderDeck(Guid id, string? query = null)
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo($"decks/{id}{query}");
        var page = Render<DeckPage>(p => p.Add(d => d.Id, id));
        page.WaitForAssertion(() => Assert.DoesNotContain("Loading deck", page.Markup));
        return page;
    }

    // Helper: Heading.
    private static string Heading(IRenderedComponent<DeckPage> page, string name) =>
        page.FindAll(".deck-group-heading").Select(h => h.TextContent.Trim()).Single(h => h.StartsWith(name));

    // TEST: Owner sees cards grouped by type with counts.
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

    // TEST: Plus and minus buttons change the count.
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

    // TEST: Visitors can view a public deck but not edit it.
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
        Assert.Equal(["Export", "Sample hand", "Copy deck"], toolbar);
    }

    // TEST: Private decks look missing to everyone else.
    [Fact]
    public async Task Private_decks_look_missing_to_everyone_else()
    {
        SignInAs(_bob);
        var id = await DeckAsync(isPublic: false, "4 Lightning Bolt");

        var page = RenderDeck(id);

        Assert.Contains("Deck not found", page.Markup);
        Assert.DoesNotContain("Lightning Bolt", page.Markup);
    }

    // TEST: Importing a pasted list adds the cards and reports problems.
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

    // TEST: Adding a card by name puts it on the chosen board.
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

    // TEST: Adding an unknown card says so.
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

    // TEST: Export shows the list and copies it.
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

    // TEST: A banned card shows a warning and flags its row.
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

    // TEST: A legal deck says so and its curve matches a manual count.
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

    // TEST: Changing the format rechecks legality.
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

    // TEST: Owner can rename and make the deck public.
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

    // Helper: the toolbar's button texts, in order (the view switch is links, so it isn't included).
    private static IReadOnlyList<string> ToolbarButtons(IRenderedComponent<DeckPage> page) =>
        page.FindAll(".deck-toolbar button").Select(b => b.TextContent.Trim()).ToList();

    // Helper: click the button with this text inside the given part of the page.
    private static void ClickButton(IRenderedComponent<DeckPage> page, string scope, string text) =>
        page.FindAll($"{scope} button").Single(b => b.TextContent.Trim() == text).Click();

    // TEST: List is the default view and the switch links to every view.
    [Fact]
    public async Task List_is_the_default_view_and_the_switch_links_to_every_view()
    {
        SignInAs(_alice);
        var id = await DeckAsync(isPublic: false, "4 Lightning Bolt\n1 Sol Ring");

        var page = RenderDeck(id);

        Assert.Equal(2, page.FindAll(".deck-row").Count);
        var links = page.FindAll(".deck-view-switch a");
        Assert.Equal(["List", "Stacks", "Grid"], links.Select(a => a.TextContent.Trim()));
        Assert.Equal([$"decks/{id}", $"decks/{id}?view=stacks", $"decks/{id}?view=grid"], links.Select(a => a.GetAttribute("href")));
        Assert.Equal("page", links[0].GetAttribute("aria-current"));
        Assert.Null(links[1].GetAttribute("aria-current"));
    }

    // TEST: Picture views show each card once in its group without edit buttons.
    [Theory]
    [InlineData("stacks", ".deck-stacks")]
    [InlineData("grid", ".deck-grid")]
    public async Task Picture_views_show_each_card_once_in_its_group_without_edit_buttons(string view, string container)
    {
        SignInAs(_alice);
        var id = await DeckAsync(isPublic: false, "4 Lightning Bolt\n1 Sol Ring\n20 Mountain\nSideboard\n2 Sol Ring");

        var page = RenderDeck(id, $"?view={view}");

        page.Find(container);
        Assert.Empty(page.FindAll(".deck-row"));
        Assert.Empty(page.FindAll(".deck-row-actions"));
        Assert.Equal(["Instants (4)", "Artifacts (1)", "Lands (20)", "Sideboard (2)"],
            page.FindAll(".deck-group-heading").Select(h => h.TextContent.Trim()));

        var tiles = page.FindAll($"{container} .deck-card-tile");
        Assert.Equal(4, tiles.Count);
        var bolt = tiles.Single(t => t.GetAttribute("aria-label")!.StartsWith("Lightning Bolt"));
        Assert.Equal("×4", bolt.QuerySelector(".deck-card-qty")?.TextContent.Trim());
        Assert.NotNull(bolt.QuerySelector("img[alt='Lightning Bolt'][loading='lazy']"));
        Assert.Null(tiles.First(t => t.GetAttribute("aria-label") == "Sol Ring").QuerySelector(".deck-card-qty"));

        // The owner can still add cards, and the switch marks the current view.
        page.Find(".deck-add");
        Assert.Equal("page", page.FindAll(".deck-view-switch a").Single(a => a.TextContent.Trim().ToLowerInvariant() == view).GetAttribute("aria-current"));
    }

    // TEST: An unknown view falls back to the list.
    [Fact]
    public async Task An_unknown_view_falls_back_to_the_list()
    {
        SignInAs(_alice);
        var id = await DeckAsync(isPublic: false, "4 Lightning Bolt");

        var page = RenderDeck(id, "?view=pictures");

        Assert.Single(page.FindAll(".deck-row"));
    }

    // TEST: Sample hand deals seven then draws and mulligans.
    [Fact]
    public async Task Sample_hand_deals_seven_then_draws_and_mulligans()
    {
        SignInAs(_bob);
        var id = await DeckAsync(isPublic: true, "4 Lightning Bolt\n56 Mountain\nSideboard\n3 Sol Ring");
        var page = RenderDeck(id);

        ClickButton(page, ".deck-toolbar", "Sample hand");
        Assert.Equal(7, page.FindAll(".sample-hand .deck-card-tile").Count);
        Assert.Contains("Hand: 7 · Library: 53", page.Find(".sample-hand-status").TextContent);
        Assert.DoesNotContain("Sol Ring", page.Find(".sample-hand").InnerHtml);

        ClickButton(page, ".sample-hand", "Draw");
        Assert.Equal(8, page.FindAll(".sample-hand .deck-card-tile").Count);
        Assert.Contains("Hand: 8 · Library: 52", page.Find(".sample-hand-status").TextContent);

        ClickButton(page, ".sample-hand", "Mulligan");
        Assert.Equal(7, page.FindAll(".sample-hand .deck-card-tile").Count);
        Assert.Contains("Mulligan 1: put 1 card on the bottom.", page.Find(".sample-hand").TextContent);

        ClickButton(page, ".sample-hand", "Mulligan");
        Assert.Contains("Mulligan 2: put 2 cards on the bottom.", page.Find(".sample-hand").TextContent);

        ClickButton(page, ".sample-hand", "New hand");
        Assert.DoesNotContain("Mulligan", page.Find(".sample-hand-status").TextContent);
        Assert.Equal(7, page.FindAll(".sample-hand .deck-card-tile").Count);
    }

    // TEST: Draw is disabled once the library is empty.
    [Fact]
    public async Task Draw_is_disabled_once_the_library_is_empty()
    {
        SignInAs(_alice);
        var id = await DeckAsync(isPublic: false, "5 Lightning Bolt");
        var page = RenderDeck(id);

        ClickButton(page, ".deck-toolbar", "Sample hand");

        Assert.Equal(5, page.FindAll(".sample-hand .deck-card-tile").Count);
        Assert.True(page.FindAll(".sample-hand button").Single(b => b.TextContent.Trim() == "Draw").HasAttribute("disabled"));
    }

    // TEST: Sample hand explains when the main deck is empty.
    [Fact]
    public async Task Sample_hand_explains_when_the_main_deck_is_empty()
    {
        SignInAs(_alice);
        var id = await DeckAsync(isPublic: false, "Sideboard\n3 Sol Ring");
        var page = RenderDeck(id);

        ClickButton(page, ".deck-toolbar", "Sample hand");

        Assert.Contains("Add cards to the main deck to draw a sample hand.", page.Find(".sample-hand").TextContent);
        Assert.Empty(page.FindAll(".sample-hand button"));
    }

    // TEST: Copy deck makes a private copy and opens it.
    // bUnit has no router, so instead of the new page we check the address the page asked to go to.
    [Fact]
    public async Task Copy_deck_makes_a_private_copy_and_opens_it()
    {
        SignInAs(_bob);
        var id = await DeckAsync(isPublic: true, "4 Lightning Bolt");
        var page = RenderDeck(id);

        ClickButton(page, ".deck-toolbar", "Copy deck");

        var copy = Assert.Single(await _decks.ListAsync(_bob));
        Assert.Equal("Copy of Burn", copy.Name);
        Assert.False(copy.IsPublic);
        // bUnit has no router, so check where the page asked to go.
        var history = Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>().History.Select(h => h.Uri);
        Assert.Contains(history, uri => uri.EndsWith($"decks/{copy.Id}"));
    }

    // TEST: Visitors get a login link instead of a copy button.
    [Fact]
    public async Task Visitors_get_a_login_link_instead_of_a_copy_button()
    {
        var id = await DeckAsync(isPublic: true, "4 Lightning Bolt");
        AddAuthorization(); // signed out

        var page = RenderDeck(id);

        Assert.DoesNotContain("Copy deck", ToolbarButtons(page));
        var login = page.Find(".deck-toolbar a.deck-copy-login");
        Assert.Equal("Log in to copy", login.TextContent.Trim());
        Assert.Equal($"Account/Login?ReturnUrl=%2Fdecks%2F{id}", login.GetAttribute("href"));
    }

    // TEST: The owner sees duplicate.
    [Fact]
    public async Task The_owner_sees_duplicate()
    {
        SignInAs(_alice);
        var id = await DeckAsync(isPublic: false, "4 Lightning Bolt");

        var page = RenderDeck(id);

        Assert.Equal(["Import", "Export", "Settings", "Sample hand", "Duplicate"], ToolbarButtons(page));
    }

    // TEST: A copy credits the original by public name and welcomes the copier once.
    // The display name shows, never the email, and ?copied=1 is removed from the address after the message.
    [Fact]
    public async Task A_copy_credits_the_original_by_public_name_and_welcomes_the_copier_once()
    {
        SignInAs(_bob);
        var sourceId = await DeckAsync(isPublic: true, "4 Lightning Bolt");
        var copyId = await _decks.CopyAsync(sourceId, _bob);

        var page = RenderDeck(copyId, "?copied=1");

        var credit = page.Find(".deck-credit");
        Assert.Equal("Copied from Burn by Alice the Brewer", credit.TextContent.Trim());
        Assert.Equal($"decks/{sourceId}", credit.QuerySelector("a")?.GetAttribute("href"));
        Assert.DoesNotContain("alice@example.com", page.Markup);
        Assert.Contains("Copied. This is your private copy.", page.Find(".deck-status").TextContent);
        // The ?copied=1 flag is dropped, so a reload doesn't show the message again.
        Assert.EndsWith($"decks/{copyId}", Services.GetRequiredService<NavigationManager>().Uri);
    }

    // TEST: No credit once the original is private.
    [Fact]
    public async Task No_credit_once_the_original_is_private()
    {
        SignInAs(_bob);
        var sourceId = await DeckAsync(isPublic: true, "4 Lightning Bolt");
        var copyId = await _decks.CopyAsync(sourceId, _bob);
        await _decks.UpdateSettingsAsync(sourceId, _alice, "Secret tech", "modern", isPublic: false);

        var page = RenderDeck(copyId);

        Assert.Empty(page.FindAll(".deck-credit"));
        Assert.DoesNotContain("Secret tech", page.Markup);
    }
}
