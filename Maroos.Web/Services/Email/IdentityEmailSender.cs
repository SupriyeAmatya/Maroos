// =====================================================================================================
// FILE: Maroos.Web/Services/Email/IdentityEmailSender.cs
// PURPOSE: The bridge between ASP.NET Core Identity and the outbox. The account pages (Register, Resend
// confirmation, Forgot password, Manage/Email) call these three methods; each queues the matching email.
// Registered in Program.cs as IEmailSender<ApplicationUser>.
// =====================================================================================================

using System.Net;
using Maroos.Core.Email;
using Maroos.Web.Data;
using Microsoft.AspNetCore.Identity;

namespace Maroos.Web.Services.Email;

/// <summary>
/// Identity's account emails (confirm, reset, email change), queued in the outbox. The template pages pass links that
/// are already HTML-encoded; they're decoded once here, and the content builders encode them again for the HTML part.
/// </summary>
public sealed class IdentityEmailSender(EmailOutbox outbox) : IEmailSender<ApplicationUser>
{
    // SendConfirmationLinkAsync: a new account's confirmation, or a new address's confirmation.
    public Task SendConfirmationLinkAsync(ApplicationUser user, string email, string confirmationLink)
    {
        var link = WebUtility.HtmlDecode(confirmationLink);
        // The account pages confirm a *new* address (Manage/Email) through the same call.
        var isChange = user.Email is not null && !string.Equals(user.Email, email, StringComparison.OrdinalIgnoreCase);
        return outbox.QueueAsync(isChange
            ? new EmailMessage(EmailKind.ConfirmEmailChange, email, AccountEmailContent.ConfirmEmailChange(link), user.Id)
            : new EmailMessage(EmailKind.ConfirmAccount, email, AccountEmailContent.ConfirmAccount(link), user.Id));
    }

    // SendPasswordResetLinkAsync: the "Forgot your password?" email.
    public Task SendPasswordResetLinkAsync(ApplicationUser user, string email, string resetLink) =>
        outbox.QueueAsync(new EmailMessage(EmailKind.PasswordReset, email,
            AccountEmailContent.PasswordReset(WebUtility.HtmlDecode(resetLink)), user.Id));

    // SendPasswordResetCodeAsync: the code-based reset used by Identity's API endpoints.
    public Task SendPasswordResetCodeAsync(ApplicationUser user, string email, string resetCode) =>
        outbox.QueueAsync(new EmailMessage(EmailKind.PasswordReset, email,
            AccountEmailContent.PasswordResetCode(resetCode), user.Id));
}
