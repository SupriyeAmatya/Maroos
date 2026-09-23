using System.Text.RegularExpressions;

namespace Maroos.Core.Decks;

/// <summary>One parsed line of a text deck list, e.g. <c>4 Lightning Bolt</c> or <c>1 Sol Ring (C21) 263</c>.</summary>
public sealed record DeckListLine(int Quantity, string Name, string? SetCode, string? CollectorNumber, Board Board);

public sealed record DeckListParseResult(IReadOnlyList<DeckListLine> Lines, IReadOnlyList<string> Unreadable);

/// <summary>
/// Parses the plain-text deck format Moxfield, Arena, MTGO and most sites use: one card per line,
/// optional <c>x</c> after the count, optional <c>(SET) number</c>, and header lines that switch boards.
/// </summary>
public static partial class DeckListParser
{
    [GeneratedRegex(@"^(\d+)x?\s+(.+?)(?:\s+\((\w+)\)(?:\s+(\S+))?)?$", RegexOptions.IgnoreCase)]
    private static partial Regex CardLine();

    // Moxfield export markers after the collector number: *F* foil, *E* etched.
    [GeneratedRegex(@"\s+\*[A-Z]+\*$")]
    private static partial Regex FinishMarker();

    private static readonly Dictionary<string, Board?> Headers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["deck"] = Board.Main,
        ["main"] = Board.Main,
        ["mainboard"] = Board.Main,
        ["main deck"] = Board.Main,
        ["sideboard"] = Board.Side,
        ["side"] = Board.Side,
        ["companion"] = Board.Side,
        ["commander"] = Board.Commander,
        ["commanders"] = Board.Commander,
        // Lines under these headers aren't part of the deck.
        ["maybeboard"] = null,
        ["considering"] = null,
        ["tokens"] = null,
    };

    public static DeckListParseResult Parse(string? text)
    {
        var lines = new List<DeckListLine>();
        var unreadable = new List<string>();
        Board? board = Board.Main;

        foreach (var raw in (text ?? "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("//") || line.StartsWith('#'))
                continue;

            if (Headers.TryGetValue(line.TrimEnd(':').Trim(), out var header))
            {
                board = header;
                continue;
            }

            if (board is not { } currentBoard)
                continue;

            line = FinishMarker().Replace(line, "");
            var match = CardLine().Match(line);
            if (!match.Success)
            {
                // "Sol Ring" with no count means one copy.
                if (!char.IsDigit(line[0]))
                    lines.Add(new DeckListLine(1, line, null, null, currentBoard));
                else
                    unreadable.Add(raw.Trim());
                continue;
            }

            var quantity = int.TryParse(match.Groups[1].Value, out var q) ? q : 0;
            if (quantity is < 1 or > DeckCard.MaxQuantity)
            {
                unreadable.Add(raw.Trim());
                continue;
            }

            lines.Add(new DeckListLine(
                quantity,
                match.Groups[2].Value.Trim(),
                match.Groups[3].Success ? match.Groups[3].Value.ToLowerInvariant() : null,
                match.Groups[4].Success ? match.Groups[4].Value : null,
                currentBoard));
        }

        return new DeckListParseResult(lines, unreadable);
    }
}
