// =====================================================================================================
// FILE: Maroos.Core/Auctions/AuctionRules.cs
// PURPOSE: The rules for creating a listing, plus display helpers for conditions and time left.
//   NewAuction                 — what the seller fills in on the "List a card" form
//   AuctionValidationException — thrown when a listing (or cancel) breaks the rules; carries the messages
//   AuctionRules.Validate      — checks a NewAuction and returns every problem at once
//   ConditionLabel/Code        — "Near Mint" / "NM"
//   Countdown / TimeLeft       — "1d 04:13:09" ticking clock / "2d 4h" short form
// =====================================================================================================

namespace Maroos.Core.Auctions;

/// <summary>What a seller asks for when listing a card.</summary>
// NewAuction: the listing request.
//   ScryfallId     — the printing chosen in the form
//   Condition      — NM ... DMG
//   IsFoil         — foil or not
//   StartCents     — starting price in cents
//   IncrementCents — minimum raise per bid in cents
//   DurationDays   — how long it runs (1, 3, 5 or 7)
//   PhotoPath      — the saved photo's URL, if a photo was uploaded
public sealed record NewAuction(
    Guid ScryfallId,
    CardCondition Condition,
    bool IsFoil,
    long StartCents,
    long IncrementCents,
    int DurationDays,
    string? PhotoPath = null);

// AuctionValidationException: an exception that carries a list of human-readable problems.
// Its Message is all of them joined; Errors keeps them separate so the form can list each one.
public sealed class AuctionValidationException(IReadOnlyList<string> errors)
    : Exception(string.Join(" ", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

public static class AuctionRules
{
    // Allowed auction lengths, and the choices offered in the dropdown.
    public const int MinDurationDays = 1;
    public const int MaxDurationDays = 7;
    public static readonly IReadOnlyList<int> DurationChoices = [1, 3, 5, 7];

    /// <summary>$1,000,000: a sanity cap so a typo can't list a card for billions.</summary>
    // 100_000_000 cents = $1,000,000 (underscores are just digit separators for readability).
    public const long MaxPriceCents = 100_000_000;

    // DefaultIncrementCents: the form pre-fills a $1.00 minimum raise.
    public const long DefaultIncrementCents = 100;

    /// <summary>Everything wrong with a listing request; empty when it can be listed.</summary>
    // Validate: collects ALL problems (not just the first) so the seller can fix everything in one go.
    public static IReadOnlyList<string> Validate(NewAuction request)
    {
        var errors = new List<string>();

        // Guid.Empty (all zeros) means no card was picked.
        if (request.ScryfallId == Guid.Empty)
            errors.Add("Pick the card you're selling.");
        // Starting price: above $0 and not absurdly high.
        if (request.StartCents <= 0)
            errors.Add("The starting price must be more than $0.");
        else if (request.StartCents > MaxPriceCents)
            errors.Add($"The starting price can't be more than {Money.FormatUsd(MaxPriceCents)}.");
        // Bid increment: same bounds.
        if (request.IncrementCents <= 0)
            errors.Add("The bid increment must be more than $0.");
        else if (request.IncrementCents > MaxPriceCents)
            errors.Add($"The bid increment can't be more than {Money.FormatUsd(MaxPriceCents)}.");
        // Duration: between 1 and 7 days.
        if (request.DurationDays is < MinDurationDays or > MaxDurationDays)
            errors.Add($"Auctions run between {MinDurationDays} and {MaxDurationDays} days.");
        // Enum.IsDefined rejects made-up numbers (e.g. a tampered form sending condition 99).
        if (!Enum.IsDefined(request.Condition))
            errors.Add("Pick the card's condition.");

        return errors;
    }

    // ConditionLabel: enum -> full name for the listing page.
    public static string ConditionLabel(CardCondition condition) => condition switch
    {
        CardCondition.NearMint => "Near Mint",
        CardCondition.LightlyPlayed => "Lightly Played",
        CardCondition.ModeratelyPlayed => "Moderately Played",
        CardCondition.HeavilyPlayed => "Heavily Played",
        CardCondition.Damaged => "Damaged",
        _ => condition.ToString(),
    };

    // ConditionCode: enum -> short code for auction tiles.
    public static string ConditionCode(CardCondition condition) => condition switch
    {
        CardCondition.NearMint => "NM",
        CardCondition.LightlyPlayed => "LP",
        CardCondition.ModeratelyPlayed => "MP",
        CardCondition.HeavilyPlayed => "HP",
        CardCondition.Damaged => "DMG",
        _ => "?",
    };

    /// <summary>A ticking countdown: "2d 04:13:09", "04:13:09", or "Ended".</summary>
    // Countdown: the live clock on an auction page, updated every second.
    public static string Countdown(DateTime nowUtc, DateTime endsAtUtc)
    {
        // left: time remaining (negative once it's over).
        var left = endsAtUtc - nowUtc;
        if (left <= TimeSpan.Zero)
            return "Ended";
        // Round up so the clock shows 00:00:01 in the final second, not 00:00:00.
        var seconds = (long)Math.Ceiling(left.TotalSeconds);
        // hours (within the day) : minutes : seconds, each as two digits.
        var clock = $"{seconds / 3600 % 24:00}:{seconds / 60 % 60:00}:{seconds % 60:00}";
        // 86400 seconds = 1 day: prefix whole days when there's at least one.
        return seconds >= 86400 ? $"{seconds / 86400}d {clock}" : clock;
    }

    /// <summary>"2d 4h", "3h 12m", "45m", "under a minute" or "Ended".</summary>
    // TimeLeft: the short form used on lists (auction tiles, profile tables), picking the two largest units.
    public static string TimeLeft(DateTime nowUtc, DateTime endsAtUtc)
    {
        var left = endsAtUtc - nowUtc;
        if (left <= TimeSpan.Zero)
            return "Ended";
        if (left.TotalDays >= 1)
            return $"{(int)left.TotalDays}d {left.Hours}h";
        if (left.TotalHours >= 1)
            return $"{(int)left.TotalHours}h {left.Minutes}m";
        if (left.TotalMinutes >= 1)
            return $"{(int)left.TotalMinutes}m";
        return "under a minute";
    }
}
