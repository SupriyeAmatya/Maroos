// =====================================================================================================
// FILE: Maroos.Core/Decks/DeckListWriter.cs
// PURPOSE: The opposite of DeckListParser — writes a deck out as text for the "Export" panel,
// in the same format Moxfield, Arena and MTGO read. Output looks like:
//
//   Commander            <- only for decks with a commander
//   1 Krenko, Mob Boss
//
//   Deck
//   4 Lightning Bolt
//   20 Mountain
//
//   Sideboard
//   2 Pyroblast
//
// With includePrintings = true each line also carries the printing: "1 Sol Ring (C21) 263".
// Tests check that exporting and re-importing gives back the same deck ("round trip").
// =====================================================================================================

// StringBuilder builds a long string efficiently, piece by piece.
using System.Text;

namespace Maroos.Core.Decks;

/// <summary>Writes a deck as text that <see cref="DeckListParser"/> (and Moxfield, Arena, MTGO) can read back.</summary>
public static class DeckListWriter
{
    // Write: deck lines -> export text.
    //   cards            — the deck's lines.
    //   includePrintings — add "(SET) number" to each line?
    public static string Write(IEnumerable<DeckCard> cards, bool includePrintings = false)
    {
        // byBoard: board -> that board's lines sorted A–Z by name (lines with 0 copies are skipped).
        var byBoard = cards
            .Where(c => c.Quantity > 0)
            .GroupBy(c => c.Board)
            .ToDictionary(g => g.Key, g => g.OrderBy(c => c.CardName, StringComparer.OrdinalIgnoreCase).ToList());

        // text: the output being built.
        var text = new StringBuilder();
        // hasCommander: Commander decks use Arena's layout with explicit "Commander" and "Deck" headers.
        var hasCommander = byBoard.ContainsKey(Board.Commander);

        if (hasCommander)
        {
            text.AppendLine("Commander");
            AppendLines(text, byBoard[Board.Commander], includePrintings);
            text.AppendLine();
            text.AppendLine("Deck");
        }

        // Main deck lines (no header needed for ordinary decks: unmarked lines mean "main").
        if (byBoard.TryGetValue(Board.Main, out var main))
            AppendLines(text, main, includePrintings);

        // Sideboard, separated by a blank line and a header.
        if (byBoard.TryGetValue(Board.Side, out var side))
        {
            if (text.Length > 0)
                text.AppendLine();
            text.AppendLine("Sideboard");
            AppendLines(text, side, includePrintings);
        }

        // TrimEnd removes the final line break so the text ends cleanly.
        return text.ToString().TrimEnd();
    }

    // FormatLine: one deck line -> "4 Lightning Bolt", or "1 Sol Ring (C21) 263" when a printing is
    // requested AND known (both set code and collector number present).
    public static string FormatLine(DeckCard card, bool includePrinting) =>
        includePrinting && card.SetCode is { } set && card.CollectorNumber is { } number
            ? $"{card.Quantity} {card.CardName} ({set.ToUpperInvariant()}) {number}"
            : $"{card.Quantity} {card.CardName}";

    // AppendLines: writes each card as its own line.
    private static void AppendLines(StringBuilder text, IEnumerable<DeckCard> cards, bool includePrintings)
    {
        foreach (var card in cards)
            text.AppendLine(FormatLine(card, includePrintings));
    }
}
