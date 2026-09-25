// =====================================================================================================
// FILE: Maroos.Tests/Scryfall/CardDisplayTests.cs
// PURPOSE: Card display helpers: ManaSymbols tokenizing {2}{R} and symbol URLs, format display names and
//   legality labels.
// Each test's name says what it proves; the '// TEST:' line above each one repeats it in plain words.
// =====================================================================================================

using Maroos.Core;
using Maroos.Core.Scryfall;

namespace Maroos.Tests.Scryfall;

public class CardDisplayTests
{
    // TEST: ToCents parses scryfall price strings.
    [Theory]
    [InlineData("0.72", 72L)]
    [InlineData("3.86", 386L)]
    [InlineData("1234.5", 123450L)]
    [InlineData("0.005", 1L)]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("n/a", null)]
    public void ToCents_parses_scryfall_price_strings(string? price, long? cents) =>
        Assert.Equal(cents, ScryfallPrices.ToCents(price));

    // TEST: FormatUsd formats cents.
    [Theory]
    [InlineData(72L, "$0.72")]
    [InlineData(123450L, "$1,234.50")]
    [InlineData(5L, "$0.05")]
    [InlineData(null, "—")]
    public void FormatUsd_formats_cents(long? cents, string expected) =>
        Assert.Equal(expected, Money.FormatUsd(cents));

    // TEST: Tokenize splits symbols from text.
    [Fact]
    public void Tokenize_splits_symbols_from_text()
    {
        var tokens = ManaSymbols.Tokenize("{2}{W/U}, {T}: Draw a card.").ToList();

        Assert.Equal(
            [new("2", true), new("W/U", true), new(", ", false), new("T", true), new(": Draw a card.", false)],
            tokens);
    }

    // TEST: Tokenize handles empty text.
    [Fact]
    public void Tokenize_handles_empty_text() => Assert.Empty(ManaSymbols.Tokenize(null));

    // TEST: SvgUrl points at scryfall symbol files.
    [Theory]
    [InlineData("R", "R.svg")]
    [InlineData("W/U", "WU.svg")]
    [InlineData("2/W", "2W.svg")]
    public void SvgUrl_points_at_scryfall_symbol_files(string symbol, string file) =>
        Assert.Equal($"https://svgs.scryfall.io/card-symbols/{file}", ManaSymbols.SvgUrl(symbol));

    // TEST: DisplayName names formats.
    [Theory]
    [InlineData("paupercommander", "Pauper Commander")]
    [InlineData("modern", "Modern")]
    [InlineData("competitivebrawl", "Competitive Brawl")]
    [InlineData("someNewFormat", "SomeNewFormat")]
    [InlineData("tlr", "TLR")]
    public void DisplayName_names_formats(string key, string name) => Assert.Equal(name, Formats.DisplayName(key));

    // TEST: LegalityLabel names legalities.
    [Theory]
    [InlineData("legal", "Legal")]
    [InlineData("not_legal", "Not legal")]
    [InlineData("banned", "Banned")]
    [InlineData("restricted", "Restricted")]
    public void LegalityLabel_names_legalities(string key, string label) => Assert.Equal(label, Formats.LegalityLabel(key));
}
