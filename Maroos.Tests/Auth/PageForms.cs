using System.Text.RegularExpressions;
using Maroos.Core.Email;
using Maroos.Web.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Maroos.Tests.Auth;

/// <summary>Posts the site's server-rendered forms over HTTP the way a browser does (antiforgery token, cookies).</summary>
public static partial class PageForms
{
    public const string Password = "Correct-horse-1";

    public static HttpClient NewBrowser(this MaroosWebFactory factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = true, HandleCookies = true });

    [GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"")]
    private static partial Regex AntiforgeryField();

    public static async Task<string> AntiforgeryTokenAsync(HttpClient browser, string url)
    {
        var html = await browser.GetStringAsync(url);
        return AntiforgeryField().Match(html).Groups[1].Value;
    }

    public static async Task<HttpResponseMessage> PostFormAsync(HttpClient browser, string url, string handler, Dictionary<string, string> fields)
    {
        fields["__RequestVerificationToken"] = await AntiforgeryTokenAsync(browser, url);
        fields["_handler"] = handler;
        return await browser.PostAsync(url, new FormUrlEncodedContent(fields));
    }

    public static Task<HttpResponseMessage> RegisterAsync(HttpClient browser, string email, string displayName = "Test Player") =>
        PostFormAsync(browser, "/Account/Register", "register", new()
        {
            ["Input.DisplayName"] = displayName,
            ["Input.Email"] = email,
            ["Input.Password"] = Password,
            ["Input.ConfirmPassword"] = Password,
        });

    public static Task<HttpResponseMessage> LoginAsync(HttpClient browser, string email, string password) =>
        PostFormAsync(browser, "/Account/Login", "login", new()
        {
            ["Input.Email"] = email,
            ["Input.Password"] = password,
        });

    [GeneratedRegex(@"https?://\S+")]
    private static partial Regex Link();

    /// <summary>The newest queued email of this kind to this address. Test hosts don't run the dispatcher, so emails stay queued.</summary>
    public static async Task<OutgoingEmail> LatestEmailAsync(this MaroosWebFactory factory, string to, EmailKind kind)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.OutgoingEmails.AsNoTracking()
            .Where(e => e.ToAddress == to && e.Kind == kind)
            .OrderByDescending(e => e.CreatedAtUtc)
            .FirstAsync();
    }

    /// <summary>The first link in an email's text body.</summary>
    public static string LinkIn(OutgoingEmail email) => Link().Match(email.TextBody!).Value;

    /// <summary>
    /// Registers, follows the confirmation link from the queued email, and logs in: the signed-in browser that
    /// <see cref="RegisterAsync"/> alone gave before email confirmation was required.
    /// </summary>
    public static async Task<HttpResponseMessage> RegisterAndConfirmAsync(this MaroosWebFactory factory, HttpClient browser, string email, string displayName = "Test Player")
    {
        (await RegisterAsync(browser, email, displayName)).EnsureSuccessStatusCode();
        var confirmation = await factory.LatestEmailAsync(email, EmailKind.ConfirmAccount);
        (await browser.GetAsync(LinkIn(confirmation))).EnsureSuccessStatusCode();
        return await LoginAsync(browser, email, Password);
    }
}
