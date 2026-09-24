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
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(options.From));
        message.To.Add(MailboxAddress.Parse(email.ToAddress));
        message.Subject = email.Subject;
        message.Body = new BodyBuilder { TextBody = email.TextBody, HtmlBody = email.HtmlBody }.ToMessageBody();

        using var client = new SmtpClient();
        await client.ConnectAsync(options.Smtp.Host, options.Smtp.Port, options.Smtp.Security switch
        {
            SmtpSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
            SmtpSecurity.None => SecureSocketOptions.None,
            _ => SecureSocketOptions.StartTls,
        }, ct);
        if (!string.IsNullOrEmpty(options.Smtp.Username))
            await client.AuthenticateAsync(options.Smtp.Username, options.Smtp.Password ?? "", ct);
        await client.SendAsync(message, ct);
        await client.DisconnectAsync(quit: true, ct);
    }
}
