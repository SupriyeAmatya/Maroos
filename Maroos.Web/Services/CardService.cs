using Maroos.Core.Scryfall;
using Maroos.Web.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Maroos.Web.Services;

/// <summary>
/// The entry point for card data in components. Single cards are cached in the database for 24 hours;
/// search and autocomplete results are cached in memory briefly so prerender + interactive renders and
/// repeated keystrokes don't hit Scryfall twice.
/// </summary>
public sealed class CardService(
    ScryfallClient scryfall,
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IMemoryCache memory,
    TimeProvider clock)
{
    private static readonly TimeSpan SearchCacheTime = TimeSpan.FromMinutes(10);

    public Task<ScryfallList<ScryfallCard>> SearchAsync(string query, int page = 1, CancellationToken ct = default) =>
        memory.GetOrCreateAsync(
            $"scryfall:search:{page}:{query.Trim().ToLowerInvariant()}",
            entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = SearchCacheTime;
                return scryfall.SearchAsync(query, page, ct);
            })!;

    public Task<IReadOnlyList<string>> AutocompleteAsync(string text, CancellationToken ct = default) =>
        memory.GetOrCreateAsync(
            $"scryfall:autocomplete:{text.Trim().ToLowerInvariant()}",
            entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = SearchCacheTime;
                return scryfall.AutocompleteAsync(text, ct);
            })!;

    /// <summary>Returns the card from the database cache, fetching from Scryfall when missing or older than 24 hours.</summary>
    public async Task<ScryfallCard?> GetCardAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var cached = await db.CachedCards.FindAsync([id], ct);

        if (cached is not null && !cached.IsStale(clock.GetUtcNow()))
            return ScryfallClient.ParseCard(cached.Json);

        var json = await scryfall.GetCardJsonAsync(id, ct);
        if (json is null)
            return null;

        var card = ScryfallClient.ParseCard(json);
        await SaveToCacheAsync(db, [new FetchedCard(card, json)], ct);
        return card;
    }

    /// <summary>
    /// Returns many cards by id from the database cache, fetching missing or stale ones from Scryfall in
    /// collection batches. Ids Scryfall doesn't know are left out of the result.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, ScryfallCard>> GetCardsAsync(IEnumerable<Guid> ids, CancellationToken ct = default)
    {
        var wanted = ids.Distinct().ToList();
        if (wanted.Count == 0)
            return new Dictionary<Guid, ScryfallCard>();

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = clock.GetUtcNow();
        var cached = await db.CachedCards.AsNoTracking().Where(c => wanted.Contains(c.ScryfallId)).ToListAsync(ct);

        var cards = cached
            .Where(c => !c.IsStale(now))
            .ToDictionary(c => c.ScryfallId, c => ScryfallClient.ParseCard(c.Json));

        var toFetch = wanted.Where(id => !cards.ContainsKey(id)).ToList();
        if (toFetch.Count > 0)
        {
            var fetched = await scryfall.GetCollectionAsync(toFetch.Select(CardIdentifier.ForId), ct);
            await SaveToCacheAsync(db, fetched.Cards, ct);
            foreach (var card in fetched.Cards)
                cards[card.Card.Id] = card.Card;

            // If Scryfall is missing a card we still have an old copy of, the old copy beats nothing.
            foreach (var stale in cached.Where(c => !cards.ContainsKey(c.ScryfallId)))
                cards[stale.ScryfallId] = ScryfallClient.ParseCard(stale.Json);
        }

        return cards;
    }

    /// <summary>Looks cards up by id, name or printing (always from Scryfall) and caches what it finds.</summary>
    public async Task<ScryfallCollectionResult> LookupAsync(IEnumerable<CardIdentifier> identifiers, CancellationToken ct = default)
    {
        var result = await scryfall.GetCollectionAsync(identifiers, ct);
        if (result.Cards.Count > 0)
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            await SaveToCacheAsync(db, result.Cards, ct);
        }
        return result;
    }

    private async Task SaveToCacheAsync(ApplicationDbContext db, IReadOnlyCollection<FetchedCard> fetched, CancellationToken ct)
    {
        var unique = fetched.DistinctBy(f => f.Card.Id).ToList();
        if (unique.Count == 0)
            return;

        var ids = unique.Select(f => f.Card.Id).ToList();
        var existing = await db.CachedCards.Where(c => ids.Contains(c.ScryfallId)).ToDictionaryAsync(c => c.ScryfallId, ct);
        var now = clock.GetUtcNow();

        foreach (var (card, json) in unique)
        {
            if (!existing.TryGetValue(card.Id, out var row))
            {
                row = new CachedCard { ScryfallId = card.Id };
                db.CachedCards.Add(row);
            }
            row.Name = card.Name.Length > 200 ? card.Name[..200] : card.Name;
            row.Json = json;
            row.FetchedAt = now;
        }

        await db.SaveChangesAsync(ct);
    }
}
