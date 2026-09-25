// =====================================================================================================
// FILE: Maroos.Core/Scryfall/ScryfallThrottle.cs
// PURPOSE: Makes sure Maroos never calls Scryfall more than about 10 times per second.
//
// WHY: Scryfall is free, and asks every app to wait 50–100 ms between requests. Apps that hammer it
// get blocked. Every Scryfall call in Maroos goes through RunAsync below, which waits until at
// least 100 ms have passed since the previous call before letting the next one go.
//
// HOW (two tools):
//   * SemaphoreSlim(1, 1) is a "one person at a time" gate: only one request can be inside at once.
//   * Stopwatch measures time since the last request finished.
//
// It must be registered as a SINGLETON (one instance for the whole app) in Program.cs; otherwise
// each HttpClient would get its own throttle and the limit wouldn't apply app-wide.
// =====================================================================================================

// Stopwatch lives in System.Diagnostics.
using System.Diagnostics;

namespace Maroos.Core.Scryfall;

/// <summary>
/// Keeps Scryfall requests at least <see cref="MinInterval"/> apart across the whole app.
/// Register as a singleton: typed HttpClients are transient, so the gate must outlive them.
/// </summary>
// `ScryfallThrottle(TimeSpan minInterval)` is a "primary constructor": minInterval is passed in when
// the object is created (tests pass TimeSpan.Zero so they run fast).
public sealed class ScryfallThrottle(TimeSpan minInterval)
{
    // DefaultInterval: the gap used in the real app — 100 milliseconds.
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMilliseconds(100);

    // _gate: allows 1 request in at a time (initial count 1, maximum 1).
    private readonly SemaphoreSlim _gate = new(1, 1);
    // _sinceLast: measures how long ago the previous request finished. Not running until the first request.
    private readonly Stopwatch _sinceLast = new();

    // Parameterless constructor used by dependency injection: uses the 100 ms default.
    public ScryfallThrottle() : this(DefaultInterval) { }

    // MinInterval: the minimum gap between two requests (read-only after creation).
    public TimeSpan MinInterval { get; } = minInterval;

    // RunAsync: runs one Scryfall request, but only when it's polite to do so.
    //   request            — a function that performs the HTTP call and returns its result (type T).
    //   cancellationToken  — lets the caller give up waiting (e.g. the user left the page).
    //   returns whatever the request returns.
    // PROCESS:
    //   1. wait for the gate (if another request is running, queue behind it);
    //   2. if the previous request ended less than MinInterval ago, sleep for the remaining time;
    //   3. run the request;
    //   4. always (even on error) restart the stopwatch and open the gate for the next caller.
    public async Task<T> RunAsync<T>(Func<Task<T>> request, CancellationToken cancellationToken = default)
    {
        // Step 1: take our turn.
        await _gate.WaitAsync(cancellationToken);
        try
        {
            // Step 2: too soon after the last call? Wait out the difference.
            if (_sinceLast.IsRunning && _sinceLast.Elapsed < MinInterval)
                await Task.Delay(MinInterval - _sinceLast.Elapsed, cancellationToken);

            // Step 3: make the actual HTTP request.
            return await request();
        }
        finally
        {
            // Step 4: `finally` runs whether the request succeeded or threw an exception.
            _sinceLast.Restart();
            _gate.Release();
        }
    }
}
