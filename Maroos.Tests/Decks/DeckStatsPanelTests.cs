using System.Globalization;
using Bunit;
using Maroos.Core.Decks;
using Maroos.Web.Components.Decks;
using static Maroos.Tests.Decks.TestCards;

namespace Maroos.Tests.Decks;

public sealed class DeckStatsPanelTests : BunitContext
{
    private static DeckStats SampleStats() => DeckStats.Compute(
    [
        Entry(4, Card("Lightning Bolt", cmc: 1, usd: "0.72")),
        Entry(2, Card("Counterspell", cmc: 2, manaCost: "{U}{U}", colors: ["U"], usd: "1.00")),
        Entry(3, Card("Craterhoof Behemoth", "Creature — Beast", cmc: 8, manaCost: "{5}{G}{G}{G}", colors: ["G"], usd: "20.00")),
        Entry(20, Land()),
    ]);

    [Fact]
    public void Renders_one_bar_group_per_mana_value_with_the_right_counts()
    {
        var panel = Render<DeckStatsPanel>(p => p.Add(x => x.Stats, SampleStats()));

        var bars = panel.FindAll(".curve-bar");
        Assert.Equal(["0", "1", "2", "3", "4", "5", "6", "7+"], bars.Select(b => b.GetAttribute("data-mana-value")));
        Assert.Equal(["0", "4", "2", "0", "0", "0", "0", "3"], bars.Select(b => b.GetAttribute("data-count")));

        // Only non-empty buckets draw a bar, and the tallest bucket gets the tallest bar.
        Assert.Equal(3, panel.FindAll(".curve-fill").Count);
        Assert.Equal("Mana value 7+: 3 cards", bars[7].QuerySelector("title")!.TextContent);
    }

    [Fact]
    public void Bar_heights_are_proportional_to_the_counts()
    {
        var panel = Render<DeckStatsPanel>(p => p.Add(x => x.Stats, SampleStats()));

        double Height(int bucket) => BarHeight(panel.FindAll(".curve-bar")[bucket].QuerySelector(".curve-fill")!.GetAttribute("d")!);

        Assert.Equal(Height(1) / 2, Height(2), precision: 1);
        Assert.Equal(Height(1) * 3 / 4, Height(7), precision: 1);
    }

    [Fact]
    public void Shows_colors_types_and_the_total_price()
    {
        var panel = Render<DeckStatsPanel>(p => p.Add(x => x.Stats, SampleStats()));

        Assert.Equal(["U", "R", "G"], panel.FindAll(".deck-colors tbody tr").Select(r => r.GetAttribute("data-color")));
        Assert.Equal(["Creature", "Instant", "Land"], panel.FindAll(".deck-types tr").Select(r => r.GetAttribute("data-type")));
        // 4 × $0.72 + 2 × $1.00 + 3 × $20.00 + 20 Mountains × $0.10
        Assert.Equal("$66.88", panel.Find(".deck-price").TextContent);
    }

    [Fact]
    public void Chart_coordinates_use_invariant_numbers_in_any_culture()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
        try
        {
            var panel = Render<DeckStatsPanel>(p => p.Add(x => x.Stats, SampleStats()));

            Assert.DoesNotContain(",", panel.Find(".curve-hit").GetAttribute("x"));
            Assert.DoesNotContain(",", panel.Find(".curve-hit").GetAttribute("width"));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    /// <summary>Height of a bar path "M x,base V y Q x,top ...": baseline minus the y of the first Q end point.</summary>
    private static double BarHeight(string path)
    {
        var numbers = System.Text.RegularExpressions.Regex.Matches(path, @"-?\d+(\.\d+)?").Select(m => double.Parse(m.Value, CultureInfo.InvariantCulture)).ToList();
        var baseline = numbers[1];
        var top = numbers[4];
        return baseline - top;
    }
}
