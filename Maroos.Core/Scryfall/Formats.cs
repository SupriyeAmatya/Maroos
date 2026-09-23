namespace Maroos.Core.Scryfall;

/// <summary>Display names for Scryfall format keys and legality values.</summary>
public static class Formats
{
    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["standard"] = "Standard",
        ["future"] = "Future",
        ["historic"] = "Historic",
        ["timeless"] = "Timeless",
        ["gladiator"] = "Gladiator",
        ["pioneer"] = "Pioneer",
        ["modern"] = "Modern",
        ["legacy"] = "Legacy",
        ["pauper"] = "Pauper",
        ["vintage"] = "Vintage",
        ["penny"] = "Penny Dreadful",
        ["commander"] = "Commander",
        ["oathbreaker"] = "Oathbreaker",
        ["standardbrawl"] = "Standard Brawl",
        ["brawl"] = "Brawl",
        ["competitivebrawl"] = "Competitive Brawl",
        ["alchemy"] = "Alchemy",
        ["paupercommander"] = "Pauper Commander",
        ["duel"] = "Duel Commander",
        ["oldschool"] = "Old School",
        ["premodern"] = "Premodern",
        ["predh"] = "PreDH",
    };

    /// <summary>Known formats get a proper name; unknown short keys are likely acronyms, longer ones get capitalized.</summary>
    public static string DisplayName(string format) =>
        Names.TryGetValue(format, out var name) ? name
        : format.Length <= 3 ? format.ToUpperInvariant()
        : char.ToUpperInvariant(format[0]) + format[1..];

    public static string LegalityLabel(string legality) => legality switch
    {
        "legal" => "Legal",
        "not_legal" => "Not legal",
        "banned" => "Banned",
        "restricted" => "Restricted",
        _ => legality,
    };
}
