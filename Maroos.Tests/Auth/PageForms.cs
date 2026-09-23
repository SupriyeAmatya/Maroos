using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;

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

    public static Task<HttpResponseMessage> RegisterAsync(HttpClient browser, string email) =>
        PostFormAsync(browser, "/Account/Register", "register", new()
        {
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
}
