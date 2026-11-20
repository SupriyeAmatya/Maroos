using Maroos.Core.Email;
using Maroos.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Maroos.Web.Services.Email;

/// <summary>
/// Sends queued emails in the background: every 5 seconds, or as soon as <see cref="EmailSignal"/> is nudged.
/// Failed sends are retried per <see cref="EmailRetry"/>; sent rows are purged after 7 days. Single instance only.
/// </summary>
public sealed class EmailDispatcher(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IEmailTransport transport,
    EmailSignal signal,
    TimeProvider clock,
    ILogger<EmailDispatcher> logger) : BackgroundService
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan KeepSentFor = TimeSpan.FromDays(7);
    private static readonly TimeSpan PurgeInterval = TimeSpan.FromHours(1);
    private const int BatchSize = 50;

    private DateTime _nextPurgeUtc = DateTime.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SendDueAsync(stoppingToken);
                var now = clock.GetUtcNow().UtcDateTime;
                if (now >= _nextPurgeUtc)
                {
                    await PurgeSentAsync(stoppingToken);
                    _nextPurgeUtc = now + PurgeInterval;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Keep running: a database hiccup must not stop email for good.
                logger.LogError(ex, "Sending queued emails failed");
            }

            await signal.WaitAsync(PollInterval, clock, stoppingToken);
        }
    }

    /// <summary>Sends every due email once. Returns how many were sent.</summary>
    public async Task<int> SendDueAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = clock.GetUtcNow().UtcDateTime;
        var due = await db.OutgoingEmails
            .Where(e => e.SentAtUtc == null && e.Attempts < EmailRetry.MaxAttempts && e.NextAttemptAtUtc <= now)
            .OrderBy(e => e.CreatedAtUtc)
            .Take(BatchSize)
            .ToListAsync(ct);

        var sent = 0;
        foreach (var email in due)
        {
            try
            {
                await transport.SendAsync(email, ct);
                email.Attempts++;
                email.SentAtUtc = clock.GetUtcNow().UtcDateTime;
                email.TextBody = null; // don't keep reset and confirmation links around
                email.HtmlBody = null;
                email.LastError = null;
                sent++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                email.Attempts++;
                email.LastError = ex.Message.Length > OutgoingEmail.MaxErrorLength ? ex.Message[..OutgoingEmail.MaxErrorLength] : ex.Message;
                if (email.Attempts >= EmailRetry.MaxAttempts)
                {
                    logger.LogError(ex, "Gave up sending {Kind} email {EmailId} after {Attempts} attempts", email.Kind, email.Id, email.Attempts);
                }
                else
                {
                    email.NextAttemptAtUtc = now + EmailRetry.DelayAfter(email.Attempts);
                    logger.LogWarning(ex, "Sending {Kind} email {EmailId} failed; retrying at {Next:u}", email.Kind, email.Id, email.NextAttemptAtUtc);
                }
            }

            // Save after each email, so a crash can resend at most one.
            await db.SaveChangesAsync(ct);
        }

        return sent;
    }

    /// <summary>Deletes emails sent more than 7 days ago. Returns how many were deleted.</summary>
    public async Task<int> PurgeSentAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var cutoff = clock.GetUtcNow().UtcDateTime - KeepSentFor;
        return await db.OutgoingEmails.Where(e => e.SentAtUtc != null && e.SentAtUtc < cutoff).ExecuteDeleteAsync(ct);
    }
}
