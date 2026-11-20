using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;

namespace Maroos.Web.Services;

public abstract record AuctionEvent(Guid AuctionId);

public sealed record BidPlaced(Guid AuctionId, long AmountCents, string BidderName, int BidCount, DateTime PlacedAtUtc) : AuctionEvent(AuctionId);

public sealed record AuctionEnded(Guid AuctionId, string? WinnerName, long? WinningCents) : AuctionEvent(AuctionId);

/// <summary>
/// SignalR hub for live auction updates. Clients call <see cref="JoinAuction"/> and then receive
/// <c>BidPlaced(amountCents, bidderName, bidCount)</c> and <c>AuctionEnded(winnerName, winningCents)</c>.
/// Everything sent here is already public on the auction page.
/// </summary>
public sealed class AuctionHub : Hub
{
    public const string Path = "/hubs/auctions";

    public static string Group(Guid auctionId) => $"auction-{auctionId}";

    public Task JoinAuction(Guid auctionId) => Groups.AddToGroupAsync(Context.ConnectionId, Group(auctionId));

    public Task LeaveAuction(Guid auctionId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, Group(auctionId));
}

/// <summary>
/// Publishes auction events to the auction's SignalR group and to Blazor components on this server.
/// Interactive components already run on the server over their own SignalR circuit, so they subscribe
/// in-process instead of opening a second hub connection.
/// </summary>
public sealed class AuctionEvents(ILogger<AuctionEvents> logger, IHubContext<AuctionHub>? hub = null)
{
    private readonly ConcurrentDictionary<Guid, ImmutableHandlers> _handlers = new();

    /// <summary>Calls <paramref name="handler"/> for every event on one auction until the returned subscription is disposed.</summary>
    public IDisposable Subscribe(Guid auctionId, Func<AuctionEvent, Task> handler)
    {
        _handlers.AddOrUpdate(auctionId, _ => new([handler]), (_, list) => list.Add(handler));
        return new Subscription(() => _handlers.AddOrUpdate(auctionId, _ => new([]), (_, list) => list.Remove(handler)));
    }

    public async Task PublishAsync(AuctionEvent auctionEvent)
    {
        try
        {
            var group = hub?.Clients.Group(AuctionHub.Group(auctionEvent.AuctionId));
            if (group is not null)
                await (auctionEvent switch
                {
                    BidPlaced bid => group.SendAsync("BidPlaced", bid.AmountCents, bid.BidderName, bid.BidCount),
                    AuctionEnded ended => group.SendAsync("AuctionEnded", ended.WinnerName, ended.WinningCents),
                    _ => Task.CompletedTask,
                });
        }
        catch (Exception ex)
        {
            // A notification failure must never undo a saved bid.
            logger.LogWarning(ex, "Couldn't broadcast {Event} for auction {AuctionId}", auctionEvent.GetType().Name, auctionEvent.AuctionId);
        }

        if (!_handlers.TryGetValue(auctionEvent.AuctionId, out var handlers))
            return;
        foreach (var handler in handlers.Items)
        {
            try
            {
                await handler(auctionEvent);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "An auction page failed to handle {Event}", auctionEvent.GetType().Name);
            }
        }
    }

    private sealed record ImmutableHandlers(IReadOnlyList<Func<AuctionEvent, Task>> Items)
    {
        public ImmutableHandlers Add(Func<AuctionEvent, Task> handler) => new([.. Items, handler]);
        public ImmutableHandlers Remove(Func<AuctionEvent, Task> handler) => new(Items.Where(h => h != handler).ToList());
    }

    private sealed class Subscription(Action dispose) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                dispose();
        }
    }
}
