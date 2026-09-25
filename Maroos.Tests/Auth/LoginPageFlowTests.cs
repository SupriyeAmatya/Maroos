// =====================================================================================================
// FILE: Maroos.Tests/Auth/LoginPageFlowTests.cs
// PURPOSE: The real Register and Login pages over HTTP: registering needs a display name, a registered user
//   can log in, wrong passwords fail.
// Each test's name says what it proves; the '// TEST:' line above each one repeats it in plain words.
// =====================================================================================================

using System.Net;
using static Maroos.Tests.Auth.PageForms;

namespace Maroos.Tests.Auth;

// Drives the real /Account/Register and /Account/Login pages over HTTP, the way a browser posts the forms.
public class LoginPageFlowTests : IClassFixture<MaroosWebFactory>, IAsyncLifetime
{
    private readonly MaroosWebFactory _factory;

    public LoginPageFlowTests(MaroosWebFactory factory) => _factory = factory;

    public Task InitializeAsync() => _factory.InitializeDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    // Helper: New browser.
    private HttpClient NewBrowser() => _factory.NewBrowser();

    // Helper: Is signed in async.
    private static async Task<bool> IsSignedInAsync(HttpClient browser, string email)
    {
        var response = await browser.GetAsync("/profile");
        var html = await response.Content.ReadAsStringAsync();
        return response.StatusCode == HttpStatusCode.OK && html.Contains($"Signed in as {email}");
    }

    // TEST: Register logout and log back in through the pages.
    [Fact]
    public async Task Register_logout_and_log_back_in_through_the_pages()
    {
        var browser = NewBrowser();
        var email = $"{Guid.NewGuid():N}@example.com";

        Assert.False(await IsSignedInAsync(browser, email));

        // Register and confirm the email (new accounts must confirm before they can log in).
        (await _factory.RegisterAndConfirmAsync(browser, email)).EnsureSuccessStatusCode();
        Assert.True(await IsSignedInAsync(browser, email));

        var logoutToken = await AntiforgeryTokenAsync(browser, "/");
        (await browser.PostAsync("/Account/Logout", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = logoutToken,
            ["ReturnUrl"] = "",
        }))).EnsureSuccessStatusCode();
        Assert.False(await IsSignedInAsync(browser, email));

        (await LoginAsync(browser, email, Password)).EnsureSuccessStatusCode();
        Assert.True(await IsSignedInAsync(browser, email));
    }

    // TEST: Wrong password on login page shows error and stays signed out.
    [Fact]
    public async Task Wrong_password_on_login_page_shows_error_and_stays_signed_out()
    {
        var email = $"{Guid.NewGuid():N}@example.com";
        // A confirmed account, so the wrong password (not confirmation) is what fails.
        (await _factory.RegisterAndConfirmAsync(NewBrowser(), email)).EnsureSuccessStatusCode();

        var browser = NewBrowser();
        var response = await LoginAsync(browser, email, "wrong-password");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("Invalid login attempt", html);
        Assert.False(await IsSignedInAsync(browser, email));
    }
}
