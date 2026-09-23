using Maroos.Core.Auctions;
using Maroos.Core.Scryfall;
using Maroos.Web.Data;
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
    DateTime EndsAtUtc);

public sealed record AuctionSetOption(string Code, string Name, int Count);

public sealed record AuctionView(Auction Auction, string SellerName, bool IsSeller, ScryfallCard? Card, long CurrentCents, bool IsOpen)
{
    /// <summary>Only the seller can cancel, and only while the auction is open.</summary>
    public bool CanCancel => IsSeller && IsOpen;
}

/// <summary>Thrown when someone other than the seller tries to change a listing.</summary>
public sealed class AuctionAccessException() : Exception("Only the seller can change this listing.");

/// <summary>Creates, lists, shows and cancels auction listings. Every change checks that the caller is the seller.</summary>
public sealed class AuctionService(IDbContextFactory<ApplicationDbContext> dbFactory, CardService cards, TimeProvider clock)
{
    public async Task<Auction> CreateAsync(string sellerId, NewAuction request, CancellationToken ct = default)
    {
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
        var query = OpenAuctions(db, now);

        if (!string.IsNullOrWhiteSpace(filter.CardName))
        {
            var term = filter.CardName.Trim().ToLower();
            query = query.Where(a => a.CardName.ToLower().Contains(term));
        }
        if (!string.IsNullOrWhiteSpace(filter.SetCode))
        {
            var set = filter.SetCode.Trim().ToLowerInvariant();
            query = query.Where(a => a.SetCode == set);
        }
        // Until bidding exists (phase 6) the current price is the starting price.
        if (filter.MaxCents is { } max)
            query = query.Where(a => a.StartCents <= max);

        query = filter.Sort switch
        {
            AuctionSort.Newest => query.OrderByDescending(a => a.CreatedAtUtc),
            AuctionSort.PriceLow => query.OrderBy(a => a.StartCents).ThenBy(a => a.EndsAtUtc),
            AuctionSort.PriceHigh => query.OrderByDescending(a => a.StartCents).ThenBy(a => a.EndsAtUtc),
            _ => query.OrderBy(a => a.EndsAtUtc),
        };

        var auctions = await query.AsNoTracking().Take(200).ToListAsync(ct);
        var cardData = await cards.GetCardsAsync(auctions.Where(a => a.PhotoPath is null).Select(a => a.ScryfallId), ct);

        return auctions.Select(a => new AuctionSummary(
                a.Id, a.ScryfallId, a.CardName, a.SetCode, a.SetName, a.Condition, a.IsFoil,
                a.PhotoPath ?? cardData.GetValueOrDefault(a.ScryfallId)?.FrontImage?.Normal,
                a.StartCents, a.EndsAtUtc))
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

    /// <summary>Any auction, open or not, with its seller's public name. Null if it doesn't exist.</summary>
    public async Task<AuctionView?> GetAsync(Guid auctionId, string? viewerId, CancellationToken ct = default)
    {
        Auction? auction;
        string? sellerName;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            auction = await db.Auctions.AsNoTracking().FirstOrDefaultAsync(a => a.Id == auctionId, ct);
            if (auction is null)
                return null;
            sellerName = await db.Users.Where(u => u.Id == auction.SellerId).Select(u => u.DisplayName).FirstOrDefaultAsync(ct);
        }

        var card = await cards.GetCardAsync(auction.ScryfallId, ct);
        var isSeller = viewerId is not null && auction.SellerId == viewerId;
        return new AuctionView(
            auction,
            ApplicationUser.PublicName(sellerName),
            isSeller,
            card,
            auction.StartCents,
            auction.IsOpenAt(clock.GetUtcNow().UtcDateTime));
    }

    /// <summary>Cancels an open auction. Only its seller can.</summary>
    public async Task CancelAsync(Guid auctionId, string sellerId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var auction = await db.Auctions.FirstOrDefaultAsync(a => a.Id == auctionId, ct)
                      ?? throw new KeyNotFoundException($"Auction {auctionId} not found.");
        if (auction.SellerId != sellerId)
            throw new AuctionAccessException();
        if (!auction.IsOpenAt(clock.GetUtcNow().UtcDateTime))
            throw new AuctionValidationException(["This auction has already ended."]);

        auction.Status = AuctionStatus.Cancelled;
        await db.SaveChangesAsync(ct);
    }

    private static IQueryable<Auction> OpenAuctions(ApplicationDbContext db, DateTime now) =>
        db.Auctions.Where(a => a.Status == AuctionStatus.Active && a.EndsAtUtc > now);
}
