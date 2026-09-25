// =====================================================================================================
// FILE: Maroos.Web/Services/CardService.cs
// PURPOSE: The ONLY way pages and services get card data. It sits in front of ScryfallClient and adds caching:
//
//   Single cards (GetCardAsync / GetCardsAsync) -> CachedCards table, fresh for 24 hours
//   Search and autocomplete                     -> in-memory cache, 10 minutes
//
// WHY: Scryfall is free but asks apps to be gentle (and ScryfallThrottle spaces calls >= 100 ms apart).
// Caching makes pages fast and keeps working for cards we've seen even if Scryfall is briefly down.
// Rule (DEVELOPMENT.md): components never call Scryfall directly — always through this class.
// =====================================================================================================

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
// Dependencies:
//   scryfall  — the HTTP client for Scryfall's API
//   dbFactory — creates a short-lived database context per operation (safe for interactive components)
//   memory    — the in-memory cache
//   clock     — current time (fake in tests, to check the 24-hour expiry)
public sealed class CardService(
    ScryfallClient scryfall,
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IMemoryCache memory,
    TimeProvider clock)
{
    // How long search/autocomplete results stay in memory.
    private static readonly TimeSpan SearchCacheTime = TimeSpan.FromMinutes(10);

    // SearchAsync: one page of results for a Scryfall search query (e.g. "t:dragon c:r").
    // The cache key is the page + the lower-cased query, so "Bolt" and "bolt " share an entry.
    // GetOrCreateAsync: return the cached value, or run the lambda, cache its result, and return it.
    public Task<ScryfallList<ScryfallCard>> SearchAsync(string query, int page = 1, CancellationToken ct = default) =>
        memory.GetOrCreateAsync(
            $"scryfall:search:{page}:{query.Trim().ToLowerInvariant()}",
            entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = SearchCacheTime;
                return scryfall.SearchAsync(query, page, ct);
            })!;

    // AutocompleteAsync: card-name suggestions while typing (used by CardAutocomplete).
    public Task<IReadOnlyList<string>> AutocompleteAsync(string text, CancellationToken ct = default) =>
        memory.GetOrCreateAsync(
            $"scryfall:autocomplete:{text.Trim().ToLowerInvariant()}",
            entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = SearchCacheTime;
                return scryfall.AutocompleteAsync(text, ct);
            })!;

    /// <summary>Returns the card from the database cache, fetching from Scryfall when missing or older than 24 hours.</summary>
    // GetCardAsync: used by the card page (/card/{id}) and when creating auctions. Returns null for unknown ids.
    public async Task<ScryfallCard?> GetCardAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        // Step 1: look in the CachedCards table.
        var cached = await db.CachedCards.FindAsync([id], ct);

        // Step 2: fresh copy? Use it (no network call).
        if (cached is not null && !cached.IsStale(clock.GetUtcNow()))
            return ScryfallClient.ParseCard(cached.Json);

        // Step 3: missing or stale — ask Scryfall. null = Scryfall doesn't know this id.
        var json = await scryfall.GetCardJsonAsync(id, ct);
        if (json is null)
            return null;

        // Step 4: parse it, save the raw JSON to the cache, and return it.
        var card = ScryfallClient.ParseCard(json);
        await SaveToCacheAsync(db, [new FetchedCard(card, json)], ct);
        return card;
    }

    /// <summary>
    /// Returns many cards by id from the database cache, fetching missing or stale ones from Scryfall in
    /// collection batches. Ids Scryfall doesn't know are left out of the result.
    /// </summary>
    // GetCardsAsync: used when showing a deck (all its cards at once). One SELECT for the cached ones,
    // then one /cards/collection request per 75 missing cards — never one request per card.
    public async Task<IReadOnlyDictionary<Guid, ScryfallCard>> GetCardsAsync(IEnumerable<Guid> ids, CancellationToken ct = default)
    {
        // Remove duplicate ids; nothing asked for -> empty result.
        var wanted = ids.Distinct().ToList();
        if (wanted.Count == 0)
            return new Dictionary<Guid, ScryfallCard>();

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = clock.GetUtcNow();
        // Step 1: every cached row for these ids, in one query. AsNoTracking: read-only, a bit faster.
        var cached = await db.CachedCards.AsNoTracking().Where(c => wanted.Contains(c.ScryfallId)).ToListAsync(ct);

        // Step 2: keep the fresh ones.
        var cards = cached
            .Where(c => !c.IsStale(now))
            .ToDictionary(c => c.ScryfallId, c => ScryfallClient.ParseCard(c.Json));

        // Step 3: fetch the rest (missing or stale) from Scryfall in batches, and cache them.
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
    // LookupAsync: used by deck import — a pasted list has names / set + number, not ids, so it can't use the cache.
    // The result also says which identifiers weren't found, so the import can report them.
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

    // SaveToCacheAsync: insert new cards and refresh existing rows ("upsert"), then save once.
    private async Task SaveToCacheAsync(ApplicationDbContext db, IReadOnlyCollection<FetchedCard> fetched, CancellationToken ct)
    {
        // The same card can appear twice (e.g. a deck lists it twice); save it once.
        var unique = fetched.DistinctBy(f => f.Card.Id).ToList();
        if (unique.Count == 0)
            return;

        // Load the rows that already exist (one query) so we update them instead of inserting duplicates.
        var ids = unique.Select(f => f.Card.Id).ToList();
        var existing = await db.CachedCards.Where(c => ids.Contains(c.ScryfallId)).ToDictionaryAsync(c => c.ScryfallId, ct);
        var now = clock.GetUtcNow();

        // `var (card, json)` "deconstructs" each FetchedCard into its two parts.
        foreach (var (card, json) in unique)
        {
            // New card -> new row.
            if (!existing.TryGetValue(card.Id, out var row))
            {
                row = new CachedCard { ScryfallId = card.Id };
                db.CachedCards.Add(row);
            }
            // Name is capped at the column size (200); FetchedAt restarts the 24-hour clock.
            row.Name = card.Name.Length > 200 ? card.Name[..200] : card.Name;
            row.Json = json;
            row.FetchedAt = now;
        }

        await db.SaveChangesAsync(ct);
    }
}
