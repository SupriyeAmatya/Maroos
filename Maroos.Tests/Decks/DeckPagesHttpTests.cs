// =====================================================================================================
// FILE: Maroos.Tests/Decks/DeckPagesHttpTests.cs
// PURPOSE: Deck pages over HTTP: /decks and /decks/new need login, creating a deck through the real form,
//   private decks are "not found" for others.
// Each test's name says what it proves; the '// TEST:' line above each one repeats it in plain words.
// =====================================================================================================

using System.Text.RegularExpressions;
using Maroos.Tests.Auth;
using static Maroos.Tests.Auth.PageForms;

namespace Maroos.Tests.Decks;

// Checks the deck pages' [Authorize] and privacy rules over real HTTP, with real Identity cookies.
public partial class DeckPagesHttpTests : IClassFixture<MaroosWebFactory>, IAsyncLifetime
{
    private readonly MaroosWebFactory _factory;

    public DeckPagesHttpTests(MaroosWebFactory factory) => _factory = factory;

    public Task InitializeAsync() => _factory.InitializeDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [GeneratedRegex(@"/decks/([0-9a-f-]{36})$")]
    private static partial Regex DeckUrl();

    // Helper: Signed in browser async.
    private async Task<HttpClient> SignedInBrowserAsync()
    {
        var browser = _factory.NewBrowser();
        // Register, follow the confirmation link from the queued email, and log in.
        (await _factory.RegisterAndConfirmAsync(browser, $"{Guid.NewGuid():N}@example.com")).EnsureSuccessStatusCode();
        return browser;
    }

    // Helper: Create deck async.
    private static Task<HttpResponseMessage> CreateDeckAsync(HttpClient browser, string name, bool isPublic = false)
    {
        var fields = new Dictionary<string, string> { ["Input.Name"] = name, ["Input.Format"] = "modern" };
        if (isPublic)
            fields["Input.IsPublic"] = "true";
        return PostFormAsync(browser, "/decks/new", "new-deck", fields);
    }

    // TEST: Deck pages that need an account send visitors to login.
    [Theory]
    [InlineData("/decks")]
    [InlineData("/decks/new")]
    public async Task Deck_pages_that_need_an_account_send_visitors_to_login(string url)
    {
        var response = await _factory.NewBrowser().GetAsync(url);

        Assert.Equal("/Account/Login", response.RequestMessage!.RequestUri!.AbsolutePath);
    }

    // TEST: Creating a deck opens it and lists it under my decks.
    [Fact]
    public async Task Creating_a_deck_opens_it_and_lists_it_under_my_decks()
    {
        var browser = await SignedInBrowserAsync();

        var response = await CreateDeckAsync(browser, "Mono-Red Burn");
        response.EnsureSuccessStatusCode();

        var deckUrl = response.RequestMessage!.RequestUri!.AbsolutePath;
        Assert.Matches(DeckUrl(), deckUrl);
        Assert.Contains("Mono-Red Burn", await response.Content.ReadAsStringAsync());

        var myDecks = await browser.GetStringAsync("/decks");
        Assert.Contains($"href=\"decks/{DeckUrl().Match(deckUrl).Groups[1].Value}\"", myDecks);
    }

    // TEST: A blank name shows a validation message instead of creating a deck.
    [Fact]
    public async Task A_blank_name_shows_a_validation_message_instead_of_creating_a_deck()
    {
        var browser = await SignedInBrowserAsync();

        var response = await CreateDeckAsync(browser, "   ");

        Assert.Equal("/decks/new", response.RequestMessage!.RequestUri!.AbsolutePath);
        Assert.Contains("Give the deck a name.", await response.Content.ReadAsStringAsync());
    }

    // TEST: Private decks are hidden from other users and visitors.
    [Fact]
    public async Task Private_decks_are_hidden_from_other_users_and_visitors()
    {
        var owner = await SignedInBrowserAsync();
        var deckUrl = (await CreateDeckAsync(owner, "Secret Tech")).RequestMessage!.RequestUri!.AbsolutePath;

        var otherUser = await (await SignedInBrowserAsync()).GetStringAsync(deckUrl);
        var visitor = await _factory.NewBrowser().GetStringAsync(deckUrl);

        Assert.Contains("Deck not found", otherUser);
        Assert.DoesNotContain("Secret Tech", otherUser);
        Assert.Contains("Deck not found", visitor);
        Assert.Contains("Secret Tech", await owner.GetStringAsync(deckUrl));
    }

    // TEST: Public decks can be viewed without an account.
    [Fact]
    public async Task Public_decks_can_be_viewed_without_an_account()
    {
        var owner = await SignedInBrowserAsync();
        var deckUrl = (await CreateDeckAsync(owner, "Open Burn", isPublic: true)).RequestMessage!.RequestUri!.AbsolutePath;

        var html = await _factory.NewBrowser().GetStringAsync(deckUrl);

        Assert.Contains("Open Burn", html);
        Assert.DoesNotContain("Deck not found", html);
    }
}
