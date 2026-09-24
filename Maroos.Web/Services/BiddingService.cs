using System.Data;
using Maroos.Core.Auctions;
using Maroos.Core.Email;
using Maroos.Web.Data;
using Maroos.Web.Services.Email;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Maroos.Web.Services;

public sealed record BidResult(bool Accepted, string? Error, long? AmountCents = null)
{
    public static BidResult Rejected(string error) => new(false, error);
}

public sealed record BidLine(string BidderName, long AmountCents, DateTime PlacedAtUtc, bool IsViewer);

public enum MyBidState
{
    Winning,
    Outbid,
    Won,
    Lost,
    Cancelled,
}

public sealed record MyBid(Guid AuctionId, string CardName, string SetName, long MyHighCents, long CurrentCents, DateTime EndsAtUtc, MyBidState State);

/// <summary>
/// Places bids. Each bid runs in a serializable transaction that re-reads the auction and its high bid,
/// so two bids at the same moment can't both win: the loser is retried against the new high bid.
/// </summary>
public sealed class BiddingService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    AuctionEvents events,
    EmailOutbox emails,
    TimeProvider clock,
    ILogger<BiddingService> logger)
{
    private const int MaxAttempts = 5;

    public async Task<BidResult> PlaceBidAsync(Guid auctionId, string bidderId, long amountCents, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(bidderId))
            return BidResult.Rejected("Log in to bid.");

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await TryPlaceBidAsync(auctionId, bidderId, amountCents, ct);
            }
            catch (Exception ex) when (attempt < MaxAttempts && IsSerializationConflict(ex))
            {
                logger.LogInformation("Bid on {AuctionId} collided with another bid; retrying ({Attempt})", auctionId, attempt);
                // A short, random pause so colliding bids don't retry in lockstep.
                await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(10, 40) * attempt), ct);
            }
        }
    }

    private async Task<BidResult> TryPlaceBidAsync(Guid auctionId, string bidderId, long amountCents, CancellationToken ct)
    {
        BidPlaced placed;
        Auction auction;
        string? previousHighBidderId;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        await using (var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct))
        {
            var found = await db.Auctions.FirstOrDefaultAsync(a => a.Id == auctionId, ct);
            if (found is null)
                return BidResult.Rejected("This auction doesn't exist.");
            auction = found;

            var high = await db.Bids
                .Where(b => b.AuctionId == auctionId)
                .OrderByDescending(b => b.AmountCents).ThenBy(b => b.CreatedAtUtc)
                .Select(b => new { b.AmountCents, b.BidderId })
                .FirstOrDefaultAsync(ct);
            previousHighBidderId = high?.BidderId;

            var now = clock.GetUtcNow().UtcDateTime;
            var error = BidRules.Validate(auction, bidderId, amountCents, high?.AmountCents, high?.BidderId, now);
            if (error is not null)
                return BidResult.Rejected(error);

            db.Bids.Add(new Bid { AuctionId = auctionId, BidderId = bidderId, AmountCents = amountCents, CreatedAtUtc = now });
            await db.SaveChangesAsync(ct);

            var bidCount = await db.Bids.CountAsync(b => b.AuctionId == auctionId, ct);
            var bidderName = await db.Users.Where(u => u.Id == bidderId).Select(u => u.DisplayName).FirstOrDefaultAsync(ct);
            await transaction.CommitAsync(ct);

            placed = new BidPlaced(auctionId, amountCents, ApplicationUser.PublicName(bidderName), bidCount, now);
        }

        // Only tell anyone after the bid is committed.
        await events.PublishAsync(placed);
        if (previousHighBidderId is not null)
            await QueueOutbidEmailAsync(auction, previousHighBidderId, bidderId, amountCents, placed.PlacedAtUtc, ct);
        return new BidResult(true, null, amountCents);
    }

    /// <summary>Emails the bidder who just lost the lead. Runs after commit: a failure is logged and never undoes the bid.</summary>
    private async Task QueueOutbidEmailAsync(Auction auction, string previousHighBidderId, string bidderId, long amountCents, DateTime nowUtc, CancellationToken ct)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var previous = await db.Users.Where(u => u.Id == previousHighBidderId)
                .Select(u => new { u.Email, u.AuctionEmails })
                .FirstOrDefaultAsync(ct);
            if (previous?.Email is null || !OutbidRule.ShouldNotify(previousHighBidderId, bidderId, previous.AuctionEmails))
                return;

            var content = AuctionEmailContent.Outbid(emails.InfoFor(auction), amountCents,
                BidRules.MinimumNextBid(auction, amountCents), AuctionRules.TimeLeft(nowUtc, auction.EndsAtUtc));
            await emails.QueueAsync(new EmailMessage(EmailKind.Outbid, previous.Email, content, previousHighBidderId, auction.Id), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Couldn't queue the outbid email for auction {AuctionId}", auction.Id);
        }
    }

    /// <summary>Bid history, highest first, with public bidder names.</summary>
    public async Task<IReadOnlyList<BidLine>> HistoryAsync(Guid auctionId, string? viewerId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var bids = await db.Bids
            .Where(b => b.AuctionId == auctionId)
            .OrderByDescending(b => b.AmountCents).ThenBy(b => b.CreatedAtUtc)
            .Join(db.Users, b => b.BidderId, u => u.Id, (b, u) => new { b.BidderId, u.DisplayName, b.AmountCents, b.CreatedAtUtc })
            .ToListAsync(ct);

        return bids.Select(b => new BidLine(ApplicationUser.PublicName(b.DisplayName), b.AmountCents, b.CreatedAtUtc, b.BidderId == viewerId)).ToList();
    }

    /// <summary>Every auction the user has bid on, with where they stand.</summary>
    public async Task<IReadOnlyList<MyBid>> ListForBidderAsync(string bidderId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = clock.GetUtcNow().UtcDateTime;

        var rows = await db.Auctions
            .Where(a => db.Bids.Any(b => b.AuctionId == a.Id && b.BidderId == bidderId))
            .Select(a => new
            {
                Auction = a,
                MyHigh = db.Bids.Where(b => b.AuctionId == a.Id && b.BidderId == bidderId).Max(b => b.AmountCents),
                TopBidder = db.Bids.Where(b => b.AuctionId == a.Id)
                    .OrderByDescending(b => b.AmountCents).ThenBy(b => b.CreatedAtUtc)
                    .Select(b => b.BidderId).FirstOrDefault(),
                Current = db.Bids.Where(b => b.AuctionId == a.Id).Max(b => b.AmountCents),
            })
            .AsNoTracking()
            .ToListAsync(ct);

        return rows
            .Select(r => new MyBid(
                r.Auction.Id, r.Auction.CardName, r.Auction.SetName, r.MyHigh, r.Current, r.Auction.EndsAtUtc,
                r.Auction.Status == AuctionStatus.Cancelled ? MyBidState.Cancelled
                : r.Auction.IsOpenAt(now) ? (r.TopBidder == bidderId ? MyBidState.Winning : MyBidState.Outbid)
                : r.TopBidder == bidderId ? MyBidState.Won : MyBidState.Lost))
            .OrderBy(b => b.State is MyBidState.Winning or MyBidState.Outbid ? 0 : 1)
            .ThenBy(b => b.EndsAtUtc)
            .ToList();
    }

    /// <summary>
    /// A write that lost a race with another transaction: PostgreSQL serialization failure / deadlock,
    /// or SQLite reporting the database busy or locked.
    /// </summary>
    public static bool IsSerializationConflict(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is PostgresException { SqlState: PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected })
                return true;
            if (e is SqliteException { SqliteErrorCode: 5 or 6 }) // SQLITE_BUSY, SQLITE_LOCKED
                return true;
        }
        return false;
    }
}
