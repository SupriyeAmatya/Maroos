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
        var now = clock.GetUtcNow();

        if (cached is not null && !cached.IsStale(now))
            return ScryfallClient.ParseCard(cached.Json);

        var json = await scryfall.GetCardJsonAsync(id, ct);
        if (json is null)
            return null;

        var card = ScryfallClient.ParseCard(json);
        if (cached is null)
        {
            cached = new CachedCard { ScryfallId = id };
            db.CachedCards.Add(cached);
        }
        cached.Name = card.Name;
        cached.Json = json;
        cached.FetchedAt = now;
        await db.SaveChangesAsync(ct);

        return card;
    }
}
