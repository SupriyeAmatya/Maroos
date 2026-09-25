// =====================================================================================================
// FILE: Maroos.Web/Data/ApplicationUser.cs
// PURPOSE: A Maroos user account. It extends ASP.NET Core Identity's IdentityUser (which already has
// Id, Email, UserName, PasswordHash, lockout fields, 2FA...) with one extra field: DisplayName.
//
// PRIVACY RULE: other players only ever see the display name — never the email. PublicName() is the
// helper every page uses to show a user's name. (The one exception, by design: after an auction ends,
// the winner and seller see each other's email so they can arrange payment.)
// =====================================================================================================

using Microsoft.AspNetCore.Identity;

namespace Maroos.Web.Data;

public class ApplicationUser : IdentityUser
{
    // Display names must be 2–40 characters (also the database column size).
    public const int MinDisplayNameLength = 2;
    public const int MaxDisplayNameLength = 40;

    /// <summary>What other people see (seller name, bidder name). Never show the email instead.</summary>
    // DisplayName: set at registration, editable in Account → Profile, prefilled from GitHub for GitHub sign-ups.
    // Nullable because accounts created before display names existed don't have one.
    public string? DisplayName { get; set; }

    /// <summary>"Email me about my bids and auctions": outbid, won, sold, ended unsold. Account emails always send.</summary>
    // AuctionEmails: the account-settings switch. Auction emails are only queued while it's on.
    public bool AuctionEmails { get; set; } = true;

    /// <summary>Shown when an account has no display name yet (e.g. created before display names existed).</summary>
    public const string FallbackDisplayName = "Maroos player";

    // PublicName: the name to show for a user: their display name, or the neutral fallback.
    public static string PublicName(string? displayName) =>
        string.IsNullOrWhiteSpace(displayName) ? FallbackDisplayName : displayName;

    /// <summary>Trims and collapses spaces; null when the result is too short or too long.</summary>
    // CleanDisplayName: "  Card   Shark " -> "Card Shark". Returns null for names outside 2–40 characters.
    public static string? CleanDisplayName(string? name)
    {
        // Split on spaces (dropping empty pieces), then re-join with single spaces.
        var clean = string.Join(' ', (name ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        // `is >= Min and <= Max` checks the length is inside the allowed range.
        return clean.Length is >= MinDisplayNameLength and <= MaxDisplayNameLength ? clean : null;
    }
}
