using Maroos.Core.Auctions;
using Maroos.Core.Email;
using Maroos.Core.Scryfall;
using Maroos.Web.Data;
using Maroos.Web.Services.Email;
using Microsoft.EntityFrameworkCore;

namespace Maroos.Web.Services;

public enum AuctionSort
{
    EndingSoon,
    Newest,
    PriceLow,
    PriceHigh,
}

public sealed record AuctionFilter(string? CardName = null, string? SetCode = null, long? MaxCents = null, AuctionSort Sort = AuctionSort.EndingSoon);

/// <param name="ImageUrl">The seller's photo if there is one, otherwise Scryfall's image of the printing.</param>
/// <param name="CurrentCents">The high bid, or the starting price when nobody has bid.</param>
public sealed record AuctionSummary(
    Guid Id,
    Guid ScryfallId,
    string CardName,
    string SetCode,
    string SetName,
    CardCondition Condition,
    bool IsFoil,
    string? ImageUrl,
    long CurrentCents,
    int BidCount,
    DateTime EndsAtUtc);

public sealed record AuctionSetOption(string Code, string Name, int Count);

/// <summary>A seller's own listing, any status, for their profile.</summary>
public sealed record SellerListing(Guid Id, string CardName, string SetName, AuctionStatus Status, bool IsOpen, long CurrentCents, int BidCount, DateTime EndsAtUtc, string? WinnerName);

/// <param name="HighBidderId">Who holds the high bid, if anyone.</param>
/// <param name="WinnerName">The high bidder once the auction is over (null while open, cancelled, or with no bids).</param>
/// <param name="ContactEmail">After a won auction: the seller's email for the winner, the winner's email for the seller; null for everyone else.</param>
public sealed record AuctionView(
    Auction Auction,
    string SellerName,
    bool IsSeller,
    ScryfallCard? Card,
    long CurrentCents,
    int BidCount,
    string? HighBidderId,
    bool IsOpen,
    string? WinnerName,
    bool ViewerIsWinner,
    string? ContactEmail)
{
    /// <summary>Only the seller can cancel, only while open, and only before anyone bids.</summary>
    public bool CanCancel => IsSeller && IsOpen && BidCount == 0;

    public long MinimumNextBid => BidCount == 0 ? Auction.StartCents : CurrentCents + Auction.IncrementCents;
}

/// <summary>Thrown when someone other than the seller tries to change a listing.</summary>
public sealed class AuctionAccessException() : Exception("Only the seller can change this listing.");

/// <summary>
/// Creates, lists, shows, cancels and closes auction listings. Every change checks that the caller is the seller.
/// Bids themselves go through <see cref="BiddingService"/>.
/// </summary>
public sealed class AuctionService(IDbContextFactory<ApplicationDbContext> dbFactory, CardService cards, AuctionEvents events, EmailOutbox emails, TimeProvider clock)
{
    public async Task<Auction> CreateAsync(string sellerId, NewAuction request, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(sellerId))
            throw new AuctionAccessException();

        var errors = AuctionRules.Validate(request).ToList();
        var card = errors.Count == 0 ? await cards.GetCardAsync(request.ScryfallId, ct) : null;
        if (errors.Count == 0 && card is null)
            errors.Add("Scryfall has no card with that id.");
        if (errors.Count > 0)
            throw new AuctionValidationException(errors);

        var now = clock.GetUtcNow().UtcDateTime;
        var auction = new Auction
        {
            SellerId = sellerId,
            ScryfallId = card!.Id,
            CardName = card.Name,
            SetCode = card.Set,
            SetName = card.SetName ?? card.Set.ToUpperInvariant(),
            CollectorNumber = card.CollectorNumber,
            Condition = request.Condition,
            IsFoil = request.IsFoil,
            PhotoPath = request.PhotoPath,
            StartCents = request.StartCents,
            IncrementCents = request.IncrementCents,
            CreatedAtUtc = now,
            EndsAtUtc = now.AddDays(request.DurationDays),
            Status = AuctionStatus.Active,
        };

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.Auctions.Add(auction);
        await db.SaveChangesAsync(ct);
        return auction;
    }

    /// <summary>Open auctions (active and not yet past their end time) matching the filter.</summary>
    public async Task<IReadOnlyList<AuctionSummary>> ListAsync(AuctionFilter filter, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = clock.GetUtcNow().UtcDateTime;
        var auctions = OpenAuctions(db, now);

        if (!string.IsNullOrWhiteSpace(filter.CardName))
        {
            var term = filter.CardName.Trim().ToLower();
            auctions = auctions.Where(a => a.CardName.ToLower().Contains(term));
        }
        if (!string.IsNullOrWhiteSpace(filter.SetCode))
        {
            var set = filter.SetCode.Trim().ToLowerInvariant();
            auctions = auctions.Where(a => a.SetCode == set);
        }

        var priced = auctions.Select(a => new
        {
            Auction = a,
            Current = db.Bids.Where(b => b.AuctionId == a.Id).Max(b => (long?)b.AmountCents) ?? a.StartCents,
            Bids = db.Bids.Count(b => b.AuctionId == a.Id),
        });

        if (filter.MaxCents is { } max)
            priced = priced.Where(p => p.Current <= max);

        priced = filter.Sort switch
        {
            AuctionSort.Newest => priced.OrderByDescending(p => p.Auction.CreatedAtUtc),
            AuctionSort.PriceLow => priced.OrderBy(p => p.Current).ThenBy(p => p.Auction.EndsAtUtc),
            AuctionSort.PriceHigh => priced.OrderByDescending(p => p.Current).ThenBy(p => p.Auction.EndsAtUtc),
            _ => priced.OrderBy(p => p.Auction.EndsAtUtc),
        };

        var rows = await priced.AsNoTracking().Take(200).ToListAsync(ct);
        var cardData = await cards.GetCardsAsync(rows.Where(r => r.Auction.PhotoPath is null).Select(r => r.Auction.ScryfallId), ct);

        return rows.Select(r => new AuctionSummary(
                r.Auction.Id, r.Auction.ScryfallId, r.Auction.CardName, r.Auction.SetCode, r.Auction.SetName, r.Auction.Condition, r.Auction.IsFoil,
                r.Auction.PhotoPath ?? cardData.GetValueOrDefault(r.Auction.ScryfallId)?.FrontImage?.Normal,
                r.Current, r.Bids, r.Auction.EndsAtUtc))
            .ToList();
    }

    /// <summary>Sets that have open auctions, for the set filter.</summary>
    public async Task<IReadOnlyList<AuctionSetOption>> ListSetsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var sets = await OpenAuctions(db, clock.GetUtcNow().UtcDateTime)
            .GroupBy(a => new { a.SetCode, a.SetName })
            .Select(g => new AuctionSetOption(g.Key.SetCode, g.Key.SetName, g.Count()))
            .ToListAsync(ct);
        return sets.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Every listing a seller has made, newest first.</summary>
    public async Task<IReadOnlyList<SellerListing>> ListForSellerAsync(string sellerId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = clock.GetUtcNow().UtcDateTime;
        var rows = await db.Auctions
            .Where(a => a.SellerId == sellerId)
            .OrderByDescending(a => a.CreatedAtUtc)
            .Select(a => new
            {
                Auction = a,
                Current = db.Bids.Where(b => b.AuctionId == a.Id).Max(b => (long?)b.AmountCents) ?? a.StartCents,
                Bids = db.Bids.Count(b => b.AuctionId == a.Id),
                TopBidderName = db.Bids.Where(b => b.AuctionId == a.Id)
                    .OrderByDescending(b => b.AmountCents).ThenBy(b => b.CreatedAtUtc)
                    .Join(db.Users, b => b.BidderId, u => u.Id, (b, u) => u.DisplayName)
                    .FirstOrDefault(),
            })
            .AsNoTracking()
            .ToListAsync(ct);

        return rows.Select(r =>
        {
            var isOpen = r.Auction.IsOpenAt(now);
            var won = !isOpen && r.Auction.Status != AuctionStatus.Cancelled && r.Bids > 0;
            return new SellerListing(r.Auction.Id, r.Auction.CardName, r.Auction.SetName, r.Auction.Status, isOpen,
                r.Current, r.Bids, r.Auction.EndsAtUtc, won ? ApplicationUser.PublicName(r.TopBidderName) : null);
        }).ToList();
    }

    /// <summary>Any auction, open or not, with bids, winner and seller's public name. Null if it doesn't exist.</summary>
    public async Task<AuctionView?> GetAsync(Guid auctionId, string? viewerId, CancellationToken ct = default)
    {
        Auction? auction;
        ApplicationUser? seller;
        ApplicationUser? highBidder = null;
        long? highBid = null;
        int bidCount;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            auction = await db.Auctions.AsNoTracking().FirstOrDefaultAsync(a => a.Id == auctionId, ct);
            if (auction is null)
                return null;

            seller = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == auction.SellerId, ct);
            bidCount = await db.Bids.CountAsync(b => b.AuctionId == auctionId, ct);
            var top = await db.Bids.Where(b => b.AuctionId == auctionId)
                .OrderByDescending(b => b.AmountCents).ThenBy(b => b.CreatedAtUtc)
                .FirstOrDefaultAsync(ct);
            if (top is not null)
            {
                highBid = top.AmountCents;
                highBidder = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == top.BidderId, ct);
            }
        }

        var card = await cards.GetCardAsync(auction.ScryfallId, ct);
        var isOpen = auction.IsOpenAt(clock.GetUtcNow().UtcDateTime);
        var isSeller = viewerId is not null && auction.SellerId == viewerId;

        // The winner is known the moment the auction ends, even before the closer marks it Ended.
        var hasWinner = !isOpen && auction.Status != AuctionStatus.Cancelled && highBidder is not null;
        var viewerIsWinner = hasWinner && highBidder!.Id == viewerId;

        // "Winner contacts seller": only the two people in the sale see each other's email.
        var contact = !hasWinner ? null
            : viewerIsWinner ? seller?.Email
            : isSeller ? highBidder!.Email
            : null;

        return new AuctionView(
            auction,
            ApplicationUser.PublicName(seller?.DisplayName),
            isSeller,
            card,
            highBid ?? auction.StartCents,
            bidCount,
            highBidder?.Id,
            isOpen,
            hasWinner ? ApplicationUser.PublicName(highBidder!.DisplayName) : null,
            viewerIsWinner,
            contact);
    }

    /// <summary>Cancels an open auction nobody has bid on. Only its seller can.</summary>
    public async Task CancelAsync(Guid auctionId, string sellerId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(sellerId))
            throw new AuctionAccessException();

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var auction = await db.Auctions.FirstOrDefaultAsync(a => a.Id == auctionId, ct)
                      ?? throw new KeyNotFoundException($"Auction {auctionId} not found.");
        if (auction.SellerId != sellerId)
            throw new AuctionAccessException();
        if (!auction.IsOpenAt(clock.GetUtcNow().UtcDateTime))
            throw new AuctionValidationException(["This auction has already ended."]);
        if (await db.Bids.AnyAsync(b => b.AuctionId == auctionId, ct))
            throw new AuctionValidationException(["Someone has already bid, so this auction can't be cancelled."]);

        auction.Status = AuctionStatus.Cancelled;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Marks every active auction past its end time as Ended and records the high bidder as the winner.
    /// Returns how many were closed. <see cref="AuctionCloser"/> runs this every minute.
    /// </summary>
    public async Task<int> CloseExpiredAsync(CancellationToken ct = default)
    {
        var ended = new List<AuctionEnded>();
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var now = clock.GetUtcNow().UtcDateTime;
            // With each seller's contact details and email switch, still one query.
            var expired = await db.Auctions
                .Where(a => a.Status == AuctionStatus.Active && a.EndsAtUtc <= now)
                .Join(db.Users, a => a.SellerId, u => u.Id, (a, u) => new
                {
                    Auction = a,
                    SellerName = u.DisplayName,
                    SellerEmail = u.Email,
                    SellerWantsEmails = u.AuctionEmails,
                })
                .ToListAsync(ct);
            if (expired.Count == 0)
                return 0;

            // One query for every expired auction's bids, not one per auction.
            var expiredIds = expired.Select(e => e.Auction.Id).ToList();
            var bids = await db.Bids
                .Where(b => expiredIds.Contains(b.AuctionId))
                .Join(db.Users, b => b.BidderId, u => u.Id, (b, u) => new
                {
                    b.AuctionId, b.BidderId, b.AmountCents, b.CreatedAtUtc, u.DisplayName, u.Email, WantsEmails = u.AuctionEmails,
                })
                .ToListAsync(ct);
            var topBids = bids
                .GroupBy(b => b.AuctionId)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(b => b.AmountCents).ThenBy(b => b.CreatedAtUtc).First());

            foreach (var row in expired)
            {
                var auction = row.Auction;
                var top = topBids.GetValueOrDefault(auction.Id);

                auction.Status = AuctionStatus.Ended;
                auction.WinnerId = top?.BidderId;
                ended.Add(new AuctionEnded(auction.Id, top is null ? null : ApplicationUser.PublicName(top.DisplayName), top?.AmountCents));

                // Queued in this context, so they save with the status change: exactly once per auction.
                var info = emails.InfoFor(auction);
                if (top is null)
                {
                    if (row.SellerWantsEmails && row.SellerEmail is not null)
                        emails.Add(db, new EmailMessage(EmailKind.AuctionUnsold, row.SellerEmail, AuctionEmailContent.Unsold(info), auction.SellerId, auction.Id));
                    continue;
                }

                if (top.WantsEmails && top.Email is not null)
                    emails.Add(db, new EmailMessage(EmailKind.AuctionWon, top.Email,
                        AuctionEmailContent.Won(info, top.AmountCents, ApplicationUser.PublicName(row.SellerName), row.SellerEmail ?? "(not on file)"),
                        top.BidderId, auction.Id));
                if (row.SellerWantsEmails && row.SellerEmail is not null)
                    emails.Add(db, new EmailMessage(EmailKind.AuctionSold, row.SellerEmail,
                        AuctionEmailContent.Sold(info, top.AmountCents, ApplicationUser.PublicName(top.DisplayName), top.Email ?? "(not on file)"),
                        auction.SellerId, auction.Id));
            }

            await db.SaveChangesAsync(ct);
        }

        emails.Nudge();
        foreach (var auctionEnded in ended)
            await events.PublishAsync(auctionEnded);
        return ended.Count;
    }

    private static IQueryable<Auction> OpenAuctions(ApplicationDbContext db, DateTime now) =>
        db.Auctions.Where(a => a.Status == AuctionStatus.Active && a.EndsAtUtc > now);
}
