namespace Maroos.Core.Decks;

public enum Board
{
    Main = 0,
    Side = 1,
    Commander = 2,
}

public class Deck
{
    public const int MaxNameLength = 100;

    public Guid Id { get; set; } = Guid.NewGuid();
    public string OwnerId { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>A Scryfall legality key, e.g. <c>modern</c> or <c>commander</c>.</summary>
    public string Format { get; set; } = DeckFormats.Default;

    public bool IsPublic { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public List<DeckCard> Cards { get; set; } = [];
}

/// <summary>One card (a specific Scryfall printing) on one board of a deck.</summary>
public class DeckCard
{
    public const int MaxQuantity = 999;

    public Guid DeckId { get; set; }
    public Guid ScryfallId { get; set; }
    public Board Board { get; set; }
    public string CardName { get; set; } = "";
    public int Quantity { get; set; }
    public string? SetCode { get; set; }
    public string? CollectorNumber { get; set; }
}
