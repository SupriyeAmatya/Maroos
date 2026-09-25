// =====================================================================================================
// FILE: Maroos.Web/Services/Email/IEmailTransport.cs
// PURPOSE: The last step of sending: hand one email to a mail server.
//   IEmailTransport     — the interface (tests use a recording fake instead of a real server)
//   SmtpEmailTransport  — the real one, using MailKit over SMTP
// =====================================================================================================

using MailKit.Net.Smtp;
using MailKit.Security;
using Maroos.Core.Email;
using MimeKit;

namespace Maroos.Web.Services.Email;

/// <summary>Hands one email to a mail server. Throws if the server refuses or can't be reached.</summary>
public interface IEmailTransport
{
    Task SendAsync(OutgoingEmail email, CancellationToken ct = default);
}

/// <summary>Sends over SMTP with MailKit: Mailpit locally, any provider (Postmark, Brevo, SendGrid, SES...) in production.</summary>
public sealed class SmtpEmailTransport(EmailOptions options) : IEmailTransport
{
    public async Task SendAsync(OutgoingEmail email, CancellationToken ct = default)
    {
        // Build the message: sender, recipient, subject, and a body with both a text and an HTML part
        // (email programs show whichever they prefer).
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(options.From));
        message.To.Add(MailboxAddress.Parse(email.ToAddress));
        message.Subject = email.Subject;
        message.Body = new BodyBuilder { TextBody = email.TextBody, HtmlBody = email.HtmlBody }.ToMessageBody();

        // A fresh connection per email: connect, log in if there's a username, send, say goodbye.
        using var client = new SmtpClient();
        await client.ConnectAsync(options.Smtp.Host, options.Smtp.Port, options.Smtp.Security switch
        {
            SmtpSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
            SmtpSecurity.None => SecureSocketOptions.None,
            _ => SecureSocketOptions.StartTls,
        }, ct);
        // Mailpit needs no login; real providers do.
        if (!string.IsNullOrEmpty(options.Smtp.Username))
            await client.AuthenticateAsync(options.Smtp.Username, options.Smtp.Password ?? "", ct);
        await client.SendAsync(message, ct);
        await client.DisconnectAsync(quit: true, ct);
    }
}
