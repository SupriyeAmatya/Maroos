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
    private string BaseUrl => options.PublicBaseUrl.TrimEnd('/');

    public string AuctionUrl(Guid auctionId) => $"{BaseUrl}/auctions/{auctionId}";

    public string SettingsUrl => $"{BaseUrl}/Account/Manage";

    public AuctionEmailInfo InfoFor(Auction auction) => new(auction.CardName, auction.SetName, AuctionUrl(auction.Id), SettingsUrl);

    public void Add(ApplicationDbContext db, EmailMessage message) => db.OutgoingEmails.Add(ToRow(message));

    public void Nudge() => signal.Nudge();

    public async Task QueueAsync(EmailMessage message, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        Add(db, message);
        await db.SaveChangesAsync(ct);
        Nudge();
    }

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
