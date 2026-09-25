// =====================================================================================================
// FILE: Maroos.Web/Services/WatchService.cs
// PURPOSE: The watchlist (the ♡ Watch button on decks and auctions, and profile → Watchlist tab).
//
// RULES
//   * You can only watch what you can see: public decks, your own decks, any auction.
//   * If a watched deck is later made private, it silently disappears from your watchlist
//     (the watch row stays, so it comes back if the deck goes public again).
//   * Deleting a deck removes every watch on it (RemoveAllAsync, called by DeckService).
// =====================================================================================================

using Maroos.Core.Auctions;
using Maroos.Core.Watchlist;
using Maroos.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Maroos.Web.Services;

// WatchedDeck / WatchedAuction: one row of the watchlist, with just what the profile tab shows.
// OwnerName is the deck owner's public display name.
public sealed record WatchedDeck(Guid Id, string Name, string Format, string OwnerName);

// CurrentCents = the high bid, or the start price if nobody has bid. IsOpen uses Auction.IsOpenAt(now).
public sealed record WatchedAuction(Guid Id, string CardName, string SetName, long CurrentCents, DateTime EndsAtUtc, bool IsOpen, AuctionStatus Status);

// Watchlist: both lists together.
public sealed record Watchlist(IReadOnlyList<WatchedDeck> Decks, IReadOnlyList<WatchedAuction> Auctions);

/// <summary>Saves decks and auctions to a user's watchlist. You can only watch what you can see.</summary>
public sealed class WatchService(IDbContextFactory<ApplicationDbContext> dbFactory, TimeProvider clock)
{
    // IsWatchingAsync: should the button show "♥ Watching" or "♡ Watch"?
    public async Task<bool> IsWatchingAsync(string userId, WatchTarget type, Guid targetId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Watches.AnyAsync(w => w.UserId == userId && w.TargetType == type && w.TargetId == targetId, ct);
    }

    /// <summary>Adds or removes the target; returns whether it's watched afterwards.</summary>
    public async Task<bool> ToggleAsync(string userId, WatchTarget type, Guid targetId, CancellationToken ct = default)
    {
        // Must be signed in.
        if (string.IsNullOrEmpty(userId))
            throw new UnauthorizedAccessException("Log in to use your watchlist.");

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        // Already watching? Then this click un-watches.
        var existing = await db.Watches.FirstOrDefaultAsync(w => w.UserId == userId && w.TargetType == type && w.TargetId == targetId, ct);
        if (existing is not null)
        {
            db.Watches.Remove(existing);
            await db.SaveChangesAsync(ct);
            return false;
        }

        // Not watching yet: check the user is allowed to see the target.
        // A private deck of someone else counts as "doesn't exist" (never reveal that it's there).
        var visible = type switch
        {
            WatchTarget.Deck => await db.Decks.AnyAsync(d => d.Id == targetId && (d.IsPublic || d.OwnerId == userId), ct),
            WatchTarget.Auction => await db.Auctions.AnyAsync(a => a.Id == targetId, ct),
            _ => false,
        };
        if (!visible)
            throw new KeyNotFoundException("That deck or auction doesn't exist.");

        // Add the watch.
        db.Watches.Add(new Watch { UserId = userId, TargetType = type, TargetId = targetId, CreatedAtUtc = clock.GetUtcNow().UtcDateTime });
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Watched decks the user can still see, and watched auctions, most recently watched first.</summary>
    // ListAsync: two queries in total (one for decks, one for auctions), whatever the list size.
    public async Task<Watchlist> ListAsync(string userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = clock.GetUtcNow().UtcDateTime;

        // Decks: watches -> decks (still visible to this user) -> owners' display names.
        var decks = await db.Watches
            .Where(w => w.UserId == userId && w.TargetType == WatchTarget.Deck)
            .OrderByDescending(w => w.CreatedAtUtc)
            .Join(db.Decks, w => w.TargetId, d => d.Id, (w, d) => d)
            .Where(d => d.IsPublic || d.OwnerId == userId)
            .Join(db.Users, d => d.OwnerId, u => u.Id, (d, u) => new { d.Id, d.Name, d.Format, u.DisplayName })
            .ToListAsync(ct);

        // Auctions: watches -> auctions, with the current price computed in SQL.
        // `(long?)` makes Max return null when there are no bids, so `??` can fall back to the start price.
        var auctions = await db.Watches
            .Where(w => w.UserId == userId && w.TargetType == WatchTarget.Auction)
            .OrderByDescending(w => w.CreatedAtUtc)
            .Join(db.Auctions, w => w.TargetId, a => a.Id, (w, a) => new
            {
                Auction = a,
                Current = db.Bids.Where(b => b.AuctionId == a.Id).Max(b => (long?)b.AmountCents) ?? a.StartCents,
            })
            .AsNoTracking()
            .ToListAsync(ct);

        // Shape the rows into the public records (display names via PublicName).
        return new Watchlist(
            decks.Select(d => new WatchedDeck(d.Id, d.Name, d.Format, ApplicationUser.PublicName(d.DisplayName))).ToList(),
            auctions.Select(a => new WatchedAuction(a.Auction.Id, a.Auction.CardName, a.Auction.SetName, a.Current, a.Auction.EndsAtUtc,
                a.Auction.IsOpenAt(now), a.Auction.Status)).ToList());
    }

    /// <summary>Removes every watch on a target (e.g. when a deck is deleted).</summary>
    // Takes the caller's DbContext so it runs inside the caller's operation. ExecuteDeleteAsync is one
    // DELETE statement in SQL (no loading rows first).
    public static async Task RemoveAllAsync(ApplicationDbContext db, WatchTarget type, Guid targetId, CancellationToken ct = default) =>
        await db.Watches.Where(w => w.TargetType == type && w.TargetId == targetId).ExecuteDeleteAsync(ct);
}
