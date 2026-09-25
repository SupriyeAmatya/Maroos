// =====================================================================================================
// FILE: Maroos.Web/Services/Email/EmailOptions.cs
// PURPOSE: The "Email" settings: who emails come from, the site's public address (for links inside
// emails), and the SMTP server. Development points at Mailpit (appsettings.Development.json); Docker sets
// them in compose.yaml; production sets Email__* environment variables (DEPLOY.md). Program.cs refuses
// to start outside Development when MissingSettings() isn't empty.
// =====================================================================================================

namespace Maroos.Web.Services.Email;

// SmtpSecurity: how the connection is encrypted. StartTls (usually port 587) upgrades a plain
// connection; SslOnConnect (port 465) is encrypted from the start; None is for Mailpit only.
public enum SmtpSecurity
{
    StartTls,
    SslOnConnect,
    None,
}

// SmtpOptions: the mail server. Username/Password are optional (Mailpit needs none).
public sealed class SmtpOptions
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public string? Username { get; set; }
    public string? Password { get; set; }
    public SmtpSecurity Security { get; set; } = SmtpSecurity.StartTls;
}

/// <summary>The <c>Email</c> settings: sender, the site's public address (for links in emails), and the SMTP server.</summary>
public sealed class EmailOptions
{
    /// <summary>Sender, e.g. <c>Maroos &lt;no-reply@maroos.example&gt;</c>.</summary>
    public string From { get; set; } = "";

    /// <summary>The site's address as users reach it, e.g. <c>https://maroos.example</c>.</summary>
    public string PublicBaseUrl { get; set; } = "";

    // Smtp: the Email:Smtp:* settings.
    public SmtpOptions Smtp { get; set; } = new();

    // Bind: read the "Email" section (defaults if it's missing).
    public static EmailOptions Bind(IConfiguration configuration) =>
        configuration.GetSection("Email").Get<EmailOptions>() ?? new EmailOptions();

    /// <summary>Settings production can't run without; empty when all are set.</summary>
    // MissingSettings: the names of required settings that are empty, e.g. ["Email:Smtp:Host"].
    public IReadOnlyList<string> MissingSettings()
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(From))
            missing.Add("Email:From");
        if (string.IsNullOrWhiteSpace(PublicBaseUrl))
            missing.Add("Email:PublicBaseUrl");
        if (string.IsNullOrWhiteSpace(Smtp.Host))
            missing.Add("Email:Smtp:Host");
        return missing;
    }
}
