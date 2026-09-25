// =====================================================================================================
// FILE: Maroos.Core/Email/AuctionEmailContent.cs
// PURPOSE: The text of the four auction emails: outbid, won, sold, and ended with no bids.
// Prices are long cents, formatted with Money.FormatUsd. Every email links to the auction and says where to
// turn auction emails off.
// =====================================================================================================

namespace Maroos.Core.Email;

/// <param name="AuctionUrl">Absolute link to the auction page.</param>
/// <param name="SettingsUrl">Absolute link to account settings, where auction emails can be turned off.</param>
// AuctionEmailInfo: what every auction email needs about its auction (built by the web app's EmailOutbox.InfoFor).
public sealed record AuctionEmailInfo(string CardName, string SetName, string AuctionUrl, string SettingsUrl);

/// <summary>The auction emails. Card and display names are escaped in the HTML part.</summary>
public static class AuctionEmailContent
{
    // Footer: why the reader gets this email, and where to turn these emails off.
    private const string Footer = "You get these emails because you bid on or listed this card. Turn them off in your account settings:";

    // Outbid: to the previous high bidder. The new high bid, the minimum to take the lead back, and the time left.
    public static EmailContent Outbid(AuctionEmailInfo info, long highBidCents, long minimumNextBidCents, string timeLeft) => Build(info,
        $"You've been outbid on {info.CardName}",
        [
            $"Someone bid {Money.FormatUsd(highBidCents)} on {info.CardName} ({info.SetName}), so you're no longer the highest bidder.",
            $"To win it, bid at least {Money.FormatUsd(minimumNextBidCents)}. Time left: {timeLeft}.",
        ]);

    // Won: to the winner, with the seller's name and email (the same contact the auction page shows the winner).
    public static EmailContent Won(AuctionEmailInfo info, long priceCents, string sellerName, string sellerEmail) => Build(info,
        $"You won {info.CardName}",
        [
            $"You won the auction for {info.CardName} ({info.SetName}) at {Money.FormatUsd(priceCents)}.",
            $"Contact the seller, {sellerName}, at {sellerEmail} to arrange payment and shipping. Maroos doesn't handle payment.",
        ]);

    // Sold: to the seller, with the winner's name and email (the same contact the auction page shows the seller).
    public static EmailContent Sold(AuctionEmailInfo info, long priceCents, string winnerName, string winnerEmail) => Build(info,
        $"Your {info.CardName} sold for {Money.FormatUsd(priceCents)}",
        [
            $"Your auction for {info.CardName} ({info.SetName}) has ended. {winnerName} won it at {Money.FormatUsd(priceCents)}.",
            $"They'll contact you to arrange payment and shipping. You can also reach them at {winnerEmail}.",
        ]);

    // Unsold: to the seller when nobody bid.
    public static EmailContent Unsold(AuctionEmailInfo info) => Build(info,
        $"Your auction for {info.CardName} ended with no bids",
        [
            $"Your auction for {info.CardName} ({info.SetName}) ended without any bids.",
            "You can list it again whenever you like.",
        ]);

    // Build: the shared layout. Text: the paragraphs, the auction link, then the footer. HTML: the same, each
    // paragraph encoded.
    private static EmailContent Build(AuctionEmailInfo info, string subject, string[] paragraphs)
    {
        var text = string.Join("\n\n", paragraphs)
                   + $"\n\nView the auction: {info.AuctionUrl}\n\n--\n{Footer} {info.SettingsUrl}";
        var html = string.Concat(paragraphs.Select(p => $"<p>{EmailHtml.Encode(p)}</p>"))
                   + $"<p><a href=\"{EmailHtml.Encode(info.AuctionUrl)}\">View the auction</a></p>"
                   + $"<hr><p style=\"color:#6c757d;font-size:13px\">{EmailHtml.Encode(Footer)} "
                   + $"<a href=\"{EmailHtml.Encode(info.SettingsUrl)}\">Account settings</a></p>";
        return new EmailContent(subject, text, EmailHtml.Document(html));
    }
}
