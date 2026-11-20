using System.Net.Http.Json;
using System.Text.Json;
using Maroos.Core.Email;
using Maroos.Web.Services.Email;

namespace Maroos.Tests.Email;

/// <summary>
/// Sends through a real SMTP server and reads the message back from Mailpit's API (compose.yaml's mailpit service).
/// Needs MAROOS_TEST_SMTP_HOST (e.g. localhost), MAROOS_TEST_SMTP_PORT (1025) and MAROOS_TEST_MAILPIT_URL
/// (http://localhost:8025); skips without them.
/// </summary>
public class MailpitTests
{
    [SkippableFact]
    public async Task An_email_goes_through_smtp_with_its_text_and_html_parts()
    {
        var host = Environment.GetEnvironmentVariable("MAROOS_TEST_SMTP_HOST");
        var api = Environment.GetEnvironmentVariable("MAROOS_TEST_MAILPIT_URL");
        Skip.If(string.IsNullOrEmpty(host) || string.IsNullOrEmpty(api),
            "Set MAROOS_TEST_SMTP_HOST, MAROOS_TEST_SMTP_PORT and MAROOS_TEST_MAILPIT_URL to run (see compose.yaml).");

        var options = new EmailOptions
        {
            From = "Maroos <no-reply@maroos.local>",
            PublicBaseUrl = "http://localhost:8080",
            Smtp = new SmtpOptions
            {
                Host = host!,
                Port = int.Parse(Environment.GetEnvironmentVariable("MAROOS_TEST_SMTP_PORT") ?? "1025"),
                Security = SmtpSecurity.None,
            },
        };
        var subject = $"Maroos test {Guid.NewGuid():N}";
        var content = AccountEmailContent.PasswordReset("http://localhost:8080/Account/ResetPassword?code=abc&x=1");

        await new SmtpEmailTransport(options).SendAsync(new OutgoingEmail
        {
            ToAddress = "player@example.com",
            Subject = subject,
            TextBody = content.TextBody,
            HtmlBody = content.HtmlBody,
        });

        using var http = new HttpClient { BaseAddress = new Uri(api!) };
        using var search = await http.GetFromJsonAsync<JsonDocument>($"api/v1/search?query={Uri.EscapeDataString($"subject:\"{subject}\"")}");
        var id = search!.RootElement.GetProperty("messages")[0].GetProperty("ID").GetString();
        using var message = await http.GetFromJsonAsync<JsonDocument>($"api/v1/message/{id}");
        var root = message!.RootElement;

        Assert.Equal("player@example.com", root.GetProperty("To")[0].GetProperty("Address").GetString());
        Assert.Contains("code=abc&x=1", root.GetProperty("Text").GetString());
        Assert.Contains("code=abc&amp;x=1", root.GetProperty("HTML").GetString());
    }
}
