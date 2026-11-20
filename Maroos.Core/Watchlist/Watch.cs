namespace Maroos.Core.Watchlist;

public enum WatchTarget
{
    Deck = 0,
    Auction = 1,
}

/// <summary>A user saving a deck or an auction to their watchlist.</summary>
public class Watch
{
    public string UserId { get; set; } = "";
    public WatchTarget TargetType { get; set; }
    public Guid TargetId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}
