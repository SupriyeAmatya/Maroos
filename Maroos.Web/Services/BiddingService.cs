// =====================================================================================================
// FILE: Maroos.Web/Services/BiddingService.cs
// PURPOSE: Places bids safely, and lists bids (auction history, "my bids" profile tab).
//
// THE RACE PROBLEM: two people bid $10 on the same auction at the same instant. Without care, both
// requests read "high bid = $9", both pass the rules, and both get saved — two winners.
//
// THE FIX (PlaceBidAsync):
//   1. Open a SERIALIZABLE transaction — the database guarantees the result is as if the transactions
//      ran one after the other. If two overlap, one is aborted with a "serialization conflict".
//   2. Inside it: re-read the auction and the current high bid, check BidRules, insert the bid, commit.
//   3. If the database aborted us (PostgreSQL 40001/40P01, SQLite busy/locked 5/6), wait a short random
//      time and retry from step 1 — up to 5 attempts. The retry sees the other bid and applies the rules
//      against it (e.g. "Bids must be at least $11.00").
//   4. Only after COMMIT, publish BidPlaced so open auction pages update live.
// Tests prove this: removing the transaction makes the concurrency tests fail on SQLite and PostgreSQL.
// =====================================================================================================

using System.Data;
using Maroos.Core.Auctions;
using Maroos.Core.Email;
using Maroos.Web.Data;
using Maroos.Web.Services.Email;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Maroos.Web.Services;

// BidResult: the outcome shown to the bidder. Accepted = saved; otherwise Error says why not.
public sealed record BidResult(bool Accepted, string? Error, long? AmountCents = null)
{
    // Shortcut for a rejection.
    public static BidResult Rejected(string error) => new(false, error);
}

// BidLine: one row of an auction's bid history (IsViewer = "this is your bid", highlighted on the page).
public sealed record BidLine(string BidderName, long AmountCents, DateTime PlacedAtUtc, bool IsViewer);

// MyBidState: where a user stands on an auction they bid on (profile → Bids tab).
public enum MyBidState
{
    // Open, and you're the high bidder.
    Winning,
    // Open, and someone outbid you.
    Outbid,
    // Ended, you had the high bid.
    Won,
    // Ended, someone else won.
    Lost,
    // The seller cancelled it.
    Cancelled,
}

// MyBid: one row of the profile's Bids tab: your highest bid vs. the current price.
public sealed record MyBid(Guid AuctionId, string CardName, string SetName, long MyHighCents, long CurrentCents, DateTime EndsAtUtc, MyBidState State);

/// <summary>
/// Places bids. Each bid runs in a serializable transaction that re-reads the auction and its high bid,
/// so two bids at the same moment can't both win: the loser is retried against the new high bid.
/// </summary>
// Dependencies: dbFactory (a new DbContext per attempt), events (live updates), clock (time), logger.
public sealed class BiddingService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    AuctionEvents events,
    // emails: the email outbox (the outbid email).
    EmailOutbox emails,
    TimeProvider clock,
    ILogger<BiddingService> logger)
{
    // How many times to try a bid that keeps colliding before giving up (the exception then surfaces).
    private const int MaxAttempts = 5;

    // PlaceBidAsync: the public entry point. auctionId + bidderId (signed-in user) + amount in cents.
    public async Task<BidResult> PlaceBidAsync(Guid auctionId, string bidderId, long amountCents, CancellationToken ct = default)
    {
        // Anonymous visitors can't bid.
        if (string.IsNullOrEmpty(bidderId))
            return BidResult.Rejected("Log in to bid.");

        // Retry loop: `for (...; ; ...)` has no end condition — it exits by returning or by an exception.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await TryPlaceBidAsync(auctionId, bidderId, amountCents, ct);
            }
            // Exception filter (`when`): only catch collisions, and only while attempts remain.
            catch (Exception ex) when (attempt < MaxAttempts && IsSerializationConflict(ex))
            {
                logger.LogInformation("Bid on {AuctionId} collided with another bid; retrying ({Attempt})", auctionId, attempt);
                // A short, random pause so colliding bids don't retry in lockstep.
                // (10–40 ms, times the attempt number: waits grow on repeated collisions.)
                await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(10, 40) * attempt), ct);
            }
        }
    }

    // TryPlaceBidAsync: one attempt, all inside one serializable transaction.
    private async Task<BidResult> TryPlaceBidAsync(Guid auctionId, string bidderId, long amountCents, CancellationToken ct)
    {
        // The event to publish after commit (assigned inside the block).
        BidPlaced placed;
        // Kept for the outbid email, which is queued after the transaction commits.
        Auction auction;
        string? previousHighBidderId;
        // Both the context and the transaction are disposed at the end of the block; an uncommitted
        // transaction is rolled back on dispose (e.g. when we return a rejection).
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        await using (var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct))
        {
            // Re-read the auction inside the transaction (it may have just ended or been cancelled).
            var found = await db.Auctions.FirstOrDefaultAsync(a => a.Id == auctionId, ct);
            if (found is null)
                return BidResult.Rejected("This auction doesn't exist.");
            // Kept for after the block: the outbid email needs the card, the increment and the end time.
            auction = found;

            // The current high bid: highest amount; on a tie, the earlier bid.
            var high = await db.Bids
                .Where(b => b.AuctionId == auctionId)
                .OrderByDescending(b => b.AmountCents).ThenBy(b => b.CreatedAtUtc)
                .Select(b => new { b.AmountCents, b.BidderId })
                .FirstOrDefaultAsync(ct);
            // Who's about to be outbid (null when this is the first bid).
            previousHighBidderId = high?.BidderId;

            // Apply the rules (Maroos.Core/Auctions/Bid.cs). `high?.X` is null when there are no bids yet.
            var now = clock.GetUtcNow().UtcDateTime;
            var error = BidRules.Validate(auction, bidderId, amountCents, high?.AmountCents, high?.BidderId, now);
            if (error is not null)
                return BidResult.Rejected(error);

            // Save the bid.
            db.Bids.Add(new Bid { AuctionId = auctionId, BidderId = bidderId, AmountCents = amountCents, CreatedAtUtc = now });
            await db.SaveChangesAsync(ct);

            // Gather what the live update needs (bid count, bidder's public name), then commit.
            var bidCount = await db.Bids.CountAsync(b => b.AuctionId == auctionId, ct);
            var bidderName = await db.Users.Where(u => u.Id == bidderId).Select(u => u.DisplayName).FirstOrDefaultAsync(ct);
            await transaction.CommitAsync(ct);

            placed = new BidPlaced(auctionId, amountCents, ApplicationUser.PublicName(bidderName), bidCount, now);
        }

        // Only tell anyone after the bid is committed.
        await events.PublishAsync(placed);
        // Then email whoever just lost the lead.
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
            // Their email and switch. OutbidRule: only the previous high bidder, never about their own bid, only if opted in.
            var previous = await db.Users.Where(u => u.Id == previousHighBidderId)
                .Select(u => new { u.Email, u.AuctionEmails })
                .FirstOrDefaultAsync(ct);
            if (previous?.Email is null || !OutbidRule.ShouldNotify(previousHighBidderId, bidderId, previous.AuctionEmails))
                return;

            // The email text (Maroos.Core/Email): new high bid, minimum next bid, time left, and links.
            var content = AuctionEmailContent.Outbid(emails.InfoFor(auction), amountCents,
                BidRules.MinimumNextBid(auction, amountCents), AuctionRules.TimeLeft(nowUtc, auction.EndsAtUtc));
            await emails.QueueAsync(new EmailMessage(EmailKind.Outbid, previous.Email, content, previousHighBidderId, auction.Id), ct);
        }
        // Logged, never thrown: the bid is already saved.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Couldn't queue the outbid email for auction {AuctionId}", auction.Id);
        }
    }

    /// <summary>Bid history, highest first, with public bidder names.</summary>
    // HistoryAsync: one query joining bids to users (display names only — never emails).
    // viewerId marks the viewer's own bids.
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
    // ListForBidderAsync: profile → Bids tab. One SQL query computes, per auction, the user's highest bid,
    // the top bidder and the current price (sub-queries), so the tab doesn't run one query per row.
    public async Task<IReadOnlyList<MyBid>> ListForBidderAsync(string bidderId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = clock.GetUtcNow().UtcDateTime;

        // Step 1 (database): auctions this user bid on, with the numbers we need.
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

        // Step 2 (in memory): work out each state, then sort — live auctions (Winning/Outbid) first,
        // each group by end time (soonest first).
        return rows
            .Select(r => new MyBid(
                r.Auction.Id, r.Auction.CardName, r.Auction.SetName, r.MyHigh, r.Current, r.Auction.EndsAtUtc,
                // Cancelled -> Cancelled; open -> Winning/Outbid; ended -> Won/Lost.
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
    // IsSerializationConflict: walks the exception and its inner exceptions (EF wraps database errors).
    public static bool IsSerializationConflict(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            // PostgreSQL error codes 40001 (serialization failure) and 40P01 (deadlock).
            if (e is PostgresException { SqlState: PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected })
                return true;
            if (e is SqliteException { SqliteErrorCode: 5 or 6 }) // SQLITE_BUSY, SQLITE_LOCKED
                return true;
        }
        return false;
    }
}
