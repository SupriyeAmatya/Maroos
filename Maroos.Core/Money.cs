// =====================================================================================================
// FILE: Maroos.Core/Money.cs
// PURPOSE: The one place that turns money into text and text into money.
//
// WHY IT EXISTS: Maroos stores every amount of money as a whole number of CENTS in a `long`
// (e.g. $12.50 is stored as 1250). Floating-point types (double/float) can't represent many
// decimal amounts exactly (0.1 + 0.2 != 0.3), which causes rounding bugs with prices and bids.
// Integers never have that problem. This class converts between those cents and what people
// see ("$12.50") or type ("12.50").
//
// USED BY: price displays on card, deck and auction pages; the auction listing form; bidding.
// =====================================================================================================

// CultureInfo lets us format numbers the same way regardless of the server's language settings.
using System.Globalization;

// All shared business code lives under the Maroos.Core namespace.
namespace Maroos.Core;

/// <summary>Money is always <c>long</c> cents in Maroos; this formats and parses it.</summary>
// `static` = a utility class: you never create a Money object, you just call Money.FormatUsd(...).
public static class Money
{
    // FormatUsd: turns cents into a US-dollar string for display.
    //   cents  (long?) — the amount in cents, or null when there is no price at all.
    //   returns "$1,234.50" style text, "-$3.00" for negatives, or "—" (an em dash) when null.
    // How: split cents into whole dollars (cents / 100) and leftover cents (cents % 100),
    // format dollars with thousands separators ("N0") and cents as exactly two digits ("00").
    // InvariantCulture guarantees "." as the decimal point and "," for thousands on every server.
    public static string FormatUsd(long? cents) =>
        // `cents is { } c` means "if cents is not null, call its value c".
        cents is { } c
            ? (c < 0 ? "-" : "") + "$" + (Math.Abs(c) / 100).ToString("N0", CultureInfo.InvariantCulture) + "." + (Math.Abs(c) % 100).ToString("00", CultureInfo.InvariantCulture)
            // No price known: show a dash instead of "$0.00" so nobody mistakes it for free.
            : "—";

    /// <summary>For form inputs: "12.5" (no symbol, no thousands separator).</summary>
    // FormatInput: cents -> text to pre-fill an input box, e.g. 1250 -> "12.50".
    // Dividing by 100m (the "m" makes it a decimal, not a double) keeps it exact.
    public static string FormatInput(long cents) =>
        (cents / 100m).ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>
    /// Parses what someone types as a dollar amount — "5", "5.5", "$1,250.00" — into cents.
    /// Fails on negatives, more than two decimal places, or anything that isn't a number.
    /// </summary>
    // TryParseUsd: text typed by a person -> cents.
    //   text   — what they typed (may be null or messy).
    //   cents  — OUT parameter: receives the amount when parsing succeeds (0 otherwise).
    //   returns true when the text was a valid amount, false otherwise.
    // The "Try" pattern (return bool + out value) avoids throwing exceptions for normal bad input.
    public static bool TryParseUsd(string? text, out long cents)
    {
        // An out parameter must always be assigned; start at 0 in case we return false.
        cents = 0;
        // Clean the input: trim spaces, drop a leading "$", and remove thousands commas ("1,250" -> "1250").
        var clean = text?.Trim().TrimStart('$').Replace(",", "").Trim();
        // Reject when any of these is true:
        if (string.IsNullOrEmpty(clean)
            // ...it isn't a plain decimal number (AllowDecimalPoint: no minus sign, no exponent like "1e3"),
            || !decimal.TryParse(clean, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var dollars)
            // ...it has more than two decimal places (e.g. "5.001" — there is no such thing as a tenth of a cent),
            || decimal.Round(dollars, 2) != dollars
            // ...or it's so large that converting to cents would overflow a long.
            || dollars > long.MaxValue / 100m)
            return false;

        // Safe to convert: multiply by 100 to get cents. The value is exact because it has at most 2 decimals.
        cents = (long)(dollars * 100m);
        return true;
    }
}
