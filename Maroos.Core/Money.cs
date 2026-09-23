using System.Globalization;

namespace Maroos.Core;

/// <summary>Money is always <c>long</c> cents in Maroos; this formats it for display.</summary>
public static class Money
{
    public static string FormatUsd(long? cents) =>
        cents is { } c
            ? (c < 0 ? "-" : "") + "$" + (Math.Abs(c) / 100).ToString("N0", CultureInfo.InvariantCulture) + "." + (Math.Abs(c) % 100).ToString("00", CultureInfo.InvariantCulture)
            : "—";
}
