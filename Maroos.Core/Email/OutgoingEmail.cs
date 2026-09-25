// =====================================================================================================
// FILE: Maroos.Core/Email/OutgoingEmail.cs
// PURPOSE: One row of the OutgoingEmails table: the email "outbox". Every email is saved here first and
// sent later by the web app's EmailDispatcher, which retries failures (see EmailRetry).
//
// LIFE OF A ROW: queued (SentAtUtc null, Attempts 0) -> sent (SentAtUtc set, bodies cleared) -> deleted
// 7 days later. Or, after 6 failed attempts, left unsent with LastError explaining why.
// =====================================================================================================

namespace Maroos.Core.Email;

/// <summary>One queued email (the OutgoingEmails table). The web app's EmailDispatcher sends it.</summary>
public class OutgoingEmail
{
    // Column sizes (also used to trim values before saving).
    public const int MaxAddressLength = 256;
    public const int MaxSubjectLength = 200;
    public const int MaxErrorLength = 500;

    // Id: unique id of the email.
    public Guid Id { get; set; } = Guid.NewGuid();
    // Kind: what the email is for. ToAddress / Subject: where it goes and its subject line.
    public EmailKind Kind { get; set; }
    public string ToAddress { get; set; } = "";
    public string Subject { get; set; } = "";

    /// <summary>Cleared once sent: account emails carry confirmation and password-reset links.</summary>
    public string? TextBody { get; set; }
    public string? HtmlBody { get; set; }

    /// <summary>The recipient's account, when there is one.</summary>
    public string? UserId { get; set; }
    // AuctionId: the auction an auction email is about (for tracing and tests).
    public Guid? AuctionId { get; set; }

    // Timing and delivery state:
    //   CreatedAtUtc     — when it was queued
    //   NextAttemptAtUtc — when it's next due (now when queued; later after a failure)
    //   SentAtUtc        — when the mail server accepted it (null until then)
    //   Attempts         — how many sends were tried
    //   LastError        — why the last attempt failed (trimmed to 500 characters)
    public DateTime CreatedAtUtc { get; set; }
    public DateTime NextAttemptAtUtc { get; set; }
    public DateTime? SentAtUtc { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
}
