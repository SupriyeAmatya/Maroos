// =====================================================================================================
// FILE: Maroos.Core/Email/EmailRetry.cs
// PURPOSE: The retry schedule for emails the mail server refuses or can't be reached for.
// Attempt 1 is immediate; if it fails, attempt 2 is 1 minute later, then 5, 15, 60 and 180 minutes.
// After the 6th failed attempt the dispatcher gives up (about 4.5 hours after the email was queued).
// =====================================================================================================

namespace Maroos.Core.Email;

/// <summary>How long to wait after a failed send before trying again, and when to give up.</summary>
public static class EmailRetry
{
    // MaxAttempts: total tries, including the first.
    public const int MaxAttempts = 6;

    // Delays: the wait after failed attempt 1, 2, 3, 4 and 5.
    private static readonly TimeSpan[] Delays =
    [
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(15),
        TimeSpan.FromMinutes(60),
        TimeSpan.FromMinutes(180),
    ];

    /// <param name="failedAttempts">Failed attempts so far (1 after the first failure).</param>
    // DelayAfter: the wait after this many failures (values outside 1–5 are clamped into range).
    public static TimeSpan DelayAfter(int failedAttempts) => Delays[Math.Clamp(failedAttempts, 1, Delays.Length) - 1];
}
