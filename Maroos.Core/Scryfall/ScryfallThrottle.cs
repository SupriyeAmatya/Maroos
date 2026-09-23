using System.Diagnostics;

namespace Maroos.Core.Scryfall;

/// <summary>
/// Keeps Scryfall requests at least <see cref="MinInterval"/> apart across the whole app.
/// Register as a singleton: typed HttpClients are transient, so the gate must outlive them.
/// </summary>
public sealed class ScryfallThrottle(TimeSpan minInterval)
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMilliseconds(100);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Stopwatch _sinceLast = new();

    public ScryfallThrottle() : this(DefaultInterval) { }

    public TimeSpan MinInterval { get; } = minInterval;

    public async Task<T> RunAsync<T>(Func<Task<T>> request, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_sinceLast.IsRunning && _sinceLast.Elapsed < MinInterval)
                await Task.Delay(MinInterval - _sinceLast.Elapsed, cancellationToken);

            return await request();
        }
        finally
        {
            _sinceLast.Restart();
            _gate.Release();
        }
    }
}
