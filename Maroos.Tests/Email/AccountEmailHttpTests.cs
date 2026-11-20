using System.Text;
using AngleSharp.Html.Parser;
using Maroos.Core.Email;
using Maroos.Tests.Auth;
using Maroos.Web.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Maroos.Tests.Auth.PageForms;

namespace Maroos.Tests.Email;

/// <summary>The account emails over real HTTP: confirmation, password reset, email change.</summary>
public class AccountEmailHttpTests : IClassFixture<MaroosWebFactory>, IAsyncLifetime
{
    private readonly MaroosWebFactory _factory;

    public AccountEmailHttpTests(MaroosWebFactory factory) => _factory = factory;

    public Task InitializeAsync() => _factory.InitializeDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static string NewEmail() => $"{Guid.NewGuid():N}@example.com";

    private async Task<ApplicationUser?> UserAsync(string email)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().FindByEmailAsync(email);
    }

    private async Task<int> EmailsToAsync(string address)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().OutgoingEmails.CountAsync(e => e.ToAddress == address);
    }

    private static async Task<bool> IsSignedInAsync(HttpClient browser, string email) =>
        (await browser.GetStringAsync("/profile")).Contains($"Signed in as {email}");

    [Fact]
    public async Task Registering_emails_a_confirmation_link_and_logging_in_waits_for_it()
    {
        var email = NewEmail();
        var browser = _factory.NewBrowser();

        var registered = await RegisterAsync(browser, email);
        registered.EnsureSuccessStatusCode();
        Assert.Equal("/Account/RegisterConfirmation", registered.RequestMessage!.RequestUri!.AbsolutePath);
        Assert.Contains("Check your email", await registered.Content.ReadAsStringAsync());

        var confirmation = await _factory.LatestEmailAsync(email, EmailKind.ConfirmAccount);
        Assert.Equal("Confirm your Maroos account", confirmation.Subject);
        var link = LinkIn(confirmation);
        Assert.StartsWith("http://localhost/Account/ConfirmEmail?", link);
        Assert.DoesNotContain("&amp;", link);

        var early = await LoginAsync(browser, email, Password);
        Assert.Contains("Confirm your email address before logging in", await early.Content.ReadAsStringAsync());
        Assert.False(await IsSignedInAsync(browser, email));

        (await browser.GetAsync(link)).EnsureSuccessStatusCode();
        Assert.True((await UserAsync(email))!.EmailConfirmed);

        (await LoginAsync(browser, email, Password)).EnsureSuccessStatusCode();
        Assert.True(await IsSignedInAsync(browser, email));
    }

    [Fact]
    public async Task Forgot_password_emails_a_link_that_sets_a_new_password()
    {
        var email = NewEmail();
        (await _factory.RegisterAndConfirmAsync(_factory.NewBrowser(), email)).EnsureSuccessStatusCode();

        var browser = _factory.NewBrowser();
        (await PostFormAsync(browser, "/Account/ForgotPassword", "forgot-password", new() { ["Input.Email"] = email }))
            .EnsureSuccessStatusCode();

        var reset = await _factory.LatestEmailAsync(email, EmailKind.PasswordReset);
        Assert.Equal("Reset your Maroos password", reset.Subject);
        var link = LinkIn(reset);
        Assert.StartsWith("http://localhost/Account/ResetPassword?", link);

        var code = QueryHelpers.ParseQuery(new Uri(link).Query)["code"].ToString();
        (await PostFormAsync(browser, link, "reset-password", new()
        {
            ["Input.Code"] = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(code)),
            ["Input.Email"] = email,
            ["Input.Password"] = "New-horse-2",
            ["Input.ConfirmPassword"] = "New-horse-2",
        })).EnsureSuccessStatusCode();

        (await LoginAsync(browser, email, "New-horse-2")).EnsureSuccessStatusCode();
        Assert.True(await IsSignedInAsync(browser, email));
    }

    [Fact]
    public async Task Forgot_password_for_an_unknown_address_sends_nothing()
    {
        var email = NewEmail();

        (await PostFormAsync(_factory.NewBrowser(), "/Account/ForgotPassword", "forgot-password", new() { ["Input.Email"] = email }))
            .EnsureSuccessStatusCode();

        Assert.Equal(0, await EmailsToAsync(email));
    }

    [Fact]
    public async Task A_new_email_address_takes_effect_only_once_confirmed()
    {
        var email = NewEmail();
        var newEmail = NewEmail();
        var browser = _factory.NewBrowser();
        (await _factory.RegisterAndConfirmAsync(browser, email)).EnsureSuccessStatusCode();

        (await PostFormAsync(browser, "/Account/Manage/Email", "change-email", new() { ["Input.NewEmail"] = newEmail }))
            .EnsureSuccessStatusCode();

        var change = await _factory.LatestEmailAsync(newEmail, EmailKind.ConfirmEmailChange);
        Assert.Equal("Confirm your new email address", change.Subject);
        Assert.NotNull(await UserAsync(email)); // not changed yet

        (await browser.GetAsync(LinkIn(change))).EnsureSuccessStatusCode();
        Assert.NotNull(await UserAsync(newEmail));
        Assert.Null(await UserAsync(email));
    }

    [Fact]
    public async Task The_auction_email_switch_is_on_by_default_and_saves_from_account_settings()
    {
        var email = NewEmail();
        var browser = _factory.NewBrowser();
        (await _factory.RegisterAndConfirmAsync(browser, email)).EnsureSuccessStatusCode();

        var page = new HtmlParser().ParseDocument(await browser.GetStringAsync("/Account/Manage"));
        var box = page.QuerySelector("input[id='Input.AuctionEmails']");
        Assert.NotNull(box);
        Assert.True(box.HasAttribute("checked"));

        // An unticked checkbox posts nothing.
        (await PostFormAsync(browser, "/Account/Manage", "profile", new() { ["Input.DisplayName"] = "Test Player" }))
            .EnsureSuccessStatusCode();
        Assert.False((await UserAsync(email))!.AuctionEmails);

        (await PostFormAsync(browser, "/Account/Manage", "profile", new()
        {
            ["Input.DisplayName"] = "Test Player",
            ["Input.AuctionEmails"] = "true",
        })).EnsureSuccessStatusCode();
        Assert.True((await UserAsync(email))!.AuctionEmails);
    }
}
