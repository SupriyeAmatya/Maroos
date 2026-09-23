using Microsoft.AspNetCore.Identity;

namespace Maroos.Web.Data;

public class ApplicationUser : IdentityUser
{
    public const int MinDisplayNameLength = 2;
    public const int MaxDisplayNameLength = 40;

    /// <summary>What other people see (seller name, bidder name). Never show the email instead.</summary>
    public string? DisplayName { get; set; }

    /// <summary>Shown when an account has no display name yet (e.g. created before display names existed).</summary>
    public const string FallbackDisplayName = "Maroos player";

    public static string PublicName(string? displayName) =>
        string.IsNullOrWhiteSpace(displayName) ? FallbackDisplayName : displayName;

    /// <summary>Trims and collapses spaces; null when the result is too short or too long.</summary>
    public static string? CleanDisplayName(string? name)
    {
        var clean = string.Join(' ', (name ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return clean.Length is >= MinDisplayNameLength and <= MaxDisplayNameLength ? clean : null;
    }
}
