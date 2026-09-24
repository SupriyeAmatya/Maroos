namespace Maroos.Web.Services.Email;

public enum SmtpSecurity
{
    StartTls,
    SslOnConnect,
    None,
}

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

    public SmtpOptions Smtp { get; set; } = new();

    public static EmailOptions Bind(IConfiguration configuration) =>
        configuration.GetSection("Email").Get<EmailOptions>() ?? new EmailOptions();

    /// <summary>Settings production can't run without; empty when all are set.</summary>
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
