using Maroos.Core.Auctions;
using Maroos.Core.Watchlist;
using Maroos.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Maroos.Web.Services;

public sealed record WatchedDeck(Guid Id, string Name, string Format, string OwnerName);

public sealed record WatchedAuction(Guid Id, string CardName, string SetName, long CurrentCents, DateTime EndsAtUtc, bool IsOpen, AuctionStatus Status);

public sealed record Watchlist(IReadOnlyList<WatchedDeck> Decks, IReadOnlyList<WatchedAuction> Auctions);

/// <summary>Saves decks and auctions to a user's watchlist. You can only watch what you can see.</summary>
public sealed class WatchService(IDbContextFactory<ApplicationDbContext> dbFactory, TimeProvider clock)
{
    public async Task<bool> IsWatchingAsync(string userId, WatchTarget type, Guid targetId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Watches.AnyAsync(w => w.UserId == userId && w.TargetType == type && w.TargetId == targetId, ct);
    }

    /// <summary>Adds or removes the target; returns whether it's watched afterwards.</summary>
    public async Task<bool> ToggleAsync(string userId, WatchTarget type, Guid targetId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(userId))
            throw new UnauthorizedAccessException("Log in to use your watchlist.");

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var existing = await db.Watches.FirstOrDefaultAsync(w => w.UserId == userId && w.TargetType == type && w.TargetId == targetId, ct);
        if (existing is not null)
        {
            db.Watches.Remove(existing);
            await db.SaveChangesAsync(ct);
            return false;
        }

        var visible = type switch
        {
            WatchTarget.Deck => await db.Decks.AnyAsync(d => d.Id == targetId && (d.IsPublic || d.OwnerId == userId), ct),
            WatchTarget.Auction => await db.Auctions.AnyAsync(a => a.Id == targetId, ct),
            _ => false,
        };
        if (!visible)
            throw new KeyNotFoundException("That deck or auction doesn't exist.");

        db.Watches.Add(new Watch { UserId = userId, TargetType = type, TargetId = targetId, CreatedAtUtc = clock.GetUtcNow().UtcDateTime });
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Watched decks the user can still see, and watched auctions, most recently watched first.</summary>
    public async Task<Watchlist> ListAsync(string userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = clock.GetUtcNow().UtcDateTime;

        var decks = await db.Watches
            .Where(w => w.UserId == userId && w.TargetType == WatchTarget.Deck)
            .OrderByDescending(w => w.CreatedAtUtc)
            .Join(db.Decks, w => w.TargetId, d => d.Id, (w, d) => d)
            .Where(d => d.IsPublic || d.OwnerId == userId)
            .Join(db.Users, d => d.OwnerId, u => u.Id, (d, u) => new { d.Id, d.Name, d.Format, u.DisplayName })
            .ToListAsync(ct);

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

        return new Watchlist(
            decks.Select(d => new WatchedDeck(d.Id, d.Name, d.Format, ApplicationUser.PublicName(d.DisplayName))).ToList(),
            auctions.Select(a => new WatchedAuction(a.Auction.Id, a.Auction.CardName, a.Auction.SetName, a.Current, a.Auction.EndsAtUtc,
                a.Auction.IsOpenAt(now), a.Auction.Status)).ToList());
    }

    /// <summary>Removes every watch on a target (e.g. when a deck is deleted).</summary>
    public static async Task RemoveAllAsync(ApplicationDbContext db, WatchTarget type, Guid targetId, CancellationToken ct = default) =>
        await db.Watches.Where(w => w.TargetType == type && w.TargetId == targetId).ExecuteDeleteAsync(ct);
}
