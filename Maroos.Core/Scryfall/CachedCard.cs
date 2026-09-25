// =====================================================================================================
// FILE: Maroos.Core/Scryfall/CachedCard.cs
// PURPOSE: One row of the CachedCards database table — a saved copy of a card from Scryfall.
//
// WHY: Looking a card up on Scryfall takes a network round trip and counts against their rate limit.
// Card data barely changes (prices update once a day), so Maroos keeps a copy for 24 hours and reads
// that instead. CardService decides when a copy is too old and fetches a fresh one.
//
// Entity Framework Core (EF) maps this class to a table: each property becomes a column.
// =====================================================================================================

namespace Maroos.Core.Scryfall;

/// <summary>A Scryfall card cached in the database as raw JSON, refreshed after <see cref="MaxAge"/>.</summary>
public class CachedCard
{
    // MaxAge: how long a saved copy is trusted. After 24 hours it's "stale" and gets refreshed.
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

    // ScryfallId: the card printing's Scryfall id — the table's primary key.
    public Guid ScryfallId { get; set; }
    // Name: the card name, kept as its own column so it can be searched/indexed without reading the JSON.
    public string Name { get; set; } = "";
    // Json: the full card exactly as Scryfall sent it. Turned back into a ScryfallCard with ScryfallClient.ParseCard.
    public string Json { get; set; } = "";
    // FetchedAt: when this copy was downloaded (with time zone, so comparisons are exact).
    public DateTimeOffset FetchedAt { get; set; }

    // IsStale: true when this copy is older than MaxAge at the moment `now`.
    // `now` is passed in (rather than reading the clock here) so tests can pretend time has passed.
    public bool IsStale(DateTimeOffset now) => now - FetchedAt > MaxAge;
}
