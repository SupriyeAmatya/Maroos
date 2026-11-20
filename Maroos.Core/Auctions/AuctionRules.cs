namespace Maroos.Core.Auctions;

/// <summary>What a seller asks for when listing a card.</summary>
public sealed record NewAuction(
    Guid ScryfallId,
    CardCondition Condition,
    bool IsFoil,
    long StartCents,
    long IncrementCents,
    int DurationDays,
    string? PhotoPath = null);

public sealed class AuctionValidationException(IReadOnlyList<string> errors)
    : Exception(string.Join(" ", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

public static class AuctionRules
{
    public const int MinDurationDays = 1;
    public const int MaxDurationDays = 7;
    public static readonly IReadOnlyList<int> DurationChoices = [1, 3, 5, 7];

    /// <summary>$1,000,000: a sanity cap so a typo can't list a card for billions.</summary>
    public const long MaxPriceCents = 100_000_000;

    public const long DefaultIncrementCents = 100;

    /// <summary>Everything wrong with a listing request; empty when it can be listed.</summary>
    public static IReadOnlyList<string> Validate(NewAuction request)
    {
        var errors = new List<string>();

        if (request.ScryfallId == Guid.Empty)
            errors.Add("Pick the card you're selling.");
        if (request.StartCents <= 0)
            errors.Add("The starting price must be more than $0.");
        else if (request.StartCents > MaxPriceCents)
            errors.Add($"The starting price can't be more than {Money.FormatUsd(MaxPriceCents)}.");
        if (request.IncrementCents <= 0)
            errors.Add("The bid increment must be more than $0.");
        else if (request.IncrementCents > MaxPriceCents)
            errors.Add($"The bid increment can't be more than {Money.FormatUsd(MaxPriceCents)}.");
        if (request.DurationDays is < MinDurationDays or > MaxDurationDays)
            errors.Add($"Auctions run between {MinDurationDays} and {MaxDurationDays} days.");
        if (!Enum.IsDefined(request.Condition))
            errors.Add("Pick the card's condition.");

        return errors;
    }

    public static string ConditionLabel(CardCondition condition) => condition switch
    {
        CardCondition.NearMint => "Near Mint",
        CardCondition.LightlyPlayed => "Lightly Played",
        CardCondition.ModeratelyPlayed => "Moderately Played",
        CardCondition.HeavilyPlayed => "Heavily Played",
        CardCondition.Damaged => "Damaged",
        _ => condition.ToString(),
    };

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
    public static string Countdown(DateTime nowUtc, DateTime endsAtUtc)
    {
        var left = endsAtUtc - nowUtc;
        if (left <= TimeSpan.Zero)
            return "Ended";
        // Round up so the clock shows 00:00:01 in the final second, not 00:00:00.
        var seconds = (long)Math.Ceiling(left.TotalSeconds);
        var clock = $"{seconds / 3600 % 24:00}:{seconds / 60 % 60:00}:{seconds % 60:00}";
        return seconds >= 86400 ? $"{seconds / 86400}d {clock}" : clock;
    }

    /// <summary>"2d 4h", "3h 12m", "45m", "under a minute" or "Ended".</summary>
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
