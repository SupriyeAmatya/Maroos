// =====================================================================================================
// FILE: Maroos.Core/Scryfall/ScryfallCard.cs
// PURPOSE: C# shapes ("records") for the card data that comes back from the Scryfall API.
//
// BACKGROUND: Scryfall (https://scryfall.com/docs/api) is a free public database of every Magic card.
// It returns JSON such as { "id": "...", "name": "Lightning Bolt", "mana_cost": "{R}", "prices": {...} }.
// System.Text.Json turns that JSON into these records automatically; the snake_case names
// ("mana_cost") are mapped to our PascalCase properties ("ManaCost") by ScryfallJson.Options.
//
// A "record" is a class designed to hold data: it gets a constructor, read-only properties,
// equality by value and a readable ToString() for free. Positional records like
// `record X(int A, string B)` declare the properties right in the header.
//
// Only the fields Maroos actually uses are declared; Scryfall's extra fields are simply ignored.
// =====================================================================================================

// Needed for CultureInfo / NumberStyles when parsing price strings.
using System.Globalization;

namespace Maroos.Core.Scryfall;

/// <summary>The subset of a Scryfall card object Maroos uses. JSON is snake_case (see <see cref="ScryfallJson"/>).</summary>
// ScryfallCard: one printing of a card (Lightning Bolt from set M10 is a different printing
// from Lightning Bolt in set 2XM — each has its own Id, image and price).
public sealed record ScryfallCard(
    // Id: Scryfall's unique id (a GUID) for this printing. Used as our key everywhere.
    Guid Id,
    // Name: the card's English name; double-faced cards are "Front // Back".
    string Name,
    // ManaCost: the cost in Scryfall's symbol text, e.g. "{2}{R}{R}". Null for lands and double-faced cards.
    string? ManaCost,
    // Cmc: "converted mana cost" / mana value — total mana needed (e.g. {2}{R}{R} = 4). decimal because
    // a few joke cards cost half a mana (0.5).
    decimal Cmc,
    // TypeLine: e.g. "Creature — Goblin Warrior" or "Instant". Used to group decks by type.
    string? TypeLine,
    // OracleText: the official rules text printed on the card.
    string? OracleText,
    // Colors: color letters of the card itself: W (white), U (blue), B (black), R (red), G (green).
    IReadOnlyList<string>? Colors,
    // Set: short set code, e.g. "c21" (Commander 2021).
    string Set,
    // SetName: the set's full name, e.g. "Commander 2021".
    string? SetName,
    // CollectorNumber: the number printed on the card within its set, e.g. "263". Can contain letters ("123a").
    string? CollectorNumber,
    // ImageUris: links to card pictures in several sizes. Null for double-faced cards (each face has its own).
    ScryfallImageUris? ImageUris,
    // Prices: today's market prices as strings (see ScryfallPrices below).
    ScryfallPrices Prices,
    // Legalities: format -> status, e.g. { "modern": "legal", "legacy": "banned" }.
    IReadOnlyDictionary<string, string> Legalities,
    // CardFaces: for cards with two faces (transform, split, adventure) — each face's own name, cost, text, image.
    IReadOnlyList<ScryfallCardFace>? CardFaces,
    // ColorIdentity: all colors in the card's cost AND rules text. Commander decks may only contain cards
    // whose identity fits the commander's. "= null" makes it optional so older code/tests still compile.
    IReadOnlyList<string>? ColorIdentity = null)
{
    /// <summary>Front-face image; double-faced cards keep their images on <see cref="CardFaces"/> instead.</summary>
    // FrontImage: the picture to show. `??` means "if the left side is null, use the right side":
    // normal cards have ImageUris; double-faced cards don't, so we take the first face's images.
    public ScryfallImageUris? FrontImage => ImageUris ?? CardFaces?.FirstOrDefault()?.ImageUris;

    /// <summary>Mana cost to show and count: double-faced cards keep theirs on the front face.</summary>
    // FrontManaCost: same idea for the mana cost — use the card's own cost, else the front face's.
    public string? FrontManaCost => !string.IsNullOrEmpty(ManaCost) ? ManaCost : CardFaces?.FirstOrDefault()?.ManaCost;

    /// <summary>The card's colors; double-faced cards keep theirs on the front face.</summary>
    // FrontColors: the colors to count in deck stats; never null (falls back to an empty list `[]`).
    public IReadOnlyList<string> FrontColors => Colors ?? CardFaces?.FirstOrDefault()?.Colors ?? [];

    /// <summary>Price used for deck totals: non-foil USD, or foil USD for foil-only printings.</summary>
    // DeckPriceCents: the price used when adding up a deck's value. Some printings only exist in foil,
    // so when there's no normal price we fall back to the foil price.
    public long? DeckPriceCents => Prices.UsdCents ?? Prices.UsdFoilCents;
}

/// <summary>One face of a multi-faced card (transform, modal DFC, split, adventure).</summary>
// ScryfallCardFace: the per-face data for cards like "Delver of Secrets // Insectile Aberration".
public sealed record ScryfallCardFace(
    // Name of this face.
    string Name,
    // Mana cost of this face (the back face of a transform card usually has none).
    string? ManaCost,
    // Type line of this face.
    string? TypeLine,
    // Rules text of this face.
    string? OracleText,
    // Picture of this face.
    ScryfallImageUris? ImageUris,
    // Colors of this face (optional).
    IReadOnlyList<string>? Colors = null);

// ScryfallImageUris: links to the same card picture at different sizes.
//   Small ≈ 146 px wide, Normal ≈ 488 px (what Maroos shows), Large ≈ 672 px, ArtCrop = just the artwork.
public sealed record ScryfallImageUris(string? Small, string? Normal, string? Large, string? ArtCrop);

/// <summary>Scryfall sends prices as decimal strings (or null); Maroos works in <c>long</c> cents.</summary>
// ScryfallPrices: Scryfall gives prices as TEXT like "0.72" (or null when unknown).
//   Usd = US dollars, UsdFoil = foil version in US dollars, Eur = euros, Tix = MTG Online tickets.
public sealed record ScryfallPrices(string? Usd, string? UsdFoil, string? Eur, string? Tix)
{
    // UsdCents: the normal USD price converted to cents (e.g. "0.72" -> 72), or null.
    public long? UsdCents => ToCents(Usd);
    // UsdFoilCents: the foil USD price in cents, or null.
    public long? UsdFoilCents => ToCents(UsdFoil);

    // ToCents: converts a Scryfall price string to cents.
    //   price — text like "12.34" or null.
    //   returns the amount in cents, or null when the text is missing or not a number.
    // MidpointRounding.AwayFromZero rounds 0.005 up to 1 cent (normal "shop" rounding).
    public static long? ToCents(string? price) =>
        decimal.TryParse(price, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var dollars)
            ? (long)decimal.Round(dollars * 100m, MidpointRounding.AwayFromZero)
            : null;
}

/// <summary>A paged list from <c>/cards/search</c>.</summary>
// ScryfallList<T>: the "page of results" wrapper Scryfall uses for searches.
//   Data       — the results on this page (up to 175).
//   HasMore    — true when there is another page.
//   NextPage   — URL of the next page (unused; Maroos asks for page numbers instead).
//   TotalCards — how many cards match in total, across all pages.
// <T> makes it generic: ScryfallList<ScryfallCard> is a list of cards.
public sealed record ScryfallList<T>(IReadOnlyList<T> Data, bool HasMore, string? NextPage, int TotalCards)
{
    // Empty: a ready-made "no results" list, returned when a search finds nothing.
    public static ScryfallList<T> Empty { get; } = new([], false, null, 0);
}

/// <summary>A card plus the raw JSON Scryfall sent for it (what the database cache stores).</summary>
// FetchedCard: the parsed card AND the exact JSON text it came from. The JSON is saved in the
// CachedCards database table so the card can be rebuilt later without calling Scryfall again.
public sealed record FetchedCard(ScryfallCard Card, string Json);

/// <summary>
/// One <c>/cards/collection</c> identifier: a Scryfall id, a name (optionally narrowed to a set),
/// or a set code plus collector number.
/// </summary>
// CardIdentifier: one "which card do you want?" entry for Scryfall's bulk lookup endpoint
// (/cards/collection accepts up to 75 of these per request). Exactly one style is used:
//   by Id                       -> { "id": "..." }
//   by Name (optionally + Set)  -> { "name": "Sol Ring" } or { "name": "Sol Ring", "set": "c21" }
//   by printing                 -> { "set": "c21", "collector_number": "263" }
// Unused fields stay null and are left out of the JSON (see ScryfallJson.Options).
public sealed record CardIdentifier(Guid? Id = null, string? Name = null, string? Set = null, string? CollectorNumber = null)
{
    // ForId / ForName / ForPrinting: small "factory" helpers so calling code reads clearly,
    // e.g. CardIdentifier.ForName("Sol Ring") instead of new CardIdentifier(Name: "Sol Ring").
    public static CardIdentifier ForId(Guid id) => new(Id: id);
    public static CardIdentifier ForName(string name) => new(Name: name);
    public static CardIdentifier ForPrinting(string set, string collectorNumber) => new(Set: set, CollectorNumber: collectorNumber);

    /// <summary>Whether <paramref name="card"/> is plausibly what this identifier asked for.</summary>
    // Matches: does this returned card answer this identifier? Used as a fallback when Scryfall's
    // answer can't be paired up by position (see ScryfallClient.MatchToIdentifiers).
    //   by Id       -> the ids must be equal
    //   by printing -> same set AND same collector number (ignoring upper/lower case)
    //   by Name     -> same name (or one face of a double-faced card), and same set if one was given
    public bool Matches(ScryfallCard card) =>
        Id is { } id ? card.Id == id
        : CollectorNumber is not null ? string.Equals(card.Set, Set, StringComparison.OrdinalIgnoreCase)
                                        && string.Equals(card.CollectorNumber, CollectorNumber, StringComparison.OrdinalIgnoreCase)
        : Name is not null && NameMatches(card, Name) && (Set is null || string.Equals(card.Set, Set, StringComparison.OrdinalIgnoreCase));

    // NameMatches: true when the card's full name equals `name`, or any single face does
    // ("Delver of Secrets" matches "Delver of Secrets // Insectile Aberration").
    private static bool NameMatches(ScryfallCard card, string name) =>
        string.Equals(card.Name, name, StringComparison.OrdinalIgnoreCase)
        || card.Name.Split(" // ").Any(face => string.Equals(face, name, StringComparison.OrdinalIgnoreCase));

    // ToString: readable text for messages and logs — the id, "Name (SET)", "Name", or "(SET) number".
    public override string ToString() =>
        Id?.ToString()
        ?? (Name is not null ? (Set is not null ? $"{Name} ({Set.ToUpperInvariant()})" : Name)
        : $"({Set?.ToUpperInvariant()}) {CollectorNumber}");
}

/// <summary>Result of <c>/cards/collection</c> lookups: which card each identifier found, and which found nothing.</summary>
// ScryfallCollectionResult: everything a bulk lookup returned.
//   Cards    — every card found (in the order Scryfall returned them).
//   NotFound — the identifiers Scryfall had no card for (e.g. a misspelled name in an imported deck list).
//   Found    — a dictionary answering "for THIS identifier, which card came back?".
public sealed record ScryfallCollectionResult(
    IReadOnlyList<FetchedCard> Cards,
    IReadOnlyList<CardIdentifier> NotFound,
    IReadOnlyDictionary<CardIdentifier, FetchedCard> Found);
