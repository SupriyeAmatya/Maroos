// =====================================================================================================
// FILE: Maroos.Core/Email/EmailKind.cs
// PURPOSE: The seven kinds of email Maroos sends. Stored as a number in the OutgoingEmails table, so the
// numbers must never change once used.
// =====================================================================================================

namespace Maroos.Core.Email;

/// <summary>What an outgoing email is for.</summary>
public enum EmailKind
{
    // Account emails (queued by the web app's IdentityEmailSender):
    ConfirmAccount = 0,
    PasswordReset = 1,
    ConfirmEmailChange = 2,
    // Auction emails (only sent to people with ApplicationUser.AuctionEmails on):
    Outbid = 3,
    AuctionWon = 4,
    AuctionSold = 5,
    AuctionUnsold = 6,
}
