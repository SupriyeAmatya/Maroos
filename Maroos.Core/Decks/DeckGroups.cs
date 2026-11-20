namespace Maroos.Core.Decks;

/// <summary>One heading on a deck page: its cards and the total number of copies.</summary>
public sealed record DeckGroup(string Heading, Board Board, IReadOnlyList<DeckEntry> Entries)
{
    public int Count => Entries.Sum(e => e.Line.Quantity);
}

/// <summary>Splits a deck into the groups a deck page shows, in page order.</summary>
public static class DeckGroups
{
    /// <summary>Commander, then the main deck by type (<see cref="CardTypes.DisplayOrder"/>), then Sideboard.
    /// Empty groups are left out; entries keep their incoming order.</summary>
    public static IReadOnlyList<DeckGroup> For(IEnumerable<DeckEntry> entries)
    {
        var all = entries.ToList();
        var groups = new List<DeckGroup>();

        Add(groups, "Commander", Board.Commander, all.Where(e => e.Line.Board == Board.Commander));
        foreach (var type in CardTypes.DisplayOrder)
            Add(groups, CardTypes.GroupHeading(type), Board.Main, all.Where(e => e.Line.Board == Board.Main && e.PrimaryType == type));
        Add(groups, "Sideboard", Board.Side, all.Where(e => e.Line.Board == Board.Side));

        return groups;
    }

    private static void Add(List<DeckGroup> groups, string heading, Board board, IEnumerable<DeckEntry> entries)
    {
        var list = entries.ToList();
        if (list.Count > 0)
            groups.Add(new DeckGroup(heading, board, list));
    }
}
