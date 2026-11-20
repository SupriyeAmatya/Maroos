using System.Globalization;

namespace Maroos.Core;

/// <summary>Money is always <c>long</c> cents in Maroos; this formats and parses it.</summary>
public static class Money
{
    public static string FormatUsd(long? cents) =>
        cents is { } c
            ? (c < 0 ? "-" : "") + "$" + (Math.Abs(c) / 100).ToString("N0", CultureInfo.InvariantCulture) + "." + (Math.Abs(c) % 100).ToString("00", CultureInfo.InvariantCulture)
            : "—";

    /// <summary>For form inputs: "12.5" (no symbol, no thousands separator).</summary>
    public static string FormatInput(long cents) =>
        (cents / 100m).ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>
    /// Parses what someone types as a dollar amount — "5", "5.5", "$1,250.00" — into cents.
    /// Fails on negatives, more than two decimal places, or anything that isn't a number.
    /// </summary>
    public static bool TryParseUsd(string? text, out long cents)
    {
        cents = 0;
        var clean = text?.Trim().TrimStart('$').Replace(",", "").Trim();
        if (string.IsNullOrEmpty(clean)
            || !decimal.TryParse(clean, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var dollars)
            || decimal.Round(dollars, 2) != dollars
            || dollars > long.MaxValue / 100m)
            return false;

        cents = (long)(dollars * 100m);
        return true;
    }
}
