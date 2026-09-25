// =====================================================================================================
// FILE: Maroos.Core/Email/EmailContent.cs
// PURPOSE: Two small records passed around when building and queueing emails.
// =====================================================================================================

namespace Maroos.Core.Email;

/// <summary>A finished email: subject, plain-text body and HTML body.</summary>
public sealed record EmailContent(string Subject, string TextBody, string HtmlBody);

/// <summary>An email to queue: what it is, who it's to, and (for tracing) which account and auction it's about.</summary>
public sealed record EmailMessage(EmailKind Kind, string To, EmailContent Content, string? UserId = null, Guid? AuctionId = null);
