// =====================================================================================================
// FILE: Maroos.Core/Decks/SampleHand.cs
// PURPOSE: Practice draws for the deck page's "Sample hand" panel.
// PROCESS
//   1. The library holds one entry per copy of each main-board card (4 Lightning Bolt = 4 entries).
//   2. NewHand / Mulligan shuffle the whole deck again and deal 7 (or fewer if the deck is smaller).
//   3. Draw moves the top library card into the hand.
// London mulligan: after N mulligans you keep 7 but put N on the bottom. The panel says so; the player chooses.
// TESTING: the Random is passed in, so tests use a seeded one and get the same shuffle every run.
// =====================================================================================================

namespace Maroos.Core.Decks;

/// <summary>
/// A practice draw from a deck's main board: shuffle, deal 7, draw more, or mulligan. Commander and sideboard
/// cards are left out. Nothing is saved; the page keeps one of these while its Sample hand panel is open.
/// </summary>
public sealed class SampleHand
{
    public const int OpeningHandSize = 7;

    // _deck: every main-board card, one entry per copy (never changes). _hand: cards drawn so far.
    // _library: the shuffled cards still to draw; a Queue, so Draw takes them from the front in order.
    private readonly IReadOnlyList<DeckEntry> _deck;
    private readonly Random _random;
    private readonly List<DeckEntry> _hand = [];
    private readonly Queue<DeckEntry> _library = new();

    // Constructor: keep only the main board (no commander or sideboard) and expand quantities into copies.
    /// <param name="entries">The deck; each main-board entry goes into the library once per copy.</param>
    /// <param name="random">The shuffle's randomness (tests pass a seeded one).</param>
    public SampleHand(IEnumerable<DeckEntry> entries, Random random)
    {
        _deck = entries
            .Where(e => e.Line.Board == Board.Main)
            .SelectMany(e => Enumerable.Repeat(e, e.Line.Quantity))
            .ToList();
        _random = random;
    }

    /// <summary>The cards drawn so far, in the order they were drawn.</summary>
    public IReadOnlyList<DeckEntry> Hand => _hand;

    /// <summary>Cards left to draw.</summary>
    public int LibraryCount => _library.Count;

    /// <summary>Main-deck cards in play: hand plus library.</summary>
    public int DeckSize => _deck.Count;

    /// <summary>Mulligans taken since the last <see cref="NewHand"/>. London mulligan: put this many on the bottom.</summary>
    public int Mulligans { get; private set; }

    /// <summary>Shuffle and deal a fresh opening hand; the mulligan count starts over.</summary>
    public void NewHand()
    {
        Mulligans = 0;
        Deal();
    }

    /// <summary>Shuffle and deal 7 again, counting one more mulligan.</summary>
    public void Mulligan()
    {
        Mulligans++;
        Deal();
    }

    /// <summary>Moves the top card of the library to the hand. False (and nothing happens) when the library is empty.</summary>
    public bool Draw()
    {
        if (!_library.TryDequeue(out var card))
            return false;
        _hand.Add(card);
        return true;
    }

    // Deal: Step 1: shuffle a copy of the whole deck (Random.Shuffle is a Fisher–Yates shuffle, built into .NET).
    // Step 2: empty the hand and refill the library. Step 3: draw up to 7; Draw stops early when the library runs out.
    private void Deal()
    {
        var cards = _deck.ToArray();
        _random.Shuffle(cards);

        _hand.Clear();
        _library.Clear();
        foreach (var card in cards)
            _library.Enqueue(card);
        for (var i = 0; i < OpeningHandSize && Draw(); i++) { }
    }
}
