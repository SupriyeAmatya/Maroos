// =====================================================================================================
// FILE: Maroos.Core/Scryfall/Formats.cs
// PURPOSE: Turns Scryfall's internal keys into words people read.
//   Format keys:     "paupercommander" -> "Pauper Commander", "duel" -> "Duel Commander"
//   Legality values: "not_legal"       -> "Not legal"
// USED BY: the card page's legality table, the deck page's format badge and deck rule messages.
// =====================================================================================================

namespace Maroos.Core.Scryfall;

/// <summary>Display names for Scryfall format keys and legality values.</summary>
public static class Formats
{
    // Names: known format keys and their proper names.
    // StringComparer.OrdinalIgnoreCase makes lookups ignore upper/lower case ("Modern" finds "modern").
    // The `[key] = value` lines are a C# "dictionary initializer".
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
        // Added after Scryfall started returning this key (found while testing the card page).
        ["competitivebrawl"] = "Competitive Brawl",
        ["alchemy"] = "Alchemy",
        ["paupercommander"] = "Pauper Commander",
        ["duel"] = "Duel Commander",
        ["oldschool"] = "Old School",
        ["premodern"] = "Premodern",
        ["predh"] = "PreDH",
    };

    /// <summary>Known formats get a proper name; unknown short keys are likely acronyms, longer ones get capitalized.</summary>
    // DisplayName: format key -> readable name.
    //   1. a known key -> its name from the table;
    //   2. an unknown key of 3 letters or fewer -> upper case (probably an acronym, e.g. "tlr" -> "TLR");
    //   3. any other unknown key -> first letter capitalised ("someformat" -> "Someformat").
    // `format[1..]` is C# range syntax: "everything from index 1 to the end".
    public static string DisplayName(string format) =>
        Names.TryGetValue(format, out var name) ? name
        : format.Length <= 3 ? format.ToUpperInvariant()
        : char.ToUpperInvariant(format[0]) + format[1..];

    // LegalityLabel: Scryfall legality value -> label. A `switch` expression picks the first matching case;
    // `_` is the catch-all ("anything else: show it as-is").
    public static string LegalityLabel(string legality) => legality switch
    {
        "legal" => "Legal",
        "not_legal" => "Not legal",
        "banned" => "Banned",
        "restricted" => "Restricted",
        _ => legality,
    };
}
