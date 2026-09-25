// =====================================================================================================
// FILE: Maroos.Web/Services/AuctionEvents.cs
// PURPOSE: Live auction updates ("someone just bid", "the auction ended").
//
// HOW IT WORKS
//   BiddingService / AuctionService  --PublishAsync-->  AuctionEvents (one shared instance)
//        |-- sends to the SignalR group "auction-{id}"   -> any external client connected to /hubs/auctions
//        '-- calls every in-process subscriber          -> each open AuctionPage (Blazor) for that auction
//
// Blazor Server pages already run on the server, so they subscribe directly (in-process) instead of
// opening a second SignalR connection. The hub exists for other clients (e.g. a future mobile app).
// Because events live in this process's memory, Maroos runs as a single instance (see DEPLOY.md).
// =====================================================================================================

using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;

namespace Maroos.Web.Services;

// AuctionEvent: the base type of every event; each event says which auction it's about.
public abstract record AuctionEvent(Guid AuctionId);

// BidPlaced: a bid was accepted. BidderName is the public display name (never an email); BidCount lets pages
// update "N bids" without reloading.
public sealed record BidPlaced(Guid AuctionId, long AmountCents, string BidderName, int BidCount, DateTime PlacedAtUtc) : AuctionEvent(AuctionId);

// AuctionEnded: the closer marked the auction Ended. Winner fields are null when nobody bid.
public sealed record AuctionEnded(Guid AuctionId, string? WinnerName, long? WinningCents) : AuctionEvent(AuctionId);

/// <summary>
/// SignalR hub for live auction updates. Clients call <see cref="JoinAuction"/> and then receive
/// <c>BidPlaced(amountCents, bidderName, bidCount)</c> and <c>AuctionEnded(winnerName, winningCents)</c>.
/// Everything sent here is already public on the auction page.
/// </summary>
public sealed class AuctionHub : Hub
{
    // The URL the hub is mapped at (Program.cs: app.MapHub<AuctionHub>(AuctionHub.Path)).
    public const string Path = "/hubs/auctions";

    // Group: one SignalR group per auction, so an update only reaches people watching that auction.
    public static string Group(Guid auctionId) => $"auction-{auctionId}";

    // JoinAuction / LeaveAuction: methods clients call over the connection to start/stop receiving an auction's updates.
    public Task JoinAuction(Guid auctionId) => Groups.AddToGroupAsync(Context.ConnectionId, Group(auctionId));

    public Task LeaveAuction(Guid auctionId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, Group(auctionId));
}

/// <summary>
/// Publishes auction events to the auction's SignalR group and to Blazor components on this server.
/// Interactive components already run on the server over their own SignalR circuit, so they subscribe
/// in-process instead of opening a second hub connection.
/// </summary>
// hub is optional (null in unit tests that don't run SignalR).
public sealed class AuctionEvents(ILogger<AuctionEvents> logger, IHubContext<AuctionHub>? hub = null)
{
    // _handlers: auction id -> the list of subscriber callbacks. ConcurrentDictionary because many Blazor
    // circuits (threads) subscribe and publish at the same time. Each list is immutable: changes replace it,
    // so a publish that is looping over the old list is never disturbed.
    private readonly ConcurrentDictionary<Guid, ImmutableHandlers> _handlers = new();

    /// <summary>Calls <paramref name="handler"/> for every event on one auction until the returned subscription is disposed.</summary>
    // Subscribe: AuctionPage calls this when it opens and disposes the result when the user leaves the page.
    public IDisposable Subscribe(Guid auctionId, Func<AuctionEvent, Task> handler)
    {
        // Add the handler (creating the list if this is the auction's first subscriber).
        _handlers.AddOrUpdate(auctionId, _ => new([handler]), (_, list) => list.Add(handler));
        // Disposing the subscription removes the handler again.
        return new Subscription(() => _handlers.AddOrUpdate(auctionId, _ => new([]), (_, list) => list.Remove(handler)));
    }

    // PublishAsync: send one event to the hub group and then to every in-process subscriber.
    // Called only AFTER the database change is committed, so nobody hears about a bid that didn't save.
    public async Task PublishAsync(AuctionEvent auctionEvent)
    {
        try
        {
            // Step 1: SignalR clients in the auction's group.
            var group = hub?.Clients.Group(AuctionHub.Group(auctionEvent.AuctionId));
            if (group is not null)
                // Pick the client-side message name and arguments by event type ("switch expression").
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

        // Step 2: in-process subscribers (open AuctionPage components). Nobody watching? Done.
        if (!_handlers.TryGetValue(auctionEvent.AuctionId, out var handlers))
            return;
        foreach (var handler in handlers.Items)
        {
            try
            {
                await handler(auctionEvent);
            }
            // One broken page (e.g. a closed browser tab) must not stop the others from updating.
            catch (Exception ex)
            {
                logger.LogWarning(ex, "An auction page failed to handle {Event}", auctionEvent.GetType().Name);
            }
        }
    }

    // ImmutableHandlers: a read-only list of callbacks; Add/Remove return a NEW list instead of changing this one.
    private sealed record ImmutableHandlers(IReadOnlyList<Func<AuctionEvent, Task>> Items)
    {
        public ImmutableHandlers Add(Func<AuctionEvent, Task> handler) => new([.. Items, handler]);
        public ImmutableHandlers Remove(Func<AuctionEvent, Task> handler) => new(Items.Where(h => h != handler).ToList());
    }

    // Subscription: runs its `dispose` action exactly once, even if Dispose is called twice or from two threads.
    private sealed class Subscription(Action dispose) : IDisposable
    {
        // 0 = still active, 1 = disposed.
        private int _disposed;

        public void Dispose()
        {
            // Interlocked.Exchange sets the flag atomically and returns the old value.
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                dispose();
        }
    }
}
