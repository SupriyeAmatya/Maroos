// =====================================================================================================
// FILE: Maroos.Web/Services/AuctionCloser.cs
// PURPOSE: A background job that runs for the app's whole life. Once a minute it asks AuctionService to
// close every auction whose end time has passed: set Status = Ended, record the winner (the high bidder),
// and publish an AuctionEnded event.
//
// NOTE: pages don't wait for this job. Auction.IsOpenAt(now) already treats an expired auction as closed,
// and AuctionPage shows the result the moment its countdown reaches zero. The closer just makes the
// database agree (for the profile tabs, the winner's contact details, etc.).
// Registered in Program.cs with AddHostedService<AuctionCloser>(); test web hosts remove it.
// =====================================================================================================

namespace Maroos.Web.Services;

/// <summary>Every minute, marks auctions past their end time as Ended and records the winner.</summary>
// BackgroundService: ASP.NET starts ExecuteAsync at startup and cancels `stoppingToken` at shutdown.
// Constructor parameters (a "primary constructor"):
//   scopes — creates a service scope per run (AuctionService is scoped, but this class is a singleton)
//   clock  — the app's TimeProvider (a fake one in tests)
//   logger — writes to the app log
public sealed class AuctionCloser(IServiceScopeFactory scopes, TimeProvider clock, ILogger<AuctionCloser> logger) : BackgroundService
{
    // How often to check for ended auctions.
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // PeriodicTimer: "tick" every Interval. `using` disposes it when the method ends.
        using var timer = new PeriodicTimer(Interval, clock);
        // do/while: run once immediately at startup, then once per tick until the app stops.
        do
        {
            try
            {
                // A fresh scope (and so a fresh database context) for each run.
                await using var scope = scopes.CreateAsyncScope();
                var closed = await scope.ServiceProvider.GetRequiredService<AuctionService>().CloseExpiredAsync(stoppingToken);
                if (closed > 0)
                    logger.LogInformation("Closed {Count} ended auction(s)", closed);
            }
            // The app is shutting down: stop quietly.
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Keep running: the next tick retries whatever didn't close.
                logger.LogError(ex, "Closing ended auctions failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
