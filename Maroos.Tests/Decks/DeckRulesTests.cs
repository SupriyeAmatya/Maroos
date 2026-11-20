using Maroos.Core.Decks;
using static Maroos.Tests.Decks.TestCards;

namespace Maroos.Tests.Decks;

public class DeckRulesTests
{
    private static List<DeckEntry> SixtyCards(params DeckEntry[] extra)
    {
        var filler = 60 - extra.Where(e => e.Line.Board == Board.Main).Sum(e => e.Line.Quantity);
        return [.. extra, Entry(filler, Land())];
    }

    [Fact]
    public void A_legal_60_card_deck_has_no_issues()
    {
        Assert.Empty(DeckRules.Check("modern", SixtyCards(Entry(4, Card("Lightning Bolt")))));
    }

    [Fact]
    public void Banned_and_not_legal_cards_are_reported_once_per_card_name()
    {
        var banned = new Dictionary<string, string> { ["modern"] = "banned" };
        var notLegal = new Dictionary<string, string> { ["modern"] = "not_legal" };

        var issues = DeckRules.Check("modern", SixtyCards(
            Entry(1, Card("Ancestral Recall", legalities: notLegal)),
            Entry(2, Card("Ponder", legalities: banned)),
            Entry(1, Card("Ponder", legalities: banned), Board.Side)));

        Assert.Equal(
        [
            (DeckIssueKind.NotLegal, "Ancestral Recall isn't legal in Modern.", "Ancestral Recall"),
            (DeckIssueKind.Banned, "Ponder is banned in Modern.", "Ponder"),
        ], issues.Select(i => (i.Kind, i.Message, i.CardName)));
    }

    [Fact]
    public void Constructed_decks_need_60_cards_and_at_most_15_in_the_sideboard()
    {
        var issues = DeckRules.Check("modern",
        [
            Entry(57, Land()),
            Entry(16, Land("Island", "Basic Land — Island"), Board.Side),
        ]);

        Assert.Equal(
        [
            "The main deck has 57 cards; Modern needs at least 60.",
            "The sideboard has 16 cards; Modern allows up to 15.",
        ], issues.Select(i => i.Message));
    }

    [Fact]
    public void Commander_decks_need_exactly_100_cards_including_the_commander()
    {
        var krenko = Entry(1, Card("Krenko, Mob Boss", "Legendary Creature — Goblin Warrior"), Board.Commander);

        Assert.Empty(DeckRules.Check("commander", [krenko, Entry(99, Land())]));
        Assert.Equal(DeckIssueKind.DeckSize, Assert.Single(DeckRules.Check("commander", [krenko, Entry(98, Land())])).Kind);
        Assert.Equal(DeckIssueKind.DeckSize, Assert.Single(DeckRules.Check("commander", [krenko, Entry(100, Land())])).Kind);
    }

    [Fact]
    public void Commander_cards_must_fit_the_commanders_color_identity()
    {
        var krenko = Entry(1, Card("Krenko, Mob Boss", "Legendary Creature — Goblin Warrior", colors: ["R"]), Board.Commander);

        var issues = DeckRules.Check("commander",
        [
            krenko,
            Entry(1, Card("Lightning Bolt", colors: ["R"])),
            Entry(1, Card("Counterspell", manaCost: "{U}{U}", colors: ["U"])),
            Entry(1, Card("Sol Ring", "Artifact", colors: [])),
            // Colorless on the face but with a red activated ability: identity decides, not colors.
            Entry(1, Card("Rakdos Keyrune", "Artifact", colors: [], colorIdentity: ["B", "R"])),
            Entry(95, Land()),
        ]);

        Assert.Equal(
        [
            (DeckIssueKind.ColorIdentity, "Counterspell is blue, outside the commander's colors (red).", "Counterspell"),
            (DeckIssueKind.ColorIdentity, "Rakdos Keyrune is black, outside the commander's colors (red).", "Rakdos Keyrune"),
        ], issues.Select(i => (i.Kind, i.Message, i.CardName)));
    }

    [Fact]
    public void Partner_commanders_combine_their_colors_and_constructed_decks_skip_the_check()
    {
        var partners = new[]
        {
            Entry(1, Card("Tymna the Weaver", "Legendary Creature — Human Cleric", colors: ["W", "B"]), Board.Commander),
            Entry(1, Card("Thrasios, Triton Hero", "Legendary Creature — Merfolk Wizard", colors: ["G", "U"]), Board.Commander),
        };

        Assert.Empty(DeckRules.Check("commander", [.. partners, Entry(1, Card("Counterspell", colors: ["U"])), Entry(97, Land())]));
        Assert.Empty(DeckRules.Check("modern", SixtyCards(Entry(4, Card("Counterspell", colors: ["U"])))));
    }

    [Fact]
    public void Commander_decks_need_a_commander()
    {
        var issue = Assert.Single(DeckRules.Check("commander", [Entry(100, Land())]));

        Assert.Equal(DeckIssueKind.Commander, issue.Kind);
    }

    [Fact]
    public void More_than_four_copies_is_too_many_across_printings_and_boards()
    {
        var bolt = Card("Lightning Bolt");
        var otherPrinting = Card("Lightning Bolt");

        var issue = Assert.Single(DeckRules.Check("modern", SixtyCards(
            Entry(3, bolt),
            Entry(1, otherPrinting),
            Entry(1, bolt, Board.Side))));

        Assert.Equal(DeckIssueKind.TooManyCopies, issue.Kind);
        Assert.Equal("5 copies of Lightning Bolt; Modern allows 4.", issue.Message);
        Assert.Equal("Lightning Bolt", issue.CardName);
    }

    [Fact]
    public void Commander_is_singleton_except_basic_lands()
    {
        var issues = DeckRules.Check("commander",
        [
            Entry(1, Card("Krenko, Mob Boss", "Legendary Creature — Goblin Warrior"), Board.Commander),
            Entry(2, Card("Sol Ring", "Artifact")),
            Entry(30, Land("Snow-Covered Mountain", "Basic Snow Land — Mountain")),
            Entry(67, Land()),
        ]);

        Assert.Equal("2 copies of Sol Ring; Commander allows 1.", Assert.Single(issues).Message);
    }

    [Theory]
    [InlineData("A deck can have any number of cards named Relentless Rats.", 30, false)]
    [InlineData("A deck can have up to seven cards named Seven Dwarves.", 7, false)]
    [InlineData("A deck can have up to seven cards named Seven Dwarves.", 8, true)]
    [InlineData("A deck can have up to nine cards named Nazgûl.", 9, false)]
    public void Cards_that_say_how_many_copies_are_allowed_override_the_limit(string oracle, int copies, bool tooMany)
    {
        var issues = DeckRules.Check("modern", SixtyCards(Entry(copies, Card("Special", "Creature — Rat", oracleText: oracle))));

        Assert.Equal(tooMany, issues.Any(i => i.Kind == DeckIssueKind.TooManyCopies));
    }

    [Fact]
    public void Restricted_cards_allow_one_copy()
    {
        var restricted = new Dictionary<string, string> { ["vintage"] = "restricted" };

        Assert.Empty(DeckRules.Check("vintage", SixtyCards(Entry(1, Card("Sol Ring", legalities: restricted)))));

        var issue = Assert.Single(DeckRules.Check("vintage", SixtyCards(Entry(2, Card("Sol Ring", legalities: restricted)))));
        Assert.Equal(DeckIssueKind.Restricted, issue.Kind);
        Assert.Equal("2 copies of Sol Ring; it's restricted to 1 in Vintage.", issue.Message);
    }

    [Fact]
    public void Cards_without_scryfall_data_are_flagged()
    {
        var issue = Assert.Single(DeckRules.Check("modern", SixtyCards(Entry(1, null, name: "Mystery Card"))));

        Assert.Equal(DeckIssueKind.UnknownCard, issue.Kind);
        Assert.Equal("Mystery Card", issue.CardName);
    }

    [Theory]
    [InlineData("commander", 100, 1, true)]
    [InlineData("paupercommander", 100, 1, true)]
    [InlineData("oathbreaker", 60, 1, true)]
    [InlineData("modern", 60, 4, false)]
    [InlineData("pauper", 60, 4, false)]
    public void RulesFor_knows_each_formats_size_and_copy_limit(string format, int size, int copies, bool usesCommander)
    {
        var rules = DeckRules.RulesFor(format);

        Assert.Equal(size, rules.MinDeckSize);
        Assert.Equal(copies, rules.MaxCopies);
        Assert.Equal(usesCommander, rules.UsesCommander);
    }
}
