namespace Maroos.Core.Email;

/// <summary>What an outgoing email is for.</summary>
public enum EmailKind
{
    ConfirmAccount = 0,
    PasswordReset = 1,
    ConfirmEmailChange = 2,
    Outbid = 3,
    AuctionWon = 4,
    AuctionSold = 5,
    AuctionUnsold = 6,
}
