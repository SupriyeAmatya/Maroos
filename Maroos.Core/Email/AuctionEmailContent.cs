namespace Maroos.Core.Email;

/// <param name="AuctionUrl">Absolute link to the auction page.</param>
/// <param name="SettingsUrl">Absolute link to account settings, where auction emails can be turned off.</param>
public sealed record AuctionEmailInfo(string CardName, string SetName, string AuctionUrl, string SettingsUrl);

/// <summary>The auction emails. Card and display names are escaped in the HTML part.</summary>
public static class AuctionEmailContent
{
    private const string Footer = "You get these emails because you bid on or listed this card. Turn them off in your account settings:";

    public static EmailContent Outbid(AuctionEmailInfo info, long highBidCents, long minimumNextBidCents, string timeLeft) => Build(info,
        $"You've been outbid on {info.CardName}",
        [
            $"Someone bid {Money.FormatUsd(highBidCents)} on {info.CardName} ({info.SetName}), so you're no longer the highest bidder.",
            $"To win it, bid at least {Money.FormatUsd(minimumNextBidCents)}. Time left: {timeLeft}.",
        ]);

    public static EmailContent Won(AuctionEmailInfo info, long priceCents, string sellerName, string sellerEmail) => Build(info,
        $"You won {info.CardName}",
        [
            $"You won the auction for {info.CardName} ({info.SetName}) at {Money.FormatUsd(priceCents)}.",
            $"Contact the seller, {sellerName}, at {sellerEmail} to arrange payment and shipping. Maroos doesn't handle payment.",
        ]);

    public static EmailContent Sold(AuctionEmailInfo info, long priceCents, string winnerName, string winnerEmail) => Build(info,
        $"Your {info.CardName} sold for {Money.FormatUsd(priceCents)}",
        [
            $"Your auction for {info.CardName} ({info.SetName}) has ended. {winnerName} won it at {Money.FormatUsd(priceCents)}.",
            $"They'll contact you to arrange payment and shipping. You can also reach them at {winnerEmail}.",
        ]);

    public static EmailContent Unsold(AuctionEmailInfo info) => Build(info,
        $"Your auction for {info.CardName} ended with no bids",
        [
            $"Your auction for {info.CardName} ({info.SetName}) ended without any bids.",
            "You can list it again whenever you like.",
        ]);

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
