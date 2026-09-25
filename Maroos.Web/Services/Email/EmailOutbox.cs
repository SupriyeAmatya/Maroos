// =====================================================================================================
// FILE: Maroos.Web/Services/Email/EmailOutbox.cs
// PURPOSE: Where every email starts. Nothing in Maroos sends email directly: pages and services queue it
// here, as a row in the OutgoingEmails table, and EmailDispatcher sends it in the background.
//
// TWO WAYS TO QUEUE
//   QueueAsync(message)  — saves on its own (account emails, the outbid email)
//   Add(db, message)     — adds to the caller's database context, so the email saves together with the
//                          caller's other changes (the auction closer); the caller then calls Nudge().
// =====================================================================================================

using Maroos.Core.Auctions;
using Maroos.Core.Email;
using Maroos.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Maroos.Web.Services.Email;

/// <summary>
/// Queues emails in the OutgoingEmails table; <see cref="EmailDispatcher"/> sends them. Queue with
/// <see cref="Add"/> to save with the caller's other changes (then call <see cref="Nudge"/>), or with
/// <see cref="QueueAsync"/> to save on its own.
/// </summary>
public sealed class EmailOutbox(IDbContextFactory<ApplicationDbContext> dbFactory, EmailOptions options, EmailSignal signal, TimeProvider clock)
{
    // BaseUrl: the site's public address (Email:PublicBaseUrl) without a trailing slash.
    private string BaseUrl => options.PublicBaseUrl.TrimEnd('/');

    // Absolute links for emails (a background email has no web request to take the address from).
    public string AuctionUrl(Guid auctionId) => $"{BaseUrl}/auctions/{auctionId}";

    public string SettingsUrl => $"{BaseUrl}/Account/Manage";

    // InfoFor: what the auction email text needs about one auction.
    public AuctionEmailInfo InfoFor(Auction auction) => new(auction.CardName, auction.SetName, AuctionUrl(auction.Id), SettingsUrl);

    // Add: queue into the caller's context (saved by the caller's SaveChanges).
    public void Add(ApplicationDbContext db, EmailMessage message) => db.OutgoingEmails.Add(ToRow(message));

    // Nudge: wake the dispatcher (after the caller has saved).
    public void Nudge() => signal.Nudge();

    // QueueAsync: queue in a context of its own, save, and wake the dispatcher.
    public async Task QueueAsync(EmailMessage message, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        Add(db, message);
        await db.SaveChangesAsync(ct);
        Nudge();
    }

    // ToRow: the table row for a message: due straight away, subject trimmed to the column size.
    private OutgoingEmail ToRow(EmailMessage message)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var subject = message.Content.Subject;
        return new OutgoingEmail
        {
            Kind = message.Kind,
            ToAddress = message.To,
            Subject = subject.Length > OutgoingEmail.MaxSubjectLength ? subject[..OutgoingEmail.MaxSubjectLength] : subject,
            TextBody = message.Content.TextBody,
            HtmlBody = message.Content.HtmlBody,
            UserId = message.UserId,
            AuctionId = message.AuctionId,
            CreatedAtUtc = now,
            NextAttemptAtUtc = now,
        };
    }
}
