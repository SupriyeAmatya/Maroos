// =====================================================================================================
// FILE: Maroos.Core/Decks/DeckViewMode.cs
// PURPOSE: The deck page's three views (List, Stacks, Grid) and how they appear in the address (?view=stacks).
// WHY: the view lives in the URL, so a view can be bookmarked or shared, and Back works. Parsing is forgiving:
//   anything unexpected just shows the list.
// =====================================================================================================

namespace Maroos.Core.Decks;

/// <summary>How a deck page shows its cards: text rows, overlapping picture columns, or a picture grid.</summary>
public enum DeckViewMode
{
    List,
    Stacks,
    Grid,
}

// DeckViewModes: helpers for the enum (C# enums can't have methods of their own).
public static class DeckViewModes
{
    /// <summary>The mode for a <c>?view=</c> value; anything missing or unknown is <see cref="DeckViewMode.List"/>.</summary>
    public static DeckViewMode Parse(string? value) =>
        // Match names only: Enum.TryParse would also accept numbers such as "1".
        Enum.GetValues<DeckViewMode>().FirstOrDefault(m => string.Equals(QueryValue(m), value, StringComparison.OrdinalIgnoreCase));

    /// <summary>The <c>?view=</c> value for a mode ("list", "stacks", "grid").</summary>
    public static string QueryValue(DeckViewMode mode) => mode.ToString().ToLowerInvariant();
}
