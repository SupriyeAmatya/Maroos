// =====================================================================================================
// FILE: Maroos.Tests/Decks/DeckGroupsTests.cs
// PURPOSE: DeckGroups (the page's card groups, in order) and DeckViewModes (reading ?view=) from Maroos.Core.
// Each test's name says what it proves; the '// TEST:' line above each one repeats it in plain words.
// =====================================================================================================

using Maroos.Core.Decks;
using static Maroos.Tests.Decks.TestCards;

namespace Maroos.Tests.Decks;

public class DeckGroupsTests
{
    // TEST: Groups come in page order commander then types then sideboard.
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

    // TEST: Counts add up quantities and entries keep their order.
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

    // TEST: An empty deck has no groups.
    [Fact]
    public void An_empty_deck_has_no_groups()
    {
        Assert.Empty(DeckGroups.For([]));
    }
}

public class DeckViewModeTests
{
    // TEST: Parse ignores case and falls back to list.
    // "1" matters: Enum.TryParse would accept numbers, so the parser matches names only.
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

    // TEST: Each mode has a lowercase query value that parses back.
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
