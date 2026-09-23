namespace Maroos.Core.Scryfall;

/// <summary>A Scryfall card cached in the database as raw JSON, refreshed after <see cref="MaxAge"/>.</summary>
public class CachedCard
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

    public Guid ScryfallId { get; set; }
    public string Name { get; set; } = "";
    public string Json { get; set; } = "";
    public DateTimeOffset FetchedAt { get; set; }

    public bool IsStale(DateTimeOffset now) => now - FetchedAt > MaxAge;
}
