// =====================================================================================================
// FILE: Maroos.Core/Decks/DeckGroups.cs
// PURPOSE: The headings a deck page shows (Commander, Creatures, Instants, ..., Lands, Sideboard) and which
//   cards go under each. One definition shared by the list view, the Stacks and Grid views, and the tests.
// WHY IN CORE: it's a deck rule ("what goes where"), not page markup, so it can be unit-tested without Blazor.
// =====================================================================================================

namespace Maroos.Core.Decks;

// DeckGroup: one heading. Board says which board its cards are on; Entries are its card lines (with Scryfall data).
/// <summary>One heading on a deck page: its cards and the total number of copies.</summary>
public sealed record DeckGroup(string Heading, Board Board, IReadOnlyList<DeckEntry> Entries)
{
    // Count: copies in the group (4 Lightning Bolt count as 4), shown as "Instants (8)".
    public int Count => Entries.Sum(e => e.Line.Quantity);
}

/// <summary>Splits a deck into the groups a deck page shows, in page order.</summary>
public static class DeckGroups
{
    // For: build the groups. Step 1: Commander. Step 2: one group per card type, in CardTypes.DisplayOrder,
    // using each card's primary type (an Artifact Creature is a Creature). Step 3: Sideboard.
    /// <summary>Commander, then the main deck by type (<see cref="CardTypes.DisplayOrder"/>), then Sideboard.
    /// Empty groups are left out; entries keep their incoming order.</summary>
    public static IReadOnlyList<DeckGroup> For(IEnumerable<DeckEntry> entries)
    {
        // ToList once: the entries are filtered several times below.
        var all = entries.ToList();
        var groups = new List<DeckGroup>();

        Add(groups, "Commander", Board.Commander, all.Where(e => e.Line.Board == Board.Commander));
        foreach (var type in CardTypes.DisplayOrder)
            Add(groups, CardTypes.GroupHeading(type), Board.Main, all.Where(e => e.Line.Board == Board.Main && e.PrimaryType == type));
        Add(groups, "Sideboard", Board.Side, all.Where(e => e.Line.Board == Board.Side));

        return groups;
    }

    // Add: append a group only if it has cards (no empty "Battles (0)" headings).
    private static void Add(List<DeckGroup> groups, string heading, Board board, IEnumerable<DeckEntry> entries)
    {
        var list = entries.ToList();
        if (list.Count > 0)
            groups.Add(new DeckGroup(heading, board, list));
    }
}
