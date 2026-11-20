namespace Maroos.Web.Services;

/// <summary>Every minute, marks auctions past their end time as Ended and records the winner.</summary>
public sealed class AuctionCloser(IServiceScopeFactory scopes, TimeProvider clock, ILogger<AuctionCloser> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, clock);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var closed = await scope.ServiceProvider.GetRequiredService<AuctionService>().CloseExpiredAsync(stoppingToken);
                if (closed > 0)
                    logger.LogInformation("Closed {Count} ended auction(s)", closed);
            }
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
