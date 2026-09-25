// =====================================================================================================
// FILE: Maroos.Web/Services/Email/EmailDispatcher.cs
// PURPOSE: The background sender. Runs for the app's whole life:
//   1. send every due email (not sent yet, fewer than 6 attempts, next attempt time reached);
//   2. on failure, schedule the next attempt per EmailRetry (1, 5, 15, 60, 180 minutes), or give up;
//   3. about once an hour, delete emails sent more than 7 days ago;
//   4. wait 5 seconds, or less if EmailSignal is nudged, and repeat.
// Registered in Program.cs as a singleton and as a hosted service. Test hosts remove hosted services,
// so tests call SendDueAsync() and PurgeSentAsync() themselves.
// =====================================================================================================

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
    // How often to check, how long to keep sent emails, how often to clean up, how many to send per round.
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan KeepSentFor = TimeSpan.FromDays(7);
    private static readonly TimeSpan PurgeInterval = TimeSpan.FromHours(1);
    private const int BatchSize = 50;

    // When the next clean-up is due (MinValue: at the first round).
    private DateTime _nextPurgeUtc = DateTime.MinValue;

    // ExecuteAsync: the loop described above; stoppingToken is cancelled when the app shuts down.
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

            // Step 4: wait for the next round.
            await signal.WaitAsync(PollInterval, clock, stoppingToken);
        }
    }

    /// <summary>Sends every due email once. Returns how many were sent.</summary>
    public async Task<int> SendDueAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = clock.GetUtcNow().UtcDateTime;
        // Up to 50 due emails, oldest first.
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
                // Hand it to the mail server. Success: mark it sent and forget the body.
                await transport.SendAsync(email, ct);
                email.Attempts++;
                email.SentAtUtc = clock.GetUtcNow().UtcDateTime;
                email.TextBody = null; // don't keep reset and confirmation links around
                email.HtmlBody = null;
                email.LastError = null;
                sent++;
            }
            // Failure (unless the app is shutting down): count it and keep the reason.
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                email.Attempts++;
                email.LastError = ex.Message.Length > OutgoingEmail.MaxErrorLength ? ex.Message[..OutgoingEmail.MaxErrorLength] : ex.Message;
                // Out of attempts: give up (it stays in the table, unsent, with LastError). Otherwise try again later.
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
        // One DELETE statement for everything sent before the cutoff; unsent emails are never deleted here.
        var cutoff = clock.GetUtcNow().UtcDateTime - KeepSentFor;
        return await db.OutgoingEmails.Where(e => e.SentAtUtc != null && e.SentAtUtc < cutoff).ExecuteDeleteAsync(ct);
    }
}
