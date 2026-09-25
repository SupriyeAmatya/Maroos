// =====================================================================================================
// FILE: Maroos.Core/Watchlist/Watch.cs
// PURPOSE: The Watches table: which decks and auctions each user has saved with the ♡ Watch button.
// Its database key is (UserId, TargetType, TargetId), so a user can watch each thing only once.
// =====================================================================================================

namespace Maroos.Core.Watchlist;

// WatchTarget: what kind of thing is being watched.
public enum WatchTarget
{
    Deck = 0,
    Auction = 1,
}

/// <summary>A user saving a deck or an auction to their watchlist.</summary>
public class Watch
{
    // UserId: who is watching (deleted with the user).
    public string UserId { get; set; } = "";
    // TargetType + TargetId: the watched deck or auction. There's no database foreign key here because the
    // id can point at either table; DeckService removes watches itself when a deck is deleted.
    public WatchTarget TargetType { get; set; }
    public Guid TargetId { get; set; }
    // CreatedAtUtc: when it was watched; the watchlist shows the most recent first.
    public DateTime CreatedAtUtc { get; set; }
}
