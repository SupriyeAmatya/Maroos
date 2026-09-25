// =====================================================================================================
// FILE: Maroos.Core/Decks/DeckFormats.cs
// PURPOSE: The list of formats a deck can be built for (the "Format" dropdown), and validation of it.
// The keys are exactly Scryfall's legality keys, so a deck's format can be looked up directly in
// each card's Legalities dictionary.
// =====================================================================================================

using Maroos.Core.Scryfall;

namespace Maroos.Core.Decks;

/// <summary>Formats a deck can be built for. Keys match Scryfall's <c>legalities</c> keys.</summary>
public static class DeckFormats
{
    // Default: new decks start as Modern.
    public const string Default = "modern";

    // All: every selectable format, in dropdown order (constructed formats, then commander-style, then others).
    public static readonly IReadOnlyList<string> All =
    [
        "standard", "pioneer", "modern", "legacy", "vintage", "pauper",
        "commander", "oathbreaker", "paupercommander", "duel", "brawl", "standardbrawl",
        "historic", "timeless", "alchemy", "premodern", "oldschool", "penny",
    ];

    // IsKnown: true when `format` is one of the selectable formats. Used to reject bad form input.
    public static bool IsKnown(string format) => All.Contains(format);

    // DisplayName: readable name for a format key (delegates to Formats in the Scryfall folder).
    public static string DisplayName(string format) => Formats.DisplayName(format);
}
