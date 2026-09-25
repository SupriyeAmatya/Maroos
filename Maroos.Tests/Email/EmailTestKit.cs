// =====================================================================================================
// FILE: Maroos.Tests/Email/EmailTestKit.cs
// PURPOSE: Shared email test helpers: a fake SMTP server that records what's sent (or fails on
// purpose), and the email settings the tests use.
// =====================================================================================================

using Maroos.Core.Email;
using Maroos.Web.Services.Email;

namespace Maroos.Tests.Email;

/// <summary>What a test transport saw (a copy: the dispatcher clears bodies after sending).</summary>
public sealed record SentEmail(EmailKind Kind, string To, string Subject, string? TextBody, string? HtmlBody, Guid? AuctionId);

/// <summary>A fake SMTP server: records every email, or fails the next <see cref="FailuresToSimulate"/> sends.</summary>
public sealed class RecordingEmailTransport : IEmailTransport
{
    // Sent: every email "delivered", in order.
    public List<SentEmail> Sent { get; } = [];
    // FailuresToSimulate: how many upcoming sends should throw, as if the server were down.
    public int FailuresToSimulate { get; set; }

    public Task SendAsync(OutgoingEmail email, CancellationToken ct = default)
    {
        if (FailuresToSimulate > 0)
        {
            FailuresToSimulate--;
            throw new IOException("SMTP server unavailable");
        }

        Sent.Add(new SentEmail(email.Kind, email.ToAddress, email.Subject, email.TextBody, email.HtmlBody, email.AuctionId));
        return Task.CompletedTask;
    }
}

// TestEmail.Options: settings pointing at https://maroos.test, so tests can check the links in emails.
public static class TestEmail
{
    public static EmailOptions Options => new()
    {
        From = "Maroos <no-reply@maroos.test>",
        PublicBaseUrl = "https://maroos.test",
        Smtp = new SmtpOptions { Host = "localhost", Port = 1025, Security = SmtpSecurity.None },
    };
}
