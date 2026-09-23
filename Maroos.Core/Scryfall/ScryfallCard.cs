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
    IReadOnlyList<ScryfallCardFace>? CardFaces)
{
    /// <summary>Front-face image; double-faced cards keep their images on <see cref="CardFaces"/> instead.</summary>
    public ScryfallImageUris? FrontImage => ImageUris ?? CardFaces?.FirstOrDefault()?.ImageUris;
}

/// <summary>One face of a multi-faced card (transform, modal DFC, split, adventure).</summary>
public sealed record ScryfallCardFace(
    string Name,
    string? ManaCost,
    string? TypeLine,
    string? OracleText,
    ScryfallImageUris? ImageUris);

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

/// <summary>Result of <c>/cards/collection</c> lookups, with names Scryfall couldn't match.</summary>
public sealed record ScryfallCollectionResult(IReadOnlyList<ScryfallCard> Cards, IReadOnlyList<string> NotFound);
