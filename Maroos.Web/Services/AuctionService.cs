// =====================================================================================================
// FILE: Maroos.Web/Services/AuctionService.cs
// PURPOSE: Auction listings (everything except placing bids, which is BiddingService):
//
//   CreateAsync         — list a card for auction (/auctions/new)
//   ListAsync           — browse open auctions with filters and sorting (/auctions)
//   ListSetsAsync       — the sets for the "Set" filter dropdown
//   ListForSellerAsync  — profile → Listings tab
//   GetAsync            — one auction for its page (/auctions/{id}), including winner + contact rules
//   CancelAsync         — seller cancels (only while open and before any bid)
//   CloseExpiredAsync   — called every minute by AuctionCloser: mark ended auctions, record winners and
//                         queue the won / sold / no-bids emails
//
// "CURRENT PRICE" everywhere = the highest bid, or the starting price when nobody has bid yet.
// "OPEN" = Status is Active AND the end time hasn't passed (see Auction.IsOpenAt). An auction past its end
// time is treated as closed immediately, even if the closer hasn't run yet.
// =====================================================================================================

using Maroos.Core.Auctions;
using Maroos.Core.Email;
using Maroos.Core.Scryfall;
using Maroos.Web.Data;
using Maroos.Web.Services.Email;
using Microsoft.EntityFrameworkCore;

namespace Maroos.Web.Services;

// AuctionSort: the "Sort by" choices on /auctions.
public enum AuctionSort
{
    EndingSoon,
    Newest,
    PriceLow,
    PriceHigh,
}

// AuctionFilter: the /auctions search form (all optional). MaxCents filters on the CURRENT price.
public sealed record AuctionFilter(string? CardName = null, string? SetCode = null, long? MaxCents = null, AuctionSort Sort = AuctionSort.EndingSoon);

/// <param name="ImageUrl">The seller's photo if there is one, otherwise Scryfall's image of the printing.</param>
/// <param name="CurrentCents">The high bid, or the starting price when nobody has bid.</param>
// AuctionSummary: one tile on the /auctions grid.
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

// AuctionSetOption: one entry of the set filter: code ("2x2"), name ("Double Masters 2022"), open auctions in it.
public sealed record AuctionSetOption(string Code, string Name, int Count);

/// <summary>A seller's own listing, any status, for their profile.</summary>
// WinnerName: the winner's public name once the auction is over with bids (else null).
public sealed record SellerListing(Guid Id, string CardName, string SetName, AuctionStatus Status, bool IsOpen, long CurrentCents, int BidCount, DateTime EndsAtUtc, string? WinnerName);

/// <param name="HighBidderId">Who holds the high bid, if anyone.</param>
/// <param name="WinnerName">The high bidder once the auction is over (null while open, cancelled, or with no bids).</param>
/// <param name="ContactEmail">After a won auction: the seller's email for the winner, the winner's email for the seller; null for everyone else.</param>
// AuctionView: everything the auction page needs, already filtered for the viewer (e.g. ContactEmail).
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

    // MinimumNextBid: pre-fills the bid box (same rule as BidRules.MinimumNextBid).
    public long MinimumNextBid => BidCount == 0 ? Auction.StartCents : CurrentCents + Auction.IncrementCents;
}

/// <summary>Thrown when someone other than the seller tries to change a listing.</summary>
public sealed class AuctionAccessException() : Exception("Only the seller can change this listing.");

/// <summary>
/// Creates, lists, shows, cancels and closes auction listings. Every change checks that the caller is the seller.
/// Bids themselves go through <see cref="BiddingService"/>.
/// </summary>
// emails: the email outbox, used by CloseExpiredAsync for the won / sold / no-bids emails.
public sealed class AuctionService(IDbContextFactory<ApplicationDbContext> dbFactory, CardService cards, AuctionEvents events, EmailOutbox emails, TimeProvider clock)
{
    // CreateAsync: validate the form, fetch the card from Scryfall (via the cache), save the listing.
    // Card name/set/number are copied from Scryfall — the seller can't type in misleading details.
    public async Task<Auction> CreateAsync(string sellerId, NewAuction request, CancellationToken ct = default)
    {
        // Must be signed in.
        if (string.IsNullOrEmpty(sellerId))
            throw new AuctionAccessException();

        // Step 1: the rules in Maroos.Core (prices > 0, 1–7 days, ...). Only call Scryfall if they pass.
        var errors = AuctionRules.Validate(request).ToList();
        var card = errors.Count == 0 ? await cards.GetCardAsync(request.ScryfallId, ct) : null;
        if (errors.Count == 0 && card is null)
            errors.Add("Scryfall has no card with that id.");
        // All problems are reported together (the page lists them).
        if (errors.Count > 0)
            throw new AuctionValidationException(errors);

        // Step 2: build the listing. `card!` tells the compiler card isn't null here (checked above).
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

        // Step 3: save.
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.Auctions.Add(auction);
        await db.SaveChangesAsync(ct);
        return auction;
    }

    /// <summary>Open auctions (active and not yet past their end time) matching the filter.</summary>
    // ListAsync: builds ONE SQL query step by step (IQueryable), then runs it; max 200 results.
    public async Task<IReadOnlyList<AuctionSummary>> ListAsync(AuctionFilter filter, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = clock.GetUtcNow().UtcDateTime;
        var auctions = OpenAuctions(db, now);

        // Card name filter: case-insensitive "contains" (ToLower works the same on SQLite and PostgreSQL).
        if (!string.IsNullOrWhiteSpace(filter.CardName))
        {
            var term = filter.CardName.Trim().ToLower();
            auctions = auctions.Where(a => a.CardName.ToLower().Contains(term));
        }
        // Set filter: set codes are stored lower-case, as Scryfall gives them.
        if (!string.IsNullOrWhiteSpace(filter.SetCode))
        {
            var set = filter.SetCode.Trim().ToLowerInvariant();
            auctions = auctions.Where(a => a.SetCode == set);
        }

        // Add the current price and bid count to each row (computed by the database, not per row in C#).
        var priced = auctions.Select(a => new
        {
            Auction = a,
            Current = db.Bids.Where(b => b.AuctionId == a.Id).Max(b => (long?)b.AmountCents) ?? a.StartCents,
            Bids = db.Bids.Count(b => b.AuctionId == a.Id),
        });

        // "Max price" filter on the current price.
        if (filter.MaxCents is { } max)
            priced = priced.Where(p => p.Current <= max);

        // Sorting; ties on price are broken by "ending soonest".
        priced = filter.Sort switch
        {
            AuctionSort.Newest => priced.OrderByDescending(p => p.Auction.CreatedAtUtc),
            AuctionSort.PriceLow => priced.OrderBy(p => p.Current).ThenBy(p => p.Auction.EndsAtUtc),
            AuctionSort.PriceHigh => priced.OrderByDescending(p => p.Current).ThenBy(p => p.Auction.EndsAtUtc),
            _ => priced.OrderBy(p => p.Auction.EndsAtUtc),
        };

        // Run the query. Then fetch Scryfall images (batched) only for listings without a seller photo.
        var rows = await priced.AsNoTracking().Take(200).ToListAsync(ct);
        var cardData = await cards.GetCardsAsync(rows.Where(r => r.Auction.PhotoPath is null).Select(r => r.Auction.ScryfallId), ct);

        return rows.Select(r => new AuctionSummary(
                r.Auction.Id, r.Auction.ScryfallId, r.Auction.CardName, r.Auction.SetCode, r.Auction.SetName, r.Auction.Condition, r.Auction.IsFoil,
                // Seller's photo first, else Scryfall's image.
                r.Auction.PhotoPath ?? cardData.GetValueOrDefault(r.Auction.ScryfallId)?.FrontImage?.Normal,
                r.Current, r.Bids, r.Auction.EndsAtUtc))
            .ToList();
    }

    /// <summary>Sets that have open auctions, for the set filter.</summary>
    public async Task<IReadOnlyList<AuctionSetOption>> ListSetsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        // GROUP BY set in SQL, then sort by name in memory.
        var sets = await OpenAuctions(db, clock.GetUtcNow().UtcDateTime)
            .GroupBy(a => new { a.SetCode, a.SetName })
            .Select(g => new AuctionSetOption(g.Key.SetCode, g.Key.SetName, g.Count()))
            .ToListAsync(ct);
        return sets.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Every listing a seller has made, newest first.</summary>
    // ListForSellerAsync: profile → Listings. One query with sub-queries for price, bid count and top bidder's name.
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
            // "Won" = over, not cancelled, and had at least one bid. Only then is a winner name shown.
            var isOpen = r.Auction.IsOpenAt(now);
            var won = !isOpen && r.Auction.Status != AuctionStatus.Cancelled && r.Bids > 0;
            return new SellerListing(r.Auction.Id, r.Auction.CardName, r.Auction.SetName, r.Auction.Status, isOpen,
                r.Current, r.Bids, r.Auction.EndsAtUtc, won ? ApplicationUser.PublicName(r.TopBidderName) : null);
        }).ToList();
    }

    /// <summary>Any auction, open or not, with bids, winner and seller's public name. Null if it doesn't exist.</summary>
    // GetAsync: the auction page's data, tailored to `viewerId` (seller? winner? someone else? signed out = null).
    public async Task<AuctionView?> GetAsync(Guid auctionId, string? viewerId, CancellationToken ct = default)
    {
        // Values filled inside the database block below.
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

            // Seller, bid count, and the top bid (highest; earliest on a tie) with its bidder.
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

        // Card details (image, text) from the cache.
        var card = await cards.GetCardAsync(auction.ScryfallId, ct);
        var isOpen = auction.IsOpenAt(clock.GetUtcNow().UtcDateTime);
        var isSeller = viewerId is not null && auction.SellerId == viewerId;

        // The winner is known the moment the auction ends, even before the closer marks it Ended.
        var hasWinner = !isOpen && auction.Status != AuctionStatus.Cancelled && highBidder is not null;
        var viewerIsWinner = hasWinner && highBidder!.Id == viewerId;

        // "Winner contacts seller": only the two people in the sale see each other's email.
        // (Chained ternary: no winner -> null; viewer is winner -> seller's email; viewer is seller -> winner's email; else null.)
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
        // Checks, in order: exists -> you're the seller -> still open -> no bids yet.
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
        // Events to publish after saving.
        var ended = new List<AuctionEnded>();
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            // Step 1: every auction still Active whose end time has passed.
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
                // ...with each bidder's public name, email and email switch (for the won / sold emails).
                .Join(db.Users, b => b.BidderId, u => u.Id, (b, u) => new
                {
                    b.AuctionId, b.BidderId, b.AmountCents, b.CreatedAtUtc, u.DisplayName, u.Email, WantsEmails = u.AuctionEmails,
                })
                .ToListAsync(ct);
            // Step 2: the winning bid per auction (highest; earliest on a tie).
            var topBids = bids
                .GroupBy(b => b.AuctionId)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(b => b.AmountCents).ThenBy(b => b.CreatedAtUtc).First());

            // Step 3: mark each Ended, record the winner (null WinnerId when nobody bid) and queue its emails.
            foreach (var row in expired)
            {
                var auction = row.Auction;
                var top = topBids.GetValueOrDefault(auction.Id);

                auction.Status = AuctionStatus.Ended;
                auction.WinnerId = top?.BidderId;
                ended.Add(new AuctionEnded(auction.Id, top is null ? null : ApplicationUser.PublicName(top.DisplayName), top?.AmountCents));

                // Queued in this context, so they save with the status change: exactly once per auction.
                var info = emails.InfoFor(auction);
                // No bids: tell the seller. Otherwise tell the winner and the seller, each only if they want auction emails.
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

        // Step 4: after saving, wake the email sender, then tell open pages / hub clients.
        emails.Nudge();
        foreach (var auctionEnded in ended)
            await events.PublishAsync(auctionEnded);
        return ended.Count;
    }

    // OpenAuctions: the shared "open" filter, written so the database can run it (IsOpenAt can't be translated to SQL).
    private static IQueryable<Auction> OpenAuctions(ApplicationDbContext db, DateTime now) =>
        db.Auctions.Where(a => a.Status == AuctionStatus.Active && a.EndsAtUtc > now);
}
