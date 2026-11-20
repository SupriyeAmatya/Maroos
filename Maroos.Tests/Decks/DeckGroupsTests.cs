using Maroos.Core.Decks;
using static Maroos.Tests.Decks.TestCards;

namespace Maroos.Tests.Decks;

public class DeckGroupsTests
{
    [Fact]
    public void Groups_come_in_page_order_commander_then_types_then_sideboard()
    {
        var groups = DeckGroups.For([
            Entry(3, Card("Pyroblast"), Board.Side),
            Entry(20, Land()),
            Entry(4, Card("Lightning Bolt")),
            Entry(4, Card("Goblin Guide", "Creature — Goblin Scout")),
            Entry(1, Card("Krenko, Mob Boss", "Legendary Creature — Goblin Warrior"), Board.Commander),
        ]);

        Assert.Equal(["Commander", "Creatures", "Instants", "Lands", "Sideboard"], groups.Select(g => g.Heading));
        Assert.Equal([Board.Commander, Board.Main, Board.Main, Board.Main, Board.Side], groups.Select(g => g.Board));
    }

    [Fact]
    public void Counts_add_up_quantities_and_entries_keep_their_order()
    {
        var groups = DeckGroups.For([
            Entry(4, Card("Lightning Bolt")),
            Entry(2, Card("Counterspell")),
            Entry(1, Card("Brainstorm")),
        ]);

        var instants = Assert.Single(groups);
        Assert.Equal(7, instants.Count);
        Assert.Equal(["Lightning Bolt", "Counterspell", "Brainstorm"], instants.Entries.Select(e => e.Line.CardName));
    }

    [Fact]
    public void An_empty_deck_has_no_groups()
    {
        Assert.Empty(DeckGroups.For([]));
    }
}

public class DeckViewModeTests
{
    [Theory]
    [InlineData(null, DeckViewMode.List)]
    [InlineData("", DeckViewMode.List)]
    [InlineData("list", DeckViewMode.List)]
    [InlineData("stacks", DeckViewMode.Stacks)]
    [InlineData("Grid", DeckViewMode.Grid)]
    [InlineData("pictures", DeckViewMode.List)]
    [InlineData("1", DeckViewMode.List)]
    public void Parse_ignores_case_and_falls_back_to_list(string? value, DeckViewMode expected)
    {
        Assert.Equal(expected, DeckViewModes.Parse(value));
    }

    [Fact]
    public void Each_mode_has_a_lowercase_query_value_that_parses_back()
    {
        foreach (var mode in Enum.GetValues<DeckViewMode>())
        {
            var value = DeckViewModes.QueryValue(mode);
            Assert.Equal(value.ToLowerInvariant(), value);
            Assert.Equal(mode, DeckViewModes.Parse(value));
        }
    }
}
