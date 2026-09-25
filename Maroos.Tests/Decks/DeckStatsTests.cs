// =====================================================================================================
// FILE: Maroos.Tests/Decks/DeckStatsTests.cs
// PURPOSE: DeckStats (Maroos.Core): mana curve (0-6, 7+; lands excluded), average mana value, colors and
//   pips, types, total price with foils and unpriced cards.
// Each test's name says what it proves; the '// TEST:' line above each one repeats it in plain words.
// =====================================================================================================

using Maroos.Core.Decks;
using Maroos.Core.Scryfall;
using static Maroos.Tests.Decks.TestCards;

namespace Maroos.Tests.Decks;

public class DeckStatsTests
{
    // TEST: Curve counts non land cards by mana value with 7 and up together.
    [Fact]
    public void Curve_counts_non_land_cards_by_mana_value_with_7_and_up_together()
    {
        var stats = DeckStats.Compute(
        [
            Entry(4, Card("Lightning Bolt", cmc: 1)),
            Entry(2, Card("Counterspell", cmc: 2, manaCost: "{U}{U}", colors: ["U"])),
            Entry(1, Card("Ornithopter", "Artifact Creature — Thopter", cmc: 0, manaCost: "{0}", colors: [])),
            Entry(1, Card("Emrakul, the Aeons Torn", "Legendary Creature — Eldrazi", cmc: 15, manaCost: "{15}", colors: [])),
            Entry(3, Card("Craterhoof Behemoth", "Creature — Beast", cmc: 8, manaCost: "{5}{G}{G}{G}", colors: ["G"])),
            Entry(1, Card("Little Girl", "Creature — Human Child", cmc: 0.5m, manaCost: "{H/W}", colors: ["W"])),
            Entry(20, Land()),
        ]);

        // 0: Ornithopter + Little Girl (½ rounds down); 1: Bolt x4; 2: Counterspell x2; 7+: Emrakul + Craterhoof x3.
        Assert.Equal([2, 4, 2, 0, 0, 0, 0, 4], Stats(stats.ManaCurve));
        Assert.Equal(20, stats.LandCount);
        Assert.Equal(12, stats.NonLandCount);
    }

    // TEST: Curve and types skip the sideboard but include the commander.
    [Fact]
    public void Curve_and_types_skip_the_sideboard_but_include_the_commander()
    {
        var stats = DeckStats.Compute(
        [
            Entry(1, Card("Krenko, Mob Boss", "Legendary Creature — Goblin Warrior", cmc: 4), Board.Commander),
            Entry(1, Card("Lightning Bolt", cmc: 1)),
            Entry(3, Card("Pyroblast", cmc: 1), Board.Side),
        ]);

        Assert.Equal([0, 1, 0, 0, 1, 0, 0, 0], Stats(stats.ManaCurve));
        Assert.Equal([KeyValuePair.Create(CardTypes.Creature, 1), KeyValuePair.Create(CardTypes.Instant, 1)], stats.Types);
    }

    // TEST: Types follow the deck display order.
    [Fact]
    public void Types_follow_the_deck_display_order()
    {
        var stats = DeckStats.Compute(
        [
            Entry(20, Land()),
            Entry(4, Card("Lightning Bolt")),
            Entry(4, Card("Goblin Guide", "Creature — Goblin Scout")),
            Entry(1, Card("Sol Ring", "Artifact", manaCost: "{1}", colors: [])),
        ]);

        Assert.Equal(
            [(CardTypes.Creature, 4), (CardTypes.Instant, 4), (CardTypes.Artifact, 1), (CardTypes.Land, 20)],
            stats.Types.Select(kv => (kv.Key, kv.Value)));
    }

    // TEST: Colors count cards per color and mana symbols.
    [Fact]
    public void Colors_count_cards_per_color_and_mana_symbols()
    {
        var stats = DeckStats.Compute(
        [
            Entry(4, Card("Lightning Bolt", manaCost: "{R}", colors: ["R"])),
            Entry(2, Card("Lightning Helix", manaCost: "{R}{W}", colors: ["R", "W"])),
            Entry(1, Card("Boros Guildgate Hybrid", manaCost: "{R/W}{R/W}", colors: ["R", "W"])),
            Entry(1, Card("Spectral Procession", manaCost: "{2/W}{2/W}{2/W}", colors: ["W"])),
            Entry(1, Card("Kozilek's Return", manaCost: "{2}{C}", colors: [])),
            Entry(20, Land()),
        ]);

        Assert.Equal(
        [
            new ColorStat("W", Cards: 4, Pips: 2 + 2 + 3),
            new ColorStat("R", Cards: 7, Pips: 4 + 2 + 2),
            new ColorStat("C", Cards: 1, Pips: 1),
        ], stats.Colors);
    }

    // TEST: Double faced cards use their front face colors and cost.
    [Fact]
    public void Double_faced_cards_use_their_front_face_colors_and_cost()
    {
        var delver = ScryfallClient.ParseCard(Scryfall.Fixtures.Read("card-delver-dfc.json"));

        var stats = DeckStats.Compute([Entry(4, delver)]);

        Assert.Equal([new ColorStat("U", 4, 4)], stats.Colors);
        Assert.Equal([0, 4, 0, 0, 0, 0, 0, 0], Stats(stats.ManaCurve));
    }

    // TEST: Total price sums every board and counts cards without a price.
    [Fact]
    public void Total_price_sums_every_board_and_counts_cards_without_a_price()
    {
        var stats = DeckStats.Compute(
        [
            Entry(4, Card("Lightning Bolt", usd: "0.72")),
            Entry(1, Card("Sol Ring", usd: null, usdFoil: "3.50")),
            Entry(2, Card("Pyroblast", usd: "1.25"), Board.Side),
            Entry(1, Card("Promo Only", usd: null)),
            Entry(1, null, name: "Deleted Card"),
        ]);

        Assert.Equal(4 * 72 + 350 + 2 * 125, stats.TotalPriceCents);
        Assert.Equal(2, stats.UnpricedCount);
    }

    // TEST: Average mana value ignores lands.
    [Fact]
    public void Average_mana_value_ignores_lands()
    {
        var stats = DeckStats.Compute([Entry(2, Card("A", cmc: 1)), Entry(2, Card("B", cmc: 4)), Entry(20, Land())]);

        Assert.Equal(2.5m, stats.AverageManaValue);
    }

    // TEST: An empty deck has empty stats.
    [Fact]
    public void An_empty_deck_has_empty_stats()
    {
        var stats = DeckStats.Compute([]);

        Assert.All(stats.ManaCurve, count => Assert.Equal(0, count));
        Assert.Empty(stats.Colors);
        Assert.Empty(stats.Types);
        Assert.Equal(0, stats.TotalPriceCents);
        Assert.Equal(0m, stats.AverageManaValue);
    }

    // Helper: Stats.
    private static int[] Stats(IReadOnlyList<int> curve) => [.. curve];
}
