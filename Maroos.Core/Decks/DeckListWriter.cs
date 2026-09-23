using System.Text;

namespace Maroos.Core.Decks;

/// <summary>Writes a deck as text that <see cref="DeckListParser"/> (and Moxfield, Arena, MTGO) can read back.</summary>
public static class DeckListWriter
{
    public static string Write(IEnumerable<DeckCard> cards, bool includePrintings = false)
    {
        var byBoard = cards
            .Where(c => c.Quantity > 0)
            .GroupBy(c => c.Board)
            .ToDictionary(g => g.Key, g => g.OrderBy(c => c.CardName, StringComparer.OrdinalIgnoreCase).ToList());

        var text = new StringBuilder();
        var hasCommander = byBoard.ContainsKey(Board.Commander);

        if (hasCommander)
        {
            text.AppendLine("Commander");
            AppendLines(text, byBoard[Board.Commander], includePrintings);
            text.AppendLine();
            text.AppendLine("Deck");
        }

        if (byBoard.TryGetValue(Board.Main, out var main))
            AppendLines(text, main, includePrintings);

        if (byBoard.TryGetValue(Board.Side, out var side))
        {
            if (text.Length > 0)
                text.AppendLine();
            text.AppendLine("Sideboard");
            AppendLines(text, side, includePrintings);
        }

        return text.ToString().TrimEnd();
    }

    public static string FormatLine(DeckCard card, bool includePrinting) =>
        includePrinting && card.SetCode is { } set && card.CollectorNumber is { } number
            ? $"{card.Quantity} {card.CardName} ({set.ToUpperInvariant()}) {number}"
            : $"{card.Quantity} {card.CardName}";

    private static void AppendLines(StringBuilder text, IEnumerable<DeckCard> cards, bool includePrintings)
    {
        foreach (var card in cards)
            text.AppendLine(FormatLine(card, includePrintings));
    }
}
