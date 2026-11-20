using System.Globalization;

namespace Maroos.Core.Scryfall;

/// <summary>The subset of a Scryfall card object Maroos uses. JSON is snake_case (see <see cref="ScryfallJson"/>).</summary>
public sealed record ScryfallCard(
    Guid Id,
    string Name,
    string? ManaCost,
    decimal Cmc,
    string? TypeLine,
    string? OracleText,
    IReadOnlyList<string>? Colors,
    string Set,
    string? SetName,
    string? CollectorNumber,
    ScryfallImageUris? ImageUris,
    ScryfallPrices Prices,
    IReadOnlyDictionary<string, string> Legalities,
    IReadOnlyList<ScryfallCardFace>? CardFaces,
    IReadOnlyList<string>? ColorIdentity = null)
{
    /// <summary>Front-face image; double-faced cards keep their images on <see cref="CardFaces"/> instead.</summary>
    public ScryfallImageUris? FrontImage => ImageUris ?? CardFaces?.FirstOrDefault()?.ImageUris;

    /// <summary>Mana cost to show and count: double-faced cards keep theirs on the front face.</summary>
    public string? FrontManaCost => !string.IsNullOrEmpty(ManaCost) ? ManaCost : CardFaces?.FirstOrDefault()?.ManaCost;

    /// <summary>The card's colors; double-faced cards keep theirs on the front face.</summary>
    public IReadOnlyList<string> FrontColors => Colors ?? CardFaces?.FirstOrDefault()?.Colors ?? [];

    /// <summary>Price used for deck totals: non-foil USD, or foil USD for foil-only printings.</summary>
    public long? DeckPriceCents => Prices.UsdCents ?? Prices.UsdFoilCents;
}

/// <summary>One face of a multi-faced card (transform, modal DFC, split, adventure).</summary>
public sealed record ScryfallCardFace(
    string Name,
    string? ManaCost,
    string? TypeLine,
    string? OracleText,
    ScryfallImageUris? ImageUris,
    IReadOnlyList<string>? Colors = null);

public sealed record ScryfallImageUris(string? Small, string? Normal, string? Large, string? ArtCrop);

/// <summary>Scryfall sends prices as decimal strings (or null); Maroos works in <c>long</c> cents.</summary>
public sealed record ScryfallPrices(string? Usd, string? UsdFoil, string? Eur, string? Tix)
{
    public long? UsdCents => ToCents(Usd);
    public long? UsdFoilCents => ToCents(UsdFoil);

    public static long? ToCents(string? price) =>
        decimal.TryParse(price, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var dollars)
            ? (long)decimal.Round(dollars * 100m, MidpointRounding.AwayFromZero)
            : null;
}

/// <summary>A paged list from <c>/cards/search</c>.</summary>
public sealed record ScryfallList<T>(IReadOnlyList<T> Data, bool HasMore, string? NextPage, int TotalCards)
{
    public static ScryfallList<T> Empty { get; } = new([], false, null, 0);
}

/// <summary>A card plus the raw JSON Scryfall sent for it (what the database cache stores).</summary>
public sealed record FetchedCard(ScryfallCard Card, string Json);

/// <summary>
/// One <c>/cards/collection</c> identifier: a Scryfall id, a name (optionally narrowed to a set),
/// or a set code plus collector number.
/// </summary>
public sealed record CardIdentifier(Guid? Id = null, string? Name = null, string? Set = null, string? CollectorNumber = null)
{
    public static CardIdentifier ForId(Guid id) => new(Id: id);
    public static CardIdentifier ForName(string name) => new(Name: name);
    public static CardIdentifier ForPrinting(string set, string collectorNumber) => new(Set: set, CollectorNumber: collectorNumber);

    /// <summary>Whether <paramref name="card"/> is plausibly what this identifier asked for.</summary>
    public bool Matches(ScryfallCard card) =>
        Id is { } id ? card.Id == id
        : CollectorNumber is not null ? string.Equals(card.Set, Set, StringComparison.OrdinalIgnoreCase)
                                        && string.Equals(card.CollectorNumber, CollectorNumber, StringComparison.OrdinalIgnoreCase)
        : Name is not null && NameMatches(card, Name) && (Set is null || string.Equals(card.Set, Set, StringComparison.OrdinalIgnoreCase));

    private static bool NameMatches(ScryfallCard card, string name) =>
        string.Equals(card.Name, name, StringComparison.OrdinalIgnoreCase)
        || card.Name.Split(" // ").Any(face => string.Equals(face, name, StringComparison.OrdinalIgnoreCase));

    public override string ToString() =>
        Id?.ToString()
        ?? (Name is not null ? (Set is not null ? $"{Name} ({Set.ToUpperInvariant()})" : Name)
        : $"({Set?.ToUpperInvariant()}) {CollectorNumber}");
}

/// <summary>Result of <c>/cards/collection</c> lookups: which card each identifier found, and which found nothing.</summary>
public sealed record ScryfallCollectionResult(
    IReadOnlyList<FetchedCard> Cards,
    IReadOnlyList<CardIdentifier> NotFound,
    IReadOnlyDictionary<CardIdentifier, FetchedCard> Found);
