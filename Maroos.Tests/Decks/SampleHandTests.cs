// =====================================================================================================
// FILE: Maroos.Tests/Decks/SampleHandTests.cs
// PURPOSE: SampleHand (Maroos.Core): 7-card hands from the main deck only, Draw, Mulligan, New hand, small decks.
//   Every test uses a seeded Random (new Random(1234)), so the "random" shuffles are the same on every run.
// Each test's name says what it proves; the '// TEST:' line above each one repeats it in plain words.
// =====================================================================================================

using Maroos.Core.Decks;
using static Maroos.Tests.Decks.TestCards;

namespace Maroos.Tests.Decks;

public class SampleHandTests
{
    // Test decks: 4 Bolts + 56 Mountains = 60 main-deck cards, plus a commander and a sideboard that must never be dealt.
    private static readonly DeckEntry Bolt = Entry(4, Card("Lightning Bolt"));
    private static readonly DeckEntry Mountain = Entry(56, Land());
    private static readonly DeckEntry Commander = Entry(1, Card("Krenko, Mob Boss", "Legendary Creature — Goblin Warrior"), Board.Commander);
    private static readonly DeckEntry Sideboard = Entry(15, Card("Pyroblast"), Board.Side);

    // Helper: a sample hand over these entries, already dealt.
    private static SampleHand Deal(params DeckEntry[] entries)
    {
        var hand = new SampleHand(entries, new Random(1234));
        hand.NewHand();
        return hand;
    }

    // TEST: A new hand is seven cards from the main deck.
    [Fact]
    public void A_new_hand_is_seven_cards_from_the_main_deck()
    {
        var hand = Deal(Bolt, Mountain, Commander, Sideboard);

        Assert.Equal(7, hand.Hand.Count);
        Assert.Equal(53, hand.LibraryCount);
        Assert.Equal(0, hand.Mulligans);
        Assert.All(hand.Hand, card => Assert.Equal(Board.Main, card.Line.Board));
    }

    // TEST: Every copy is in the library but never more.
    // Draw the whole library: exactly 4 Bolts and 56 Mountains come out.
    [Fact]
    public void Every_copy_is_in_the_library_but_never_more()
    {
        var hand = Deal(Bolt, Mountain);
        while (hand.Draw()) { }

        Assert.Equal(60, hand.Hand.Count);
        Assert.Equal(4, hand.Hand.Count(c => c.Line.CardName == "Lightning Bolt"));
        Assert.Equal(56, hand.Hand.Count(c => c.Line.CardName == "Mountain"));
    }

    // TEST: Draw takes the next card until the library is empty.
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

    // TEST: Mulligan deals seven again and counts up until a new hand.
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

    // TEST: Shuffles differ between hands.
    // 40 different cards, so two hands in the same order would mean the shuffle isn't working.
    [Fact]
    public void Shuffles_differ_between_hands()
    {
        var hand = Deal(Enumerable.Range(1, 40).Select(i => Entry(1, Card($"Card {i}"))).ToArray());
        var first = hand.Hand.Select(c => c.Line.CardName).ToList();

        hand.NewHand();

        Assert.NotEqual(first, hand.Hand.Select(c => c.Line.CardName).ToList());
    }

    // TEST: A small deck deals what it has.
    [Fact]
    public void A_small_deck_deals_what_it_has()
    {
        var hand = Deal(Entry(5, Card("Lightning Bolt")), Commander);

        Assert.Equal(5, hand.Hand.Count);
        Assert.Equal(0, hand.LibraryCount);
        Assert.Equal(5, hand.DeckSize);
    }

    // TEST: A deck without main cards deals nothing.
    [Fact]
    public void A_deck_without_main_cards_deals_nothing()
    {
        var hand = Deal(Commander, Sideboard);

        Assert.Empty(hand.Hand);
        Assert.Equal(0, hand.DeckSize);
        Assert.False(hand.Draw());
    }
}
