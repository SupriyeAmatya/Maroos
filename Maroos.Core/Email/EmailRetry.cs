namespace Maroos.Core.Email;

/// <summary>How long to wait after a failed send before trying again, and when to give up.</summary>
public static class EmailRetry
{
    public const int MaxAttempts = 6;

    private static readonly TimeSpan[] Delays =
    [
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(15),
        TimeSpan.FromMinutes(60),
        TimeSpan.FromMinutes(180),
    ];

    /// <param name="failedAttempts">Failed attempts so far (1 after the first failure).</param>
    public static TimeSpan DelayAfter(int failedAttempts) => Delays[Math.Clamp(failedAttempts, 1, Delays.Length) - 1];
}
