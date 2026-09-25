// =====================================================================================================
// FILE: Maroos.Core/Scryfall/ManaSymbols.cs
// PURPOSE: Splits card text like "{2}{R}: Deal 3 damage" into pieces so symbols can be drawn as icons.
//
// Scryfall writes symbols in curly braces: {R} red mana, {2} two generic mana, {T} tap, {W/U} hybrid.
// Tokenize() cuts the text into a sequence of plain-text pieces and symbol pieces; the ManaText
// component then shows each symbol as Scryfall's SVG icon (SvgUrl) and each text piece as text.
// Deck stats also use Tokenize() to count colored mana symbols ("pips").
// =====================================================================================================

// Regular expressions (text patterns).
using System.Text.RegularExpressions;

namespace Maroos.Core.Scryfall;

// ManaToken: one piece of tokenized text.
//   Text     — the symbol without braces ("R", "W/U") or a plain text fragment (": Deal 3 damage").
//   IsSymbol — true for a {symbol}, false for plain text.
// `readonly record struct` = a small, immutable value type (cheap to create, compared by value).
public readonly record struct ManaToken(string Text, bool IsSymbol);

/// <summary>Splits mana costs and oracle text like <c>{2}{R}: Deal 3 damage</c> into text and symbol tokens.</summary>
// `partial` is required because the [GeneratedRegex] below is completed by a source generator at build time.
public static partial class ManaSymbols
{
    // SymbolPattern: matches one {...} symbol: "{", then one or more characters that aren't braces, then "}".
    // The outer parentheses make it a "capturing group", which makes Regex.Split KEEP the symbols
    // in the output instead of throwing them away.
    // [GeneratedRegex] compiles the pattern at build time (faster than parsing it at runtime).
    [GeneratedRegex(@"(\{[^{}]+\})")]
    private static partial Regex SymbolPattern();

    // Tokenize: text -> sequence of ManaTokens.
    // Example: "{2}{R}: Deal" -> ("2", symbol), ("R", symbol), (": Deal", text).
    // `IEnumerable` + `yield return` produces the tokens one at a time, lazily, as the caller loops.
    public static IEnumerable<ManaToken> Tokenize(string? text)
    {
        // Nothing to split: produce no tokens at all.
        if (string.IsNullOrEmpty(text))
            yield break;

        // Split on symbols (kept, thanks to the capturing group).
        foreach (var part in SymbolPattern().Split(text))
        {
            // Split produces empty strings between adjacent symbols ("{2}{R}"); skip them.
            if (part.Length == 0)
                continue;
            // A part wrapped in braces is a symbol: strip the braces with the range [1..^1]
            // (from the 2nd character to the one before last). Anything else is plain text.
            yield return part.StartsWith('{') && part.EndsWith('}')
                ? new ManaToken(part[1..^1], IsSymbol: true)
                : new ManaToken(part, IsSymbol: false);
        }
    }

    /// <summary>Scryfall's hosted SVG for a symbol: <c>W/U</c> → <c>WU.svg</c>, <c>T</c> → <c>T.svg</c>.</summary>
    // SvgUrl: the web address of Scryfall's icon for a symbol. Scryfall names hybrid symbols without the slash,
    // so "W/U" becomes "WU.svg". EscapeDataString keeps the URL valid for any unusual symbol.
    public static string SvgUrl(string symbol) =>
        $"https://svgs.scryfall.io/card-symbols/{Uri.EscapeDataString(symbol.Replace("/", ""))}.svg";
}
