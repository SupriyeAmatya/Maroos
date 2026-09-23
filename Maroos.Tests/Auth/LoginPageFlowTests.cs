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

    private HttpClient NewBrowser() => _factory.NewBrowser();

    private static async Task<bool> IsSignedInAsync(HttpClient browser, string email)
    {
        var response = await browser.GetAsync("/profile");
        var html = await response.Content.ReadAsStringAsync();
        return response.StatusCode == HttpStatusCode.OK && html.Contains($"Signed in as {email}");
    }

    [Fact]
    public async Task Register_logout_and_log_back_in_through_the_pages()
    {
        var browser = NewBrowser();
        var email = $"{Guid.NewGuid():N}@example.com";

        Assert.False(await IsSignedInAsync(browser, email));

        (await RegisterAsync(browser, email)).EnsureSuccessStatusCode();
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

    [Fact]
    public async Task Wrong_password_on_login_page_shows_error_and_stays_signed_out()
    {
        var email = $"{Guid.NewGuid():N}@example.com";
        (await RegisterAsync(NewBrowser(), email)).EnsureSuccessStatusCode();

        var browser = NewBrowser();
        var response = await LoginAsync(browser, email, "wrong-password");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("Invalid login attempt", html);
        Assert.False(await IsSignedInAsync(browser, email));
    }
}
