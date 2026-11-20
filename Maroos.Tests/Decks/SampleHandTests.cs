using Maroos.Core.Decks;
using static Maroos.Tests.Decks.TestCards;

namespace Maroos.Tests.Decks;

public class SampleHandTests
{
    private static readonly DeckEntry Bolt = Entry(4, Card("Lightning Bolt"));
    private static readonly DeckEntry Mountain = Entry(56, Land());
    private static readonly DeckEntry Commander = Entry(1, Card("Krenko, Mob Boss", "Legendary Creature — Goblin Warrior"), Board.Commander);
    private static readonly DeckEntry Sideboard = Entry(15, Card("Pyroblast"), Board.Side);

    private static SampleHand Deal(params DeckEntry[] entries)
    {
        var hand = new SampleHand(entries, new Random(1234));
        hand.NewHand();
        return hand;
    }

    [Fact]
    public void A_new_hand_is_seven_cards_from_the_main_deck()
    {
        var hand = Deal(Bolt, Mountain, Commander, Sideboard);

        Assert.Equal(7, hand.Hand.Count);
        Assert.Equal(53, hand.LibraryCount);
        Assert.Equal(0, hand.Mulligans);
        Assert.All(hand.Hand, card => Assert.Equal(Board.Main, card.Line.Board));
    }

    [Fact]
    public void Every_copy_is_in_the_library_but_never_more()
    {
        var hand = Deal(Bolt, Mountain);
        while (hand.Draw()) { }

        Assert.Equal(60, hand.Hand.Count);
        Assert.Equal(4, hand.Hand.Count(c => c.Line.CardName == "Lightning Bolt"));
        Assert.Equal(56, hand.Hand.Count(c => c.Line.CardName == "Mountain"));
    }

    [Fact]
    public void Draw_takes_the_next_card_until_the_library_is_empty()
    {
        var hand = Deal(Entry(8, Card("Lightning Bolt")));

        Assert.True(hand.Draw());
        Assert.Equal(8, hand.Hand.Count);
        Assert.Equal(0, hand.LibraryCount);

        Assert.False(hand.Draw());
        Assert.Equal(8, hand.Hand.Count);
    }

    [Fact]
    public void Mulligan_deals_seven_again_and_counts_up_until_a_new_hand()
    {
        var hand = Deal(Bolt, Mountain);
        hand.Draw();

        hand.Mulligan();
        hand.Mulligan();

        Assert.Equal(7, hand.Hand.Count);
        Assert.Equal(53, hand.LibraryCount);
        Assert.Equal(2, hand.Mulligans);

        hand.NewHand();
        Assert.Equal(0, hand.Mulligans);
        Assert.Equal(7, hand.Hand.Count);
    }

    [Fact]
    public void Shuffles_differ_between_hands()
    {
        var hand = Deal(Enumerable.Range(1, 40).Select(i => Entry(1, Card($"Card {i}"))).ToArray());
        var first = hand.Hand.Select(c => c.Line.CardName).ToList();

        hand.NewHand();

        Assert.NotEqual(first, hand.Hand.Select(c => c.Line.CardName).ToList());
    }

    [Fact]
    public void A_small_deck_deals_what_it_has()
    {
        var hand = Deal(Entry(5, Card("Lightning Bolt")), Commander);

        Assert.Equal(5, hand.Hand.Count);
        Assert.Equal(0, hand.LibraryCount);
        Assert.Equal(5, hand.DeckSize);
    }

    [Fact]
    public void A_deck_without_main_cards_deals_nothing()
    {
        var hand = Deal(Commander, Sideboard);

        Assert.Empty(hand.Hand);
        Assert.Equal(0, hand.DeckSize);
        Assert.False(hand.Draw());
    }
}
