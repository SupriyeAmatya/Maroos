// =====================================================================================================
// FILE: Maroos.Tests/Layout/NavigationBarTests.cs
// PURPOSE: The top navigation bar over real HTTP: every link has an icon and a readable label,
// the pin button and its script are there, the old sidebar is gone, the current page is highlighted, and
// signed-in users get Account and Log out.
// =====================================================================================================

using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Maroos.Tests.Auth;
using static Maroos.Tests.Auth.PageForms;

namespace Maroos.Tests.Layout;

/// <summary>
/// The top navigation bar: icon links whose labels stay readable to screen readers, the "always show labels"
/// pin, and the account links that depend on being signed in.
/// </summary>
public class NavigationBarTests : IClassFixture<MaroosWebFactory>, IAsyncLifetime
{
    private readonly MaroosWebFactory _factory;

    public NavigationBarTests(MaroosWebFactory factory) => _factory = factory;

    public Task InitializeAsync() => _factory.InitializeDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    // PageAsync: fetch a page and parse its HTML (AngleSharp), so tests can query it like a browser would.
    private static async Task<IDocument> PageAsync(HttpClient browser, string url) =>
        new HtmlParser().ParseDocument(await browser.GetStringAsync(url));

    // MainNav: the bar's <nav aria-label="Main">, or fail the test.
    private static IElement MainNav(IDocument page) =>
        page.QuerySelector("nav[aria-label='Main']") ?? throw new Xunit.Sdk.XunitException("No main navigation bar.");

    // Link: the bar's link to a given address, or fail the test.
    private static IElement Link(IElement nav, string href) =>
        nav.QuerySelector($".nav-items a[href='{href}']") ?? throw new Xunit.Sdk.XunitException($"No link to '{href}' in the bar.");

    // TEST: Each link has an icon and a label screen readers can read.
    [Theory]
    [InlineData("", "Home")]
    [InlineData("search", "Search")]
    [InlineData("decks", "Decks")]
    [InlineData("auctions", "Auctions")]
    [InlineData("profile", "Profile")]
    [InlineData("Account/Register", "Register")]
    [InlineData("Account/Login", "Log in")]
    public async Task Each_link_has_an_icon_and_a_label_screen_readers_can_read(string href, string label)
    {
        var nav = MainNav(await PageAsync(_factory.NewBrowser(), "/"));
        var link = Link(nav, href);

        Assert.Equal(label, link.QuerySelector(".nav-label")?.TextContent.Trim());
        Assert.Equal("true", link.QuerySelector(".nav-icon")?.GetAttribute("aria-hidden"));
    }

    // TEST: The bar has a pin to always show labels and loads its script.
    [Fact]
    public async Task The_bar_has_a_pin_to_always_show_labels_and_loads_its_script()
    {
        var page = await PageAsync(_factory.NewBrowser(), "/");
        var pin = MainNav(page).QuerySelector("button[data-nav-pin]");

        Assert.NotNull(pin);
        Assert.Equal("false", pin.GetAttribute("aria-pressed"));
        Assert.Equal("Always show labels", pin.QuerySelector(".nav-label")?.TextContent.Trim());
        // In <head>, so a pinned bar is applied before the page is drawn.
        Assert.NotNull(page.Head!.QuerySelector("script[src^='nav']"));
    }

    // TEST: The old sidebar is gone.
    [Fact]
    public async Task The_old_sidebar_is_gone()
    {
        var page = await PageAsync(_factory.NewBrowser(), "/");

        Assert.Null(page.QuerySelector(".sidebar"));
        Assert.Null(page.QuerySelector(".navbar-toggler"));
    }

    // TEST: The current page is highlighted.
    [Fact]
    public async Task The_current_page_is_highlighted()
    {
        var nav = MainNav(await PageAsync(_factory.NewBrowser(), "/auctions"));

        Assert.Contains("active", Link(nav, "auctions").ClassList);
        Assert.DoesNotContain("active", Link(nav, "decks").ClassList);
    }

    // TEST: Signed in users get account and log out instead of register and log in.
    [Fact]
    public async Task Signed_in_users_get_account_and_log_out_instead_of_register_and_log_in()
    {
        var browser = _factory.NewBrowser();
        (await _factory.RegisterAndConfirmAsync(browser, $"{Guid.NewGuid():N}@example.com")).EnsureSuccessStatusCode();

        var nav = MainNav(await PageAsync(browser, "/"));

        Assert.Equal("Account", Link(nav, "Account/Manage").QuerySelector(".nav-label")?.TextContent.Trim());
        var logout = nav.QuerySelector("form[action='Account/Logout'] button[type='submit']");
        Assert.Equal("Log out", logout?.QuerySelector(".nav-label")?.TextContent.Trim());
        Assert.Null(nav.QuerySelector("a[href='Account/Register']"));
        Assert.Null(nav.QuerySelector("a[href='Account/Login']"));
    }
}
