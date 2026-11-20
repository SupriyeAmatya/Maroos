namespace Maroos.Core.Email;

/// <summary>Account emails. Links arrive raw (not HTML-encoded) and are encoded here for the HTML part.</summary>
public static class AccountEmailContent
{
    public static EmailContent ConfirmAccount(string link) => WithLink(
        "Confirm your Maroos account",
        "Welcome to Maroos! Confirm your email address to finish creating your account:",
        "Confirm my account", link,
        "If you didn't sign up for Maroos, you can ignore this email.");

    public static EmailContent ConfirmEmailChange(string link) => WithLink(
        "Confirm your new email address",
        "Confirm this address to start using it for your Maroos account:",
        "Confirm my new email", link,
        "If you didn't ask to change your email, you can ignore this email. Your account keeps its current address.");

    public static EmailContent PasswordReset(string link) => WithLink(
        "Reset your Maroos password",
        "Someone (hopefully you) asked to reset your Maroos password. The link works for 24 hours:",
        "Choose a new password", link,
        "If you didn't ask for this, you can ignore this email. Your password stays the same.");

    public static EmailContent PasswordResetCode(string code) => new(
        "Reset your Maroos password",
        $"Your password reset code is: {code}\n\nIf you didn't ask for this, you can ignore this email.",
        EmailHtml.Document(
            $"<p>Your password reset code is: <strong>{EmailHtml.Encode(code)}</strong></p>"
            + "<p>If you didn't ask for this, you can ignore this email.</p>"));

    private static EmailContent WithLink(string subject, string intro, string linkText, string link, string footer) => new(
        subject,
        $"{intro}\n\n{link}\n\n{footer}",
        EmailHtml.Document(
            $"<p>{EmailHtml.Encode(intro)}</p>"
            + $"<p><a href=\"{EmailHtml.Encode(link)}\">{EmailHtml.Encode(linkText)}</a></p>"
            + $"<p style=\"color:#6c757d\">{EmailHtml.Encode(footer)}</p>"));
}
