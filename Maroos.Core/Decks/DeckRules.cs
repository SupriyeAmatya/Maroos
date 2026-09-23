using System.Text.RegularExpressions;

namespace Maroos.Core.Decks;

public enum DeckIssueKind
{
    DeckSize,
    SideboardSize,
    Commander,
    Banned,
    NotLegal,
    Restricted,
    TooManyCopies,
    UnknownCard,
}

/// <param name="CardName">The card the issue is about, or null for deck-level issues (size, commander).</param>
public sealed record DeckIssue(DeckIssueKind Kind, string Message, string? CardName = null)
{
    /// <summary>Short label for a card row, e.g. "Banned".</summary>
    public string Label => Kind switch
    {
        DeckIssueKind.Banned => "Banned",
        DeckIssueKind.NotLegal => "Not legal",
        DeckIssueKind.Restricted => "Restricted",
        DeckIssueKind.TooManyCopies => "Too many",
        DeckIssueKind.UnknownCard => "Unknown",
        _ => "Issue",
    };
}

/// <param name="MinDeckSize">Fewest cards allowed (main deck plus commander zone).</param>
/// <param name="ExactDeckSize">When set, the deck must be exactly this size (Commander-style formats).</param>
/// <param name="MaxCopies">Copies allowed per card name, except basic lands and "any number" cards.</param>
/// <param name="MaxSideboard">Largest sideboard allowed, or null when the format has no sideboard rule.</param>
/// <param name="UsesCommander">Whether the deck needs a card in the commander zone.</param>
public sealed record FormatRules(int MinDeckSize, int? ExactDeckSize, int MaxCopies, int? MaxSideboard, bool UsesCommander)
{
    public static readonly FormatRules Constructed = new(60, null, 4, 15, false);
    public static readonly FormatRules Commander100 = new(100, 100, 1, null, true);
    public static readonly FormatRules Commander60 = new(60, 60, 1, null, true);
}

/// <summary>Checks a deck against its format: card legality, deck and sideboard size, and copy limits.</summary>
public static partial class DeckRules
{
    public static FormatRules RulesFor(string format) => format switch
    {
        "commander" or "duel" or "paupercommander" or "predh" or "brawl" => FormatRules.Commander100,
        "oathbreaker" or "standardbrawl" => FormatRules.Commander60,
        _ => FormatRules.Constructed,
    };

    [GeneratedRegex(@"A deck can have up to (\w+) cards named", RegexOptions.IgnoreCase)]
    private static partial Regex UpToCopies();

    public static IReadOnlyList<DeckIssue> Check(string format, IEnumerable<DeckEntry> entries)
    {
        var rules = RulesFor(format);
        var formatName = DeckFormats.DisplayName(format);
        var cards = entries.Where(e => e.Line.Quantity > 0).ToList();
        var issues = new List<DeckIssue>();

        var deckSize = cards.Where(e => e.Line.Board != Board.Side).Sum(e => e.Line.Quantity);
        var sideboard = cards.Where(e => e.Line.Board == Board.Side).Sum(e => e.Line.Quantity);
        var commanders = cards.Where(e => e.Line.Board == Board.Commander).Sum(e => e.Line.Quantity);

        if (rules.ExactDeckSize is { } exact && deckSize != exact)
            issues.Add(new(DeckIssueKind.DeckSize, $"The deck has {Cards(deckSize)} including the commander; {formatName} needs exactly {exact}."));
        else if (deckSize < rules.MinDeckSize)
            issues.Add(new(DeckIssueKind.DeckSize, $"The main deck has {Cards(deckSize)}; {formatName} needs at least {rules.MinDeckSize}."));

        if (rules.MaxSideboard is { } maxSide && sideboard > maxSide)
            issues.Add(new(DeckIssueKind.SideboardSize, $"The sideboard has {Cards(sideboard)}; {formatName} allows up to {maxSide}."));

        if (rules.UsesCommander && commanders == 0)
            issues.Add(new(DeckIssueKind.Commander, $"Choose a commander: {formatName} decks need one in the commander zone."));
        else if (rules.UsesCommander && commanders > 2)
            issues.Add(new(DeckIssueKind.Commander, $"The commander zone has {Cards(commanders)}; {formatName} allows one, or two with partner."));

        // Copies and legality are per card name: different printings of a card are the same card.
        foreach (var group in cards.GroupBy(e => e.Line.CardName, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var name = group.Key;
            var copies = group.Sum(e => e.Line.Quantity);
            var card = group.Select(e => e.Card).FirstOrDefault(c => c is not null);

            if (card is null)
            {
                issues.Add(new(DeckIssueKind.UnknownCard, $"Couldn't check {name}: Scryfall has no data for it.", name));
                continue;
            }

            var legality = card.Legalities.GetValueOrDefault(format);
            if (legality == "banned")
                issues.Add(new(DeckIssueKind.Banned, $"{name} is banned in {formatName}.", name));
            else if (legality == "not_legal")
                issues.Add(new(DeckIssueKind.NotLegal, $"{name} isn't legal in {formatName}.", name));
            else if (legality == "restricted" && copies > 1)
                issues.Add(new(DeckIssueKind.Restricted, $"{Copies(copies)} of {name}; it's restricted to 1 in {formatName}.", name));

            var allowed = AllowedCopies(card.TypeLine, card.OracleText ?? card.CardFaces?.FirstOrDefault()?.OracleText, rules.MaxCopies);
            if (copies > allowed && legality != "restricted")
                issues.Add(new(DeckIssueKind.TooManyCopies, $"{Copies(copies)} of {name}; {formatName} allows {allowed}.", name));
        }

        return issues;
    }

    /// <summary>
    /// Copy limit for one card: basic lands and "A deck can have any number of cards named …" are unlimited;
    /// "A deck can have up to seven cards named …" allows seven.
    /// </summary>
    public static int AllowedCopies(string? typeLine, string? oracleText, int formatLimit)
    {
        var front = typeLine?.Split(" // ")[0] ?? "";
        if (front.Contains("Basic") && front.Contains("Land"))
            return int.MaxValue;
        if (oracleText?.Contains("A deck can have any number of cards named", StringComparison.OrdinalIgnoreCase) == true)
            return int.MaxValue;
        if (oracleText is not null && UpToCopies().Match(oracleText) is { Success: true } match && NumberWord(match.Groups[1].Value) is { } upTo)
            return Math.Max(upTo, formatLimit);
        return formatLimit;
    }

    private static int? NumberWord(string word) => word.ToLowerInvariant() switch
    {
        "two" => 2, "three" => 3, "four" => 4, "five" => 5, "six" => 6,
        "seven" => 7, "eight" => 8, "nine" => 9, "ten" => 10,
        _ => int.TryParse(word, out var n) ? n : null,
    };

    private static string Cards(int count) => count == 1 ? "1 card" : $"{count} cards";

    private static string Copies(int count) => count == 1 ? "1 copy" : $"{count} copies";
}
