namespace Maroos.Core.Decks;

/// <summary>
/// A practice draw from a deck's main board: shuffle, deal 7, draw more, or mulligan. Commander and sideboard
/// cards are left out. Nothing is saved; the page keeps one of these while its Sample hand panel is open.
/// </summary>
public sealed class SampleHand
{
    public const int OpeningHandSize = 7;

    private readonly IReadOnlyList<DeckEntry> _deck;
    private readonly Random _random;
    private readonly List<DeckEntry> _hand = [];
    private readonly Queue<DeckEntry> _library = new();

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
