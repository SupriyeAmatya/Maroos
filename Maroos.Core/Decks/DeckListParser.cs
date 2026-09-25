// =====================================================================================================
// FILE: Maroos.Core/Decks/DeckListParser.cs
// PURPOSE: Reads a pasted deck list (the text format Moxfield, MTG Arena, MTGO and most sites use)
// and turns each line into structured data: how many, which card, which printing, which board.
//
// Lines it understands:
//   4 Lightning Bolt               quantity + name
//   4x Lightning Bolt              "x" after the number is allowed
//   Sol Ring                       no number = 1 copy
//   1 Sol Ring (C21) 263           set code in parentheses + collector number = an exact printing
//   1 Sol Ring (C21) 263 *F*       Moxfield's foil/etched markers are ignored
//   Sideboard / SIDEBOARD:         header lines that switch to another board (see Headers)
//   // comment   or   # comment    ignored
// Lines it can't read (e.g. "0 Mountain", "1000 Island", a bare "4") are collected in Unreadable
// so the import screen can show them to the user instead of silently dropping them.
// =====================================================================================================

using System.Text.RegularExpressions;

namespace Maroos.Core.Decks;

/// <summary>One parsed line of a text deck list, e.g. <c>4 Lightning Bolt</c> or <c>1 Sol Ring (C21) 263</c>.</summary>
// DeckListLine: the result for one line.
//   Quantity        — copies (1–999)
//   Name            — card name as typed
//   SetCode         — lower-case set code if given ("c21"), else null
//   CollectorNumber — collector number if given ("263"), else null
//   Board           — which board the line was under
public sealed record DeckListLine(int Quantity, string Name, string? SetCode, string? CollectorNumber, Board Board);

// DeckListParseResult: all readable lines plus the raw text of any lines that couldn't be read.
public sealed record DeckListParseResult(IReadOnlyList<DeckListLine> Lines, IReadOnlyList<string> Unreadable);

/// <summary>
/// Parses the plain-text deck format Moxfield, Arena, MTGO and most sites use: one card per line,
/// optional <c>x</c> after the count, optional <c>(SET) number</c>, and header lines that switch boards.
/// </summary>
public static partial class DeckListParser
{
    // CardLine: the pattern for one card line. Reading it piece by piece:
    //   ^(\d+)                 start of line, then the quantity (group 1)
    //   x?                     an optional "x" ("4x")
    //   \s+(.+?)               spaces, then the card name (group 2) — "lazy" so it stops before the set part
    //   (?:\s+\((\w+)\)        optionally: spaces, "(", set code (group 3), ")"
    //      (?:\s+(\S+))?)?     ...and optionally spaces + collector number (group 4)
    //   $                      end of line
    // IgnoreCase lets "4X" work too.
    [GeneratedRegex(@"^(\d+)x?\s+(.+?)(?:\s+\((\w+)\)(?:\s+(\S+))?)?$", RegexOptions.IgnoreCase)]
    private static partial Regex CardLine();

    // Moxfield export markers after the collector number: *F* foil, *E* etched.
    // FinishMarker: matches such a marker at the end of a line so it can be removed before parsing.
    [GeneratedRegex(@"\s+\*[A-Z]+\*$")]
    private static partial Regex FinishMarker();

    // Headers: section titles that switch the current board. Matching ignores case and a trailing ":".
    // A null value means "ignore the cards under this header" (they aren't part of the deck).
    private static readonly Dictionary<string, Board?> Headers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["deck"] = Board.Main,
        ["main"] = Board.Main,
        ["mainboard"] = Board.Main,
        ["main deck"] = Board.Main,
        ["sideboard"] = Board.Side,
        ["side"] = Board.Side,
        // A companion sits outside the deck and is listed with the sideboard in Arena exports.
        ["companion"] = Board.Side,
        ["commander"] = Board.Commander,
        ["commanders"] = Board.Commander,
        // Lines under these headers aren't part of the deck.
        ["maybeboard"] = null,
        ["considering"] = null,
        ["tokens"] = null,
    };

    // Parse: the whole deck list text -> parsed lines + unreadable lines.
    // PROCESS for each line of text:
    //   1. trim it; skip blank lines and comments;
    //   2. if it's a header, switch the current board and move on;
    //   3. if the current board is "ignored" (maybeboard...), skip the line;
    //   4. strip a foil marker, then match the card-line pattern;
    //   5. no match: a line without a number is "1 copy of <line>"; anything else is unreadable;
    //   6. reject quantities outside 1–999; otherwise record the line.
    public static DeckListParseResult Parse(string? text)
    {
        // lines: the successfully read card lines.
        var lines = new List<DeckListLine>();
        // unreadable: raw text of lines we couldn't understand.
        var unreadable = new List<string>();
        // board: the board new lines belong to. Starts at Main; null while inside an ignored section.
        Board? board = Board.Main;

        // `text ?? ""` treats a null input like an empty list. Split on '\n' handles Windows "\r\n" too,
        // because Trim() below removes the leftover "\r".
        foreach (var raw in (text ?? "").Split('\n'))
        {
            // Step 1.
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("//") || line.StartsWith('#'))
                continue;

            // Step 2: "SIDEBOARD:" -> "SIDEBOARD" -> looked up case-insensitively.
            if (Headers.TryGetValue(line.TrimEnd(':').Trim(), out var header))
            {
                board = header;
                continue;
            }

            // Step 3: `board is not { } currentBoard` is true when board is null (ignored section);
            // otherwise it copies the value into currentBoard for use below.
            if (board is not { } currentBoard)
                continue;

            // Step 4.
            line = FinishMarker().Replace(line, "");
            var match = CardLine().Match(line);
            if (!match.Success)
            {
                // "Sol Ring" with no count means one copy.
                // Step 5: only lines that don't start with a digit get this treatment; "4" alone is unreadable.
                if (!char.IsDigit(line[0]))
                    lines.Add(new DeckListLine(1, line, null, null, currentBoard));
                else
                    unreadable.Add(raw.Trim());
                continue;
            }

            // Step 6: group 1 is the quantity. int.TryParse fails for absurdly long numbers -> 0 -> rejected.
            var quantity = int.TryParse(match.Groups[1].Value, out var q) ? q : 0;
            // `is < 1 or > MaxQuantity` is C# pattern syntax for "less than 1 OR more than 999".
            if (quantity is < 1 or > DeckCard.MaxQuantity)
            {
                unreadable.Add(raw.Trim());
                continue;
            }

            // Record the line. Set codes are stored in lower case to match Scryfall's codes.
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
