using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Maroos.Tests.Auth;

// Drives the real /Account/Register and /Account/Login pages over HTTP, the way a browser posts the forms.
public partial class LoginPageFlowTests : IClassFixture<MaroosWebFactory>, IAsyncLifetime
{
    private const string Password = "Correct-horse-1";
    private readonly MaroosWebFactory _factory;

    public LoginPageFlowTests(MaroosWebFactory factory) => _factory = factory;

    public Task InitializeAsync() => _factory.InitializeDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private HttpClient NewBrowser() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = true, HandleCookies = true });

    [GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"")]
    private static partial Regex AntiforgeryField();

    private static async Task<string> AntiforgeryTokenAsync(HttpClient browser, string url)
    {
        var html = await browser.GetStringAsync(url);
        return AntiforgeryField().Match(html).Groups[1].Value;
    }

    private static async Task<HttpResponseMessage> PostFormAsync(HttpClient browser, string url, string handler, Dictionary<string, string> fields)
    {
        fields["__RequestVerificationToken"] = await AntiforgeryTokenAsync(browser, url);
        fields["_handler"] = handler;
        return await browser.PostAsync(url, new FormUrlEncodedContent(fields));
    }

    private static Task<HttpResponseMessage> RegisterAsync(HttpClient browser, string email) =>
        PostFormAsync(browser, "/Account/Register", "register", new()
        {
            ["Input.Email"] = email,
            ["Input.Password"] = Password,
            ["Input.ConfirmPassword"] = Password,
        });

    private static Task<HttpResponseMessage> LoginAsync(HttpClient browser, string email, string password) =>
        PostFormAsync(browser, "/Account/Login", "login", new()
        {
            ["Input.Email"] = email,
            ["Input.Password"] = password,
        });

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
