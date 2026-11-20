using Maroos.Core.Email;

namespace Maroos.Tests.Email;

public class EmailRulesTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 5)]
    [InlineData(3, 15)]
    [InlineData(4, 60)]
    [InlineData(5, 180)]
    public void Failed_sends_are_retried_with_growing_delays(int failedAttempts, int minutes) =>
        Assert.Equal(TimeSpan.FromMinutes(minutes), EmailRetry.DelayAfter(failedAttempts));

    [Fact]
    public void Sending_gives_up_after_six_attempts() => Assert.Equal(6, EmailRetry.MaxAttempts);

    [Theory]
    [InlineData("alice", "bob", true, true)]
    [InlineData(null, "bob", true, false)]      // first bid: nobody was outbid
    [InlineData("bob", "bob", true, false)]     // never about your own bid
    [InlineData("alice", "bob", false, false)]  // alice turned auction emails off
    public void Outbid_emails_go_to_the_previous_high_bidder_if_they_want_them(string? previous, string bidder, bool optedIn, bool expected) =>
        Assert.Equal(expected, OutbidRule.ShouldNotify(previous, bidder, optedIn));
}

public class EmailContentTests
{
    private static readonly AuctionEmailInfo SolRing =
        new("Sol Ring", "Commander 2021", "https://maroos.test/auctions/abc", "https://maroos.test/Account/Manage");

    [Fact]
    public void Outbid_email_gives_the_new_price_the_minimum_next_bid_and_the_links()
    {
        var email = AuctionEmailContent.Outbid(SolRing, 1200, 1300, "2d 4h");

        Assert.Equal("You've been outbid on Sol Ring", email.Subject);
        Assert.Contains("$12.00", email.TextBody);
        Assert.Contains("$13.00", email.TextBody);
        Assert.Contains("2d 4h", email.TextBody);
        Assert.Contains("https://maroos.test/auctions/abc", email.TextBody);
        Assert.Contains("https://maroos.test/Account/Manage", email.TextBody);
        Assert.Contains("<a href=\"https://maroos.test/auctions/abc\">", email.HtmlBody);
    }

    [Fact]
    public void Won_email_tells_the_winner_how_to_reach_the_seller()
    {
        var email = AuctionEmailContent.Won(SolRing, 900, "Card Shark", "shark@example.com");

        Assert.Equal("You won Sol Ring", email.Subject);
        Assert.Contains("$9.00", email.TextBody);
        Assert.Contains("Card Shark", email.TextBody);
        Assert.Contains("shark@example.com", email.TextBody);
    }

    [Fact]
    public void Sold_email_tells_the_seller_who_won()
    {
        var email = AuctionEmailContent.Sold(SolRing, 900, "Bob", "bob@example.com");

        Assert.Equal("Your Sol Ring sold for $9.00", email.Subject);
        Assert.Contains("Bob", email.TextBody);
        Assert.Contains("bob@example.com", email.TextBody);
    }

    [Fact]
    public void Unsold_email_says_there_were_no_bids()
    {
        var email = AuctionEmailContent.Unsold(SolRing);

        Assert.Equal("Your auction for Sol Ring ended with no bids", email.Subject);
        Assert.Contains("https://maroos.test/auctions/abc", email.TextBody);
    }

    [Fact]
    public void Names_are_escaped_in_the_html_but_kept_as_is_in_the_text()
    {
        var info = SolRing with { CardName = "<script>alert(1)</script>" };
        var email = AuctionEmailContent.Won(info, 900, "<b>Eve</b>", "eve@example.com");

        Assert.DoesNotContain("<script>", email.HtmlBody);
        Assert.DoesNotContain("<b>Eve</b>", email.HtmlBody);
        Assert.Contains("&lt;script&gt;", email.HtmlBody);
        Assert.Contains("<script>alert(1)</script>", email.TextBody);
    }

    [Fact]
    public void Account_emails_put_the_raw_link_in_the_text_and_an_encoded_link_in_the_html()
    {
        const string link = "https://maroos.test/Account/ConfirmEmail?userId=1&code=abc";
        var email = AccountEmailContent.ConfirmAccount(link);

        Assert.Equal("Confirm your Maroos account", email.Subject);
        Assert.Contains(link, email.TextBody);
        Assert.Contains("href=\"https://maroos.test/Account/ConfirmEmail?userId=1&amp;code=abc\"", email.HtmlBody);
    }

    [Fact]
    public void Each_account_email_has_its_own_subject()
    {
        Assert.Equal("Reset your Maroos password", AccountEmailContent.PasswordReset("https://x/r").Subject);
        Assert.Equal("Confirm your new email address", AccountEmailContent.ConfirmEmailChange("https://x/c").Subject);
        Assert.Contains("123456", AccountEmailContent.PasswordResetCode("123456").TextBody);
    }
}
