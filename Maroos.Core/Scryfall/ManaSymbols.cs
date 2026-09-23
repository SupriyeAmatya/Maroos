using System.Text.RegularExpressions;

namespace Maroos.Core.Scryfall;

public readonly record struct ManaToken(string Text, bool IsSymbol);

/// <summary>Splits mana costs and oracle text like <c>{2}{R}: Deal 3 damage</c> into text and symbol tokens.</summary>
public static partial class ManaSymbols
{
    [GeneratedRegex(@"(\{[^{}]+\})")]
    private static partial Regex SymbolPattern();

    public static IEnumerable<ManaToken> Tokenize(string? text)
    {
        if (string.IsNullOrEmpty(text))
            yield break;

        foreach (var part in SymbolPattern().Split(text))
        {
            if (part.Length == 0)
                continue;
            yield return part.StartsWith('{') && part.EndsWith('}')
                ? new ManaToken(part[1..^1], IsSymbol: true)
                : new ManaToken(part, IsSymbol: false);
        }
    }

    /// <summary>Scryfall's hosted SVG for a symbol: <c>W/U</c> → <c>WU.svg</c>, <c>T</c> → <c>T.svg</c>.</summary>
    public static string SvgUrl(string symbol) =>
        $"https://svgs.scryfall.io/card-symbols/{Uri.EscapeDataString(symbol.Replace("/", ""))}.svg";
}
