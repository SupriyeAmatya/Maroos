// =====================================================================================================
// FILE: Maroos.Tests/Email/EmailDeliveryTests.cs
// PURPOSE: The sending machinery: settings binding, the wake-up signal, the outbox, and the
// dispatcher (send, clear bodies, retry with backoff, give up, purge), on an in-memory database with a manual clock.
// =====================================================================================================

using System.Diagnostics;
using Maroos.Core.Email;
using Maroos.Tests.Scryfall;
using Maroos.Web.Services.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maroos.Tests.Email;

public class EmailOptionsTests
{
    // TEST: Settings bind from the email section.
    [Fact]
    public void Settings_bind_from_the_email_section()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Email:From"] = "Maroos <no-reply@maroos.example>",
            ["Email:PublicBaseUrl"] = "https://maroos.example",
            ["Email:Smtp:Host"] = "smtp.example.com",
            ["Email:Smtp:Port"] = "2525",
            ["Email:Smtp:Security"] = "SslOnConnect",
        }).Build();

        var options = EmailOptions.Bind(configuration);

        Assert.Equal("smtp.example.com", options.Smtp.Host);
        Assert.Equal(2525, options.Smtp.Port);
        Assert.Equal(SmtpSecurity.SslOnConnect, options.Smtp.Security);
        Assert.Empty(options.MissingSettings());
    }

    // TEST: Missing settings are named.
    [Fact]
    public void Missing_settings_are_named()
    {
        var options = EmailOptions.Bind(new ConfigurationBuilder().Build());

        Assert.Equal(["Email:From", "Email:PublicBaseUrl", "Email:Smtp:Host"], options.MissingSettings());
        Assert.Equal(587, options.Smtp.Port);
        Assert.Equal(SmtpSecurity.StartTls, options.Smtp.Security);
    }
}

public class EmailSignalTests
{
    // TEST: A nudge ends the wait straight away.
    [Fact]
    public async Task A_nudge_ends_the_wait_straight_away()
    {
        var signal = new EmailSignal();
        signal.Nudge();

        var watch = Stopwatch.StartNew();
        await signal.WaitAsync(TimeSpan.FromSeconds(30), TimeProvider.System, CancellationToken.None);

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
    }

    // TEST: Without a nudge the wait ends after the timeout.
    [Fact]
    public async Task Without_a_nudge_the_wait_ends_after_the_timeout()
    {
        var watch = Stopwatch.StartNew();
        await new EmailSignal().WaitAsync(TimeSpan.FromMilliseconds(50), TimeProvider.System, CancellationToken.None);

        Assert.True(watch.Elapsed >= TimeSpan.FromMilliseconds(40));
    }
}

public sealed class EmailDeliveryTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 9, 24, 12, 0, 0, TimeSpan.Zero));
    private readonly RecordingEmailTransport _transport = new();
    private readonly EmailOutbox _outbox;
    private readonly EmailDispatcher _dispatcher;

    // A fresh database, outbox and dispatcher per test, sharing one signal as in Program.cs.
    public EmailDeliveryTests()
    {
        var signal = new EmailSignal();
        _outbox = new EmailOutbox(_db, TestEmail.Options, signal, _clock);
        _dispatcher = new EmailDispatcher(_db, _transport, signal, _clock, NullLogger<EmailDispatcher>.Instance);
    }

    public void Dispose() => _db.Dispose();

    // Message: a simple email to queue.
    private static EmailMessage Message(string to, string subject = "Hello") =>
        new(EmailKind.PasswordReset, to, new EmailContent(subject, "text body", "<p>html body</p>"));

    // Row: the queued row for an address, read fresh from the database.
    private OutgoingEmail Row(string to)
    {
        using var db = _db.CreateDbContext();
        return db.OutgoingEmails.AsNoTracking().Single(e => e.ToAddress == to);
    }

    // TEST: Queued emails are due straight away and long subjects are trimmed.
    [Fact]
    public async Task Queued_emails_are_due_straight_away_and_long_subjects_are_trimmed()
    {
        await _outbox.QueueAsync(Message("player@example.com", new string('x', 300)));

        var row = Row("player@example.com");
        Assert.Equal(_clock.Now.UtcDateTime, row.CreatedAtUtc);
        Assert.Equal(_clock.Now.UtcDateTime, row.NextAttemptAtUtc);
        Assert.Equal(OutgoingEmail.MaxSubjectLength, row.Subject.Length);
        Assert.Null(row.SentAtUtc);
    }

    // TEST: Auction links use the public address.
    [Fact]
    public void Auction_links_use_the_public_address()
    {
        var id = Guid.NewGuid();

        Assert.Equal($"https://maroos.test/auctions/{id}", _outbox.AuctionUrl(id));
        Assert.Equal("https://maroos.test/Account/Manage", _outbox.SettingsUrl);
    }

    // TEST: Due emails are sent and their bodies cleared.
    [Fact]
    public async Task Due_emails_are_sent_and_their_bodies_cleared()
    {
        await _outbox.QueueAsync(Message("player@example.com"));

        Assert.Equal(1, await _dispatcher.SendDueAsync());

        var sent = Assert.Single(_transport.Sent);
        Assert.Equal("player@example.com", sent.To);
        Assert.Equal("text body", sent.TextBody);
        var row = Row("player@example.com");
        Assert.Equal(_clock.Now.UtcDateTime, row.SentAtUtc);
        Assert.Null(row.TextBody);
        Assert.Null(row.HtmlBody);
        Assert.Equal(0, await _dispatcher.SendDueAsync()); // nothing sent twice
    }

    // TEST: A failing email is retried with backoff then given up.
    [Fact]
    public async Task A_failing_email_is_retried_with_backoff_then_given_up()
    {
        _transport.FailuresToSimulate = int.MaxValue;
        await _outbox.QueueAsync(Message("fails@example.com"));

        await _dispatcher.SendDueAsync(); // attempt 1 fails
        int[] delays = [1, 5, 15, 60, 180];
        for (var i = 0; i < delays.Length; i++)
        {
            _clock.Advance(TimeSpan.FromMinutes(delays[i]) - TimeSpan.FromSeconds(1));
            await _dispatcher.SendDueAsync(); // not due yet
            Assert.Equal(i + 1, Row("fails@example.com").Attempts);

            _clock.Advance(TimeSpan.FromSeconds(1));
            await _dispatcher.SendDueAsync(); // due: the next attempt fails
        }

        var row = Row("fails@example.com");
        Assert.Equal(EmailRetry.MaxAttempts, row.Attempts);
        Assert.Null(row.SentAtUtc);
        Assert.Equal("SMTP server unavailable", row.LastError);

        _clock.Advance(TimeSpan.FromDays(1));
        await _dispatcher.SendDueAsync();
        Assert.Equal(EmailRetry.MaxAttempts, Row("fails@example.com").Attempts); // given up
        Assert.Empty(_transport.Sent);
    }

    // TEST: One failing email does not hold up the others.
    [Fact]
    public async Task One_failing_email_does_not_hold_up_the_others()
    {
        _transport.FailuresToSimulate = 1;
        await _outbox.QueueAsync(Message("first@example.com"));
        _clock.Advance(TimeSpan.FromSeconds(1));
        await _outbox.QueueAsync(Message("second@example.com"));

        await _dispatcher.SendDueAsync();

        Assert.Equal(["second@example.com"], _transport.Sent.Select(e => e.To));
        Assert.Equal(1, Row("first@example.com").Attempts);
    }

    // TEST: Sent emails are purged after seven days but unsent ones stay.
    [Fact]
    public async Task Sent_emails_are_purged_after_seven_days_but_unsent_ones_stay()
    {
        await _outbox.QueueAsync(Message("sent@example.com"));
        await _dispatcher.SendDueAsync();
        _transport.FailuresToSimulate = int.MaxValue;
        await _outbox.QueueAsync(Message("stuck@example.com"));

        _clock.Advance(TimeSpan.FromDays(7) - TimeSpan.FromMinutes(1));
        Assert.Equal(0, await _dispatcher.PurgeSentAsync());

        _clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(1, await _dispatcher.PurgeSentAsync());

        await using var db = _db.CreateDbContext();
        Assert.Equal(["stuck@example.com"], await db.OutgoingEmails.Select(e => e.ToAddress).ToListAsync());
    }
}
