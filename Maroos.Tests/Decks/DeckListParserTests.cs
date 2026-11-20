using Maroos.Core.Decks;

namespace Maroos.Tests.Decks;

public class DeckListParserTests
{
    private static DeckListLine Single(string text) => Assert.Single(DeckListParser.Parse(text).Lines);

    [Fact]
    public void Parses_plain_lines()
    {
        Assert.Equal(new DeckListLine(4, "Lightning Bolt", null, null, Board.Main), Single("4 Lightning Bolt"));
    }

    [Theory]
    [InlineData("4x Lightning Bolt")]
    [InlineData("4X Lightning Bolt")]
    [InlineData("  4   Lightning Bolt  ")]
    public void Accepts_the_x_form_and_extra_spaces(string text)
    {
        var line = Single(text);
        Assert.Equal(4, line.Quantity);
        Assert.Equal("Lightning Bolt", line.Name);
    }

    [Fact]
    public void Parses_set_code_and_collector_number()
    {
        Assert.Equal(new DeckListLine(1, "Sol Ring", "c21", "263", Board.Main), Single("1 Sol Ring (C21) 263"));
    }

    [Theory]
    [InlineData("1 Sol Ring (PLST) C21-263", "plst", "C21-263")]
    [InlineData("1 Fire // Ice (MH2) 290", "mh2", "290")]
    [InlineData("1 Sol Ring (C21) 263 *F*", "c21", "263")]
    [InlineData("1 Sol Ring (2XM) 270★", "2xm", "270★")]
    public void Handles_unusual_collector_numbers_split_cards_and_finish_markers(string text, string set, string number)
    {
        var line = Single(text);
        Assert.Equal(set, line.SetCode);
        Assert.Equal(number, line.CollectorNumber);
        Assert.DoesNotContain("(", line.Name);
    }

    [Fact]
    public void Keeps_split_card_names_whole()
    {
        Assert.Equal("Fire // Ice", Single("2 Fire // Ice").Name);
    }

    [Fact]
    public void Set_code_without_collector_number_is_kept()
    {
        var line = Single("1 Sol Ring (C21)");
        Assert.Equal("Sol Ring", line.Name);
        Assert.Equal("c21", line.SetCode);
        Assert.Null(line.CollectorNumber);
    }

    [Fact]
    public void A_line_without_a_count_means_one_copy()
    {
        Assert.Equal(new DeckListLine(1, "Sol Ring", null, null, Board.Main), Single("Sol Ring"));
    }

    [Theory]
    [InlineData("Sideboard")]
    [InlineData("SIDEBOARD:")]
    [InlineData("sideboard")]
    public void Sideboard_header_switches_board(string header)
    {
        var lines = DeckListParser.Parse($"4 Lightning Bolt\n{header}\n2 Pyroblast").Lines;

        Assert.Equal(Board.Main, lines[0].Board);
        Assert.Equal(new DeckListLine(2, "Pyroblast", null, null, Board.Side), lines[1]);
    }

    [Fact]
    public void Arena_style_headers_set_commander_main_and_sideboard()
    {
        var lines = DeckListParser.Parse("Commander\n1 Krenko, Mob Boss\n\nDeck\n1 Sol Ring\n\nSideboard\n1 Pyroblast").Lines;

        Assert.Equal([Board.Commander, Board.Main, Board.Side], lines.Select(l => l.Board));
    }

    [Fact]
    public void Skips_blank_lines_and_comments_and_handles_windows_line_endings()
    {
        var lines = DeckListParser.Parse("// Burn\r\n\r\n4 Lightning Bolt\r\n# note\r\n   \r\n20 Mountain\r\n").Lines;

        Assert.Equal(["Lightning Bolt", "Mountain"], lines.Select(l => l.Name));
    }

    [Fact]
    public void Ignores_cards_under_maybeboard_until_the_next_board_header()
    {
        var lines = DeckListParser.Parse("4 Lightning Bolt\nMaybeboard\n1 Fireblast\nSideboard\n1 Pyroblast").Lines;

        Assert.Equal(["Lightning Bolt", "Pyroblast"], lines.Select(l => l.Name));
    }

    [Theory]
    [InlineData("0 Lightning Bolt")]
    [InlineData("1000 Mountain")]
    [InlineData("4")]
    public void Reports_lines_it_cannot_use(string text)
    {
        var result = DeckListParser.Parse(text);

        Assert.Empty(result.Lines);
        Assert.Equal([text], result.Unreadable);
    }

    [Fact]
    public void Empty_or_null_text_gives_nothing()
    {
        Assert.Empty(DeckListParser.Parse(null).Lines);
        Assert.Empty(DeckListParser.Parse("").Lines);
    }
}
