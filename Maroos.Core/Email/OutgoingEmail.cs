namespace Maroos.Core.Email;

/// <summary>One queued email (the OutgoingEmails table). The web app's EmailDispatcher sends it.</summary>
public class OutgoingEmail
{
    public const int MaxAddressLength = 256;
    public const int MaxSubjectLength = 200;
    public const int MaxErrorLength = 500;

    public Guid Id { get; set; } = Guid.NewGuid();
    public EmailKind Kind { get; set; }
    public string ToAddress { get; set; } = "";
    public string Subject { get; set; } = "";

    /// <summary>Cleared once sent: account emails carry confirmation and password-reset links.</summary>
    public string? TextBody { get; set; }
    public string? HtmlBody { get; set; }

    /// <summary>The recipient's account, when there is one.</summary>
    public string? UserId { get; set; }
    public Guid? AuctionId { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public DateTime NextAttemptAtUtc { get; set; }
    public DateTime? SentAtUtc { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
}
