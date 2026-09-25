// =====================================================================================================
// FILE: Maroos.Core/Email/AccountEmailContent.cs
// PURPOSE: The text of the account emails: confirm a new account, confirm a new email address, and reset
// a password (as a link, or as a code for the API endpoints). Each has a plain-text and an HTML version.
// =====================================================================================================

namespace Maroos.Core.Email;

/// <summary>Account emails. Links arrive raw (not HTML-encoded) and are encoded here for the HTML part.</summary>
public static class AccountEmailContent
{
    // ConfirmAccount: sent after registering (and by "Resend email confirmation").
    public static EmailContent ConfirmAccount(string link) => WithLink(
        "Confirm your Maroos account",
        "Welcome to Maroos! Confirm your email address to finish creating your account:",
        "Confirm my account", link,
        "If you didn't sign up for Maroos, you can ignore this email.");

    // ConfirmEmailChange: sent to the NEW address when someone changes their email.
    public static EmailContent ConfirmEmailChange(string link) => WithLink(
        "Confirm your new email address",
        "Confirm this address to start using it for your Maroos account:",
        "Confirm my new email", link,
        "If you didn't ask to change your email, you can ignore this email. Your account keeps its current address.");

    // PasswordReset: sent by "Forgot your password?". Identity's reset links last 1 day.
    public static EmailContent PasswordReset(string link) => WithLink(
        "Reset your Maroos password",
        "Someone (hopefully you) asked to reset your Maroos password. The link works for 24 hours:",
        "Choose a new password", link,
        "If you didn't ask for this, you can ignore this email. Your password stays the same.");

    // PasswordResetCode: the code-based reset used by Identity's API endpoints.
    public static EmailContent PasswordResetCode(string code) => new(
        "Reset your Maroos password",
        $"Your password reset code is: {code}\n\nIf you didn't ask for this, you can ignore this email.",
        EmailHtml.Document(
            $"<p>Your password reset code is: <strong>{EmailHtml.Encode(code)}</strong></p>"
            + "<p>If you didn't ask for this, you can ignore this email.</p>"));

    // WithLink: the shared layout: intro, the link (raw in the text, as a button-style link in the HTML), footer.
    private static EmailContent WithLink(string subject, string intro, string linkText, string link, string footer) => new(
        subject,
        $"{intro}\n\n{link}\n\n{footer}",
        EmailHtml.Document(
            $"<p>{EmailHtml.Encode(intro)}</p>"
            + $"<p><a href=\"{EmailHtml.Encode(link)}\">{EmailHtml.Encode(linkText)}</a></p>"
            + $"<p style=\"color:#6c757d\">{EmailHtml.Encode(footer)}</p>"));
}
