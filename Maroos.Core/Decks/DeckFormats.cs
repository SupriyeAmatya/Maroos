using Maroos.Core.Scryfall;

namespace Maroos.Core.Decks;

/// <summary>Formats a deck can be built for. Keys match Scryfall's <c>legalities</c> keys.</summary>
public static class DeckFormats
{
    public const string Default = "modern";

    public static readonly IReadOnlyList<string> All =
    [
        "standard", "pioneer", "modern", "legacy", "vintage", "pauper",
        "commander", "oathbreaker", "paupercommander", "duel", "brawl", "standardbrawl",
        "historic", "timeless", "alchemy", "premodern", "oldschool", "penny",
    ];

    public static bool IsKnown(string format) => All.Contains(format);

    public static string DisplayName(string format) => Formats.DisplayName(format);
}
