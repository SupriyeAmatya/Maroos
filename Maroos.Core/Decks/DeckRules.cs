// =====================================================================================================
// FILE: Maroos.Core/Decks/DeckRules.cs
// PURPOSE: Checks a deck against its format's rules and produces a list of problems ("issues").
// The deck page shows them as red warnings, flags the affected card rows, and shows a green
// "Legal in <format>" badge when the list is empty.
//
// RULES CHECKED:
//   * Deck size       — constructed formats: at least 60 main-deck cards;
//                       Commander-style formats: EXACTLY 100 (or 60) including the commander.
//   * Sideboard size  — constructed formats: at most 15.
//   * Commander       — commander formats need 1 commander (2 allowed with "partner").
//   * Legality        — each card's Scryfall legality for this format: banned / not legal / restricted.
//   * Copies          — at most 4 of a card (1 in singleton formats), counted across printings and boards;
//                       basic lands and "A deck can have any number of cards named ..." are unlimited.
//   * Color identity  — in commander formats every card must fit the commander's colors.
// =====================================================================================================

// Used to read "up to seven cards named ..." from card text.
using System.Text.RegularExpressions;

namespace Maroos.Core.Decks;

// DeckIssueKind: the category of a problem (used for labels and tests).
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
    ColorIdentity,
}

/// <param name="CardName">The card the issue is about, or null for deck-level issues (size, commander).</param>
// DeckIssue: one problem, with a full sentence for the warning list (Message) and, for card problems,
// the card's name so the matching row can be flagged.
public sealed record DeckIssue(DeckIssueKind Kind, string Message, string? CardName = null)
{
    /// <summary>Short label for a card row, e.g. "Banned".</summary>
    // Label: the small red badge text shown next to a flagged card.
    public string Label => Kind switch
    {
        DeckIssueKind.Banned => "Banned",
        DeckIssueKind.NotLegal => "Not legal",
        DeckIssueKind.Restricted => "Restricted",
        DeckIssueKind.TooManyCopies => "Too many",
        DeckIssueKind.UnknownCard => "Unknown",
        DeckIssueKind.ColorIdentity => "Off-color",
        _ => "Issue",
    };
}

/// <param name="MinDeckSize">Fewest cards allowed (main deck plus commander zone).</param>
/// <param name="ExactDeckSize">When set, the deck must be exactly this size (Commander-style formats).</param>
/// <param name="MaxCopies">Copies allowed per card name, except basic lands and "any number" cards.</param>
/// <param name="MaxSideboard">Largest sideboard allowed, or null when the format has no sideboard rule.</param>
/// <param name="UsesCommander">Whether the deck needs a card in the commander zone.</param>
// FormatRules: the numbers that differ between formats.
public sealed record FormatRules(int MinDeckSize, int? ExactDeckSize, int MaxCopies, int? MaxSideboard, bool UsesCommander)
{
    // Constructed: Modern, Legacy, Standard... — 60+ cards, 4 copies, 15-card sideboard, no commander.
    public static readonly FormatRules Constructed = new(60, null, 4, 15, false);
    // Commander100: Commander, Duel Commander, Pauper Commander, PreDH, Brawl — exactly 100, singleton.
    public static readonly FormatRules Commander100 = new(100, 100, 1, null, true);
    // Commander60: Oathbreaker and Standard Brawl — exactly 60, singleton.
    public static readonly FormatRules Commander60 = new(60, 60, 1, null, true);
}

/// <summary>Checks a deck against its format: card legality, deck and sideboard size, and copy limits.</summary>
public static partial class DeckRules
{
    // RulesFor: format key -> its rule numbers. `or` in a switch arm matches any of the listed values.
    public static FormatRules RulesFor(string format) => format switch
    {
        "commander" or "duel" or "paupercommander" or "predh" or "brawl" => FormatRules.Commander100,
        "oathbreaker" or "standardbrawl" => FormatRules.Commander60,
        _ => FormatRules.Constructed,
    };

    // UpToCopies: finds "A deck can have up to seven cards named ..." and captures the number word ("seven").
    [GeneratedRegex(@"A deck can have up to (\w+) cards named", RegexOptions.IgnoreCase)]
    private static partial Regex UpToCopies();

    // Check: format + deck entries -> list of issues (empty list = the deck is legal).
    public static IReadOnlyList<DeckIssue> Check(string format, IEnumerable<DeckEntry> entries)
    {
        // The rule numbers and readable name for this format.
        var rules = RulesFor(format);
        var formatName = DeckFormats.DisplayName(format);
        // Only lines with at least one copy matter.
        var cards = entries.Where(e => e.Line.Quantity > 0).ToList();
        var issues = new List<DeckIssue>();

        // Totals per board. deckSize counts main + commander (not the sideboard).
        var deckSize = cards.Where(e => e.Line.Board != Board.Side).Sum(e => e.Line.Quantity);
        var sideboard = cards.Where(e => e.Line.Board == Board.Side).Sum(e => e.Line.Quantity);
        var commanders = cards.Where(e => e.Line.Board == Board.Commander).Sum(e => e.Line.Quantity);

        // --- Deck size: exact size for commander formats, minimum size for the rest.
        if (rules.ExactDeckSize is { } exact && deckSize != exact)
            issues.Add(new(DeckIssueKind.DeckSize, $"The deck has {Cards(deckSize)} including the commander; {formatName} needs exactly {exact}."));
        else if (deckSize < rules.MinDeckSize)
            issues.Add(new(DeckIssueKind.DeckSize, $"The main deck has {Cards(deckSize)}; {formatName} needs at least {rules.MinDeckSize}."));

        // --- Sideboard size (only formats that have a limit).
        if (rules.MaxSideboard is { } maxSide && sideboard > maxSide)
            issues.Add(new(DeckIssueKind.SideboardSize, $"The sideboard has {Cards(sideboard)}; {formatName} allows up to {maxSide}."));

        // --- Commander count.
        if (rules.UsesCommander && commanders == 0)
            issues.Add(new(DeckIssueKind.Commander, $"Choose a commander: {formatName} decks need one in the commander zone."));
        else if (rules.UsesCommander && commanders > 2)
            issues.Add(new(DeckIssueKind.Commander, $"The commander zone has {Cards(commanders)}; {formatName} allows one, or two with partner."));

        // Commander decks can only use cards whose color identity fits inside the commanders' combined identity.
        // commanderIdentity: every color letter in any commander's color identity (partners combine).
        var commanderIdentity = cards
            .Where(e => e.Line.Board == Board.Commander && e.Card is not null)
            .SelectMany(e => e.Card!.ColorIdentity ?? [])
            .ToHashSet();
        // checkIdentity: only check when it's a commander format, there IS a commander, and we know its colors.
        var checkIdentity = rules.UsesCommander && commanders > 0 && cards.Where(e => e.Line.Board == Board.Commander).All(e => e.Card is not null);

        // Copies and legality are per card name: different printings of a card are the same card.
        // So group all lines by name (ignoring case) and check each name once, alphabetically.
        foreach (var group in cards.GroupBy(e => e.Line.CardName, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            // name: the card name; copies: total copies across printings and boards.
            var name = group.Key;
            var copies = group.Sum(e => e.Line.Quantity);
            // card: any printing's Scryfall data (legality is the same for every printing).
            var card = group.Select(e => e.Card).FirstOrDefault(c => c is not null);

            // No Scryfall data at all: we can't check it, so say so rather than silently passing it.
            if (card is null)
            {
                issues.Add(new(DeckIssueKind.UnknownCard, $"Couldn't check {name}: Scryfall has no data for it.", name));
                continue;
            }

            // --- Legality for this format: "legal", "not_legal", "banned" or "restricted" (null if unknown).
            var legality = card.Legalities.GetValueOrDefault(format);
            if (legality == "banned")
                issues.Add(new(DeckIssueKind.Banned, $"{name} is banned in {formatName}.", name));
            else if (legality == "not_legal")
                issues.Add(new(DeckIssueKind.NotLegal, $"{name} isn't legal in {formatName}.", name));
            // Restricted (Vintage) means "at most one copy".
            else if (legality == "restricted" && copies > 1)
                issues.Add(new(DeckIssueKind.Restricted, $"{Copies(copies)} of {name}; it's restricted to 1 in {formatName}.", name));

            // --- Color identity: for main-deck cards, collect any identity colors the commander doesn't have
            // (`outside`); if there are any, it's an issue. `is { Count: > 0 } outside` both tests and names the list.
            if (checkIdentity && group.Any(e => e.Line.Board == Board.Main)
                && (card.ColorIdentity ?? []).Where(c => !commanderIdentity.Contains(c)).ToList() is { Count: > 0 } outside)
                issues.Add(new(DeckIssueKind.ColorIdentity,
                    $"{name} is {ColorList(outside)}, outside the commander's colors ({ColorList(commanderIdentity)}).", name));

            // --- Copy limit (skipped for restricted cards, which already have their own rule above).
            // Rules text comes from the card, or its front face for double-faced cards.
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
    // AllowedCopies:
    //   typeLine    — to spot basic lands ("Basic Land — Mountain", "Basic Snow Land — Island").
    //   oracleText  — to spot the special "any number" / "up to N" rules text.
    //   formatLimit — the normal limit (4 or 1).
    //   returns the number of copies allowed (int.MaxValue = unlimited).
    public static int AllowedCopies(string? typeLine, string? oracleText, int formatLimit)
    {
        var front = typeLine?.Split(" // ")[0] ?? "";
        if (front.Contains("Basic") && front.Contains("Land"))
            return int.MaxValue;
        if (oracleText?.Contains("A deck can have any number of cards named", StringComparison.OrdinalIgnoreCase) == true)
            return int.MaxValue;
        // "up to seven" -> 7. Math.Max keeps the larger of that and the normal limit.
        if (oracleText is not null && UpToCopies().Match(oracleText) is { Success: true } match && NumberWord(match.Groups[1].Value) is { } upTo)
            return Math.Max(upTo, formatLimit);
        return formatLimit;
    }

    // NumberWord: "seven" -> 7 (also accepts digits like "7"); null if it isn't a number.
    private static int? NumberWord(string word) => word.ToLowerInvariant() switch
    {
        "two" => 2, "three" => 3, "four" => 4, "five" => 5, "six" => 6,
        "seven" => 7, "eight" => 8, "nine" => 9, "ten" => 10,
        _ => int.TryParse(word, out var n) ? n : null,
    };

    // Wubrg: color order for messages.
    private static readonly string[] Wubrg = ["W", "U", "B", "R", "G"];

    // ColorList: color letters -> "white/blue" style text for messages ("colorless" if none).
    private static string ColorList(IEnumerable<string> colors)
    {
        var names = Wubrg.Where(colors.Contains).Select(DeckStats.ColorName).ToList();
        return names.Count == 0 ? "colorless" : string.Join("/", names).ToLowerInvariant();
    }

    // Cards / Copies: correct singular/plural in messages ("1 card", "57 cards", "1 copy", "5 copies").
    private static string Cards(int count) => count == 1 ? "1 card" : $"{count} cards";

    private static string Copies(int count) => count == 1 ? "1 copy" : $"{count} copies";
}
