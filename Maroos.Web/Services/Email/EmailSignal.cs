// =====================================================================================================
// FILE: Maroos.Web/Services/Email/EmailSignal.cs
// PURPOSE: A doorbell between the outbox and the dispatcher. Queueing an email rings it, so the dispatcher
// sends within a moment instead of waiting up to 5 seconds for its next check. One shared instance.
// =====================================================================================================

using System.Threading.Channels;

namespace Maroos.Web.Services.Email;

/// <summary>Wakes the dispatcher as soon as an email is queued, instead of waiting for its next poll.</summary>
public sealed class EmailSignal
{
    // Holds at most one pending nudge: any number of queued emails wake the dispatcher once.
    private readonly Channel<bool> _nudges = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    // Nudge: ring the bell. TryWrite does nothing if a nudge is already waiting.
    public void Nudge() => _nudges.Writer.TryWrite(true);

    /// <summary>Returns when nudged or after <paramref name="timeout"/>, whichever comes first.</summary>
    public async Task WaitAsync(TimeSpan timeout, TimeProvider clock, CancellationToken ct)
    {
        // Race a nudge against the timeout; whichever finishes first ends the wait.
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var nudged = _nudges.Reader.ReadAsync(stop.Token).AsTask();
        var timedOut = Task.Delay(timeout, clock, stop.Token);
        await Task.WhenAny(nudged, timedOut);
        // Cancel the one that didn't finish.
        await stop.CancelAsync();
        // Observe the loser so its cancellation isn't reported as unobserved.
        await Task.WhenAll(nudged, timedOut).ContinueWith(_ => { }, TaskScheduler.Default);
    }
}
