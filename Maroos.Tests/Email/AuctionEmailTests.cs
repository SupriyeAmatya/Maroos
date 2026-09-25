// =====================================================================================================
// FILE: Maroos.Tests/Email/AuctionEmailTests.cs
// PURPOSE: Outbid, won, sold and no-bids emails, queued by the real services over one database
// (AuctionWorld): who gets what, the bidding war, the on/off switch, and that the dispatcher sends them.
// =====================================================================================================

using Maroos.Core.Email;
using Maroos.Tests.Auctions;

namespace Maroos.Tests.Email;

/// <summary>Outbid, won, sold and unsold emails, queued by the real services over one database.</summary>
public sealed class AuctionEmailTests : IDisposable
{
    private readonly AuctionWorld _world = new();

    public void Dispose() => _world.Dispose();

    // Queued: every queued email of one kind.
    private List<OutgoingEmail> Queued(EmailKind kind) => _world.QueuedEmails().Where(e => e.Kind == kind).ToList();

    // TEST: The previous high bidder is emailed when outbid.
    [Fact]
    public async Task The_previous_high_bidder_is_emailed_when_outbid()
    {
        var seller = _world.AddUser("Seller");
        var alice = _world.AddUser("Alice");
        var bob = _world.AddUser("Bob");
        var auction = await _world.ListAsync(seller); // Sol Ring, starts at $5.00, $1.00 increment

        await _world.Bidding.PlaceBidAsync(auction.Id, alice, 500);
        Assert.Empty(Queued(EmailKind.Outbid)); // the first bid outbids nobody

        await _world.Bidding.PlaceBidAsync(auction.Id, bob, 700);

        var email = Assert.Single(Queued(EmailKind.Outbid));
        Assert.Equal("alice@example.com", email.ToAddress);
        Assert.Equal(alice, email.UserId);
        Assert.Equal(auction.Id, email.AuctionId);
        Assert.Contains("$7.00", email.TextBody);
        Assert.Contains("$8.00", email.TextBody); // minimum next bid
        Assert.Contains($"https://maroos.test/auctions/{auction.Id}", email.TextBody);
    }

    // TEST: In a bidding war each person gets one email each time they are outbid.
    [Fact]
    public async Task In_a_bidding_war_each_person_gets_one_email_each_time_they_are_outbid()
    {
        var seller = _world.AddUser("Seller");
        var alice = _world.AddUser("Alice");
        var bob = _world.AddUser("Bob");
        var carol = _world.AddUser("Carol");
        var auction = await _world.ListAsync(seller);

        await _world.Bidding.PlaceBidAsync(auction.Id, alice, 500);
        await _world.Bidding.PlaceBidAsync(auction.Id, bob, 600);   // alice outbid
        await _world.Bidding.PlaceBidAsync(auction.Id, carol, 700); // bob outbid (alice hears nothing more)
        await _world.Bidding.PlaceBidAsync(auction.Id, alice, 800); // carol outbid

        Assert.Equal(["alice@example.com", "bob@example.com", "carol@example.com"],
            Queued(EmailKind.Outbid).Select(e => e.ToAddress).Order());
    }

    // TEST: People who turned auction emails off are not emailed when outbid.
    [Fact]
    public async Task People_who_turned_auction_emails_off_are_not_emailed_when_outbid()
    {
        var seller = _world.AddUser("Seller");
        var alice = _world.AddUser("Alice", auctionEmails: false);
        var bob = _world.AddUser("Bob");
        var auction = await _world.ListAsync(seller);

        await _world.Bidding.PlaceBidAsync(auction.Id, alice, 500);
        await _world.Bidding.PlaceBidAsync(auction.Id, bob, 600);

        Assert.Empty(Queued(EmailKind.Outbid));
    }

    // TEST: A rejected bid emails nobody.
    [Fact]
    public async Task A_rejected_bid_emails_nobody()
    {
        var seller = _world.AddUser("Seller");
        var alice = _world.AddUser("Alice");
        var bob = _world.AddUser("Bob");
        var auction = await _world.ListAsync(seller);

        await _world.Bidding.PlaceBidAsync(auction.Id, alice, 500);
        var result = await _world.Bidding.PlaceBidAsync(auction.Id, bob, 500); // below $6.00

        Assert.False(result.Accepted);
        Assert.Empty(Queued(EmailKind.Outbid));
    }

    // TEST: Closing a sold auction emails the winner and the seller exactly once.
    [Fact]
    public async Task Closing_a_sold_auction_emails_the_winner_and_the_seller_exactly_once()
    {
        var seller = _world.AddUser("Seller");
        var alice = _world.AddUser("Alice");
        var auction = await _world.ListAsync(seller, days: 1);
        await _world.Bidding.PlaceBidAsync(auction.Id, alice, 900);
        _world.Clock.Advance(TimeSpan.FromDays(1));

        await _world.Auctions.CloseExpiredAsync();
        await _world.Auctions.CloseExpiredAsync(); // already closed: nothing new

        var won = Assert.Single(Queued(EmailKind.AuctionWon));
        Assert.Equal("alice@example.com", won.ToAddress);
        Assert.Contains("$9.00", won.TextBody);
        Assert.Contains("Seller", won.TextBody);
        Assert.Contains("seller@example.com", won.TextBody);

        var sold = Assert.Single(Queued(EmailKind.AuctionSold));
        Assert.Equal("seller@example.com", sold.ToAddress);
        Assert.Equal("Your Sol Ring sold for $9.00", sold.Subject);
        Assert.Contains("Alice", sold.TextBody);
        Assert.Contains("alice@example.com", sold.TextBody);

        Assert.Empty(Queued(EmailKind.AuctionUnsold));
    }

    // TEST: An auction with no bids tells the seller.
    [Fact]
    public async Task An_auction_with_no_bids_tells_the_seller()
    {
        var seller = _world.AddUser("Seller");
        var auction = await _world.ListAsync(seller, days: 1);
        _world.Clock.Advance(TimeSpan.FromDays(1));

        await _world.Auctions.CloseExpiredAsync();

        var unsold = Assert.Single(Queued(EmailKind.AuctionUnsold));
        Assert.Equal("seller@example.com", unsold.ToAddress);
        Assert.Equal(auction.Id, unsold.AuctionId);
        Assert.Empty(Queued(EmailKind.AuctionWon));
        Assert.Empty(Queued(EmailKind.AuctionSold));
    }

    // TEST: Closing emails respect each persons switch.
    [Fact]
    public async Task Closing_emails_respect_each_persons_switch()
    {
        var seller = _world.AddUser("Seller", auctionEmails: false);
        var alice = _world.AddUser("Alice");
        var auction = await _world.ListAsync(seller, days: 1);
        await _world.Bidding.PlaceBidAsync(auction.Id, alice, 900);
        _world.Clock.Advance(TimeSpan.FromDays(1));

        await _world.Auctions.CloseExpiredAsync();

        Assert.Single(Queued(EmailKind.AuctionWon));
        Assert.Empty(Queued(EmailKind.AuctionSold));
    }

    // TEST: Queued auction emails are sent by the dispatcher.
    [Fact]
    public async Task Queued_auction_emails_are_sent_by_the_dispatcher()
    {
        var seller = _world.AddUser("Seller");
        var alice = _world.AddUser("Alice");
        var bob = _world.AddUser("Bob");
        var auction = await _world.ListAsync(seller);
        await _world.Bidding.PlaceBidAsync(auction.Id, alice, 500);
        await _world.Bidding.PlaceBidAsync(auction.Id, bob, 600);

        await _world.EmailDispatcher.SendDueAsync();

        var sent = Assert.Single(_world.EmailTransport.Sent);
        Assert.Equal(EmailKind.Outbid, sent.Kind);
        Assert.Equal("alice@example.com", sent.To);
    }
}
