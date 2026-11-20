using Maroos.Core.Scryfall;

namespace Maroos.Core.Decks;

/// <param name="Color">A WUBRG letter, or <c>C</c> for colorless.</param>
/// <param name="Cards">Non-land cards of this color (a multicolor card counts for each of its colors).</param>
/// <param name="Pips">Mana symbols of this color in those cards' costs (hybrid counts for both halves).</param>
public sealed record ColorStat(string Color, int Cards, int Pips)
{
    public string Name => DeckStats.ColorName(Color);
}

/// <summary>
/// Numbers for the deck stats panel. Curve, colors and types cover the cards you play (main deck and
/// commander); the price covers every board.
/// </summary>
public sealed class DeckStats
{
    /// <summary>Mana value buckets: 0 through 6, then 7 and up.</summary>
    public static readonly IReadOnlyList<string> CurveLabels = ["0", "1", "2", "3", "4", "5", "6", "7+"];

    private static readonly string[] ColorOrder = ["W", "U", "B", "R", "G"];

    /// <summary>Non-land card counts per mana value bucket (see <see cref="CurveLabels"/>).</summary>
    public required IReadOnlyList<int> ManaCurve { get; init; }

    /// <summary>WUBRG in order, then colorless; only colors that appear.</summary>
    public required IReadOnlyList<ColorStat> Colors { get; init; }

    /// <summary>Card counts by primary type in <see cref="CardTypes.DisplayOrder"/>; only types that appear.</summary>
    public required IReadOnlyList<KeyValuePair<string, int>> Types { get; init; }

    public required int LandCount { get; init; }
    public required int NonLandCount { get; init; }

    /// <summary>Average mana value of non-land cards, or 0 when there are none.</summary>
    public required decimal AverageManaValue { get; init; }

    /// <summary>Sum of quantity × USD price over every board.</summary>
    public required long TotalPriceCents { get; init; }

    /// <summary>Copies with no Scryfall USD price, left out of <see cref="TotalPriceCents"/>.</summary>
    public required int UnpricedCount { get; init; }

    public static DeckStats Compute(IEnumerable<DeckEntry> entries)
    {
        var all = entries.Where(e => e.Line.Quantity > 0).ToList();
        var played = all.Where(e => e.Line.Board != Board.Side).ToList();
        var spells = played.Where(e => !e.IsLand).ToList();

        var curve = new int[CurveLabels.Count];
        foreach (var entry in spells.Where(e => e.Card is not null))
            curve[CurveBucket(entry.Card!.Cmc)] += entry.Line.Quantity;

        var withValue = spells.Where(e => e.Card is not null).ToList();
        var spellCount = withValue.Sum(e => e.Line.Quantity);
        var average = spellCount == 0 ? 0m : decimal.Round(withValue.Sum(e => e.Card!.Cmc * e.Line.Quantity) / spellCount, 2);

        return new DeckStats
        {
            ManaCurve = curve,
            Colors = CountColors(spells),
            Types = CardTypes.DisplayOrder
                .Select(type => KeyValuePair.Create(type, played.Where(e => e.PrimaryType == type).Sum(e => e.Line.Quantity)))
                .Where(kv => kv.Value > 0)
                .ToList(),
            LandCount = played.Where(e => e.IsLand).Sum(e => e.Line.Quantity),
            NonLandCount = spells.Sum(e => e.Line.Quantity),
            AverageManaValue = average,
            TotalPriceCents = all.Sum(e => (e.Card?.DeckPriceCents ?? 0) * e.Line.Quantity),
            UnpricedCount = all.Where(e => e.Card?.DeckPriceCents is null).Sum(e => e.Line.Quantity),
        };
    }

    public static int CurveBucket(decimal manaValue) =>
        Math.Clamp((int)Math.Floor(manaValue), 0, CurveLabels.Count - 1);

    public static string ColorName(string color) => color switch
    {
        "W" => "White",
        "U" => "Blue",
        "B" => "Black",
        "R" => "Red",
        "G" => "Green",
        _ => "Colorless",
    };

    private static List<ColorStat> CountColors(IEnumerable<DeckEntry> spells)
    {
        var cards = ColorOrder.Append("C").ToDictionary(c => c, _ => 0);
        var pips = ColorOrder.Append("C").ToDictionary(c => c, _ => 0);

        foreach (var entry in spells.Where(e => e.Card is not null))
        {
            var quantity = entry.Line.Quantity;
            var colors = entry.Card!.FrontColors.Where(cards.ContainsKey).ToList();
            if (colors.Count == 0)
                cards["C"] += quantity;
            foreach (var color in colors)
                cards[color] += quantity;

            foreach (var symbol in ManaSymbols.Tokenize(entry.Card.FrontManaCost).Where(t => t.IsSymbol).Select(t => t.Text))
            {
                // {W/U} counts for both; {2/W} and {W/P} count for W; {C} is a colorless pip. Generic {3} and {X} aren't pips.
                if (symbol == "C")
                    pips["C"] += quantity;
                foreach (var color in ColorOrder.Where(c => symbol.Split('/').Contains(c)))
                    pips[color] += quantity;
            }
        }

        return ColorOrder.Append("C")
            .Where(c => cards[c] > 0 || pips[c] > 0)
            .Select(c => new ColorStat(c, cards[c], pips[c]))
            .ToList();
    }
}
