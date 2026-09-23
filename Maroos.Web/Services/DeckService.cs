using Maroos.Core.Decks;
using Maroos.Core.Scryfall;
using Maroos.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Maroos.Web.Services;

public sealed record DeckSummary(Guid Id, string Name, string Format, bool IsPublic, int MainCount, int SideCount, DateTime UpdatedAt);

public sealed record DeckView(Deck Deck, bool IsOwner, IReadOnlyList<DeckEntry> Entries)
{
    public DeckStats Stats { get; } = DeckStats.Compute(Entries);

    /// <summary>Problems with the deck in its format; empty when it's legal.</summary>
    public IReadOnlyList<DeckIssue> Issues { get; } = DeckRules.Check(Deck.Format, Entries);

    public int Count(Board board) => Entries.Where(e => e.Line.Board == board).Sum(e => e.Line.Quantity);

    public string Export(bool includePrintings = false) => DeckListWriter.Write(Entries.Select(e => e.Line), includePrintings);
}

public sealed record DeckImportResult(int CardsAdded, IReadOnlyList<string> NotFound, IReadOnlyList<string> Unreadable);

/// <summary>Thrown when someone other than the owner tries to change a deck.</summary>
public sealed class DeckAccessException() : Exception("Only the deck's owner can change it.");

/// <summary>Creates, edits, imports and exports decks. Every change checks that the caller owns the deck.</summary>
public sealed class DeckService(IDbContextFactory<ApplicationDbContext> dbFactory, CardService cards, TimeProvider clock)
{
    public async Task<IReadOnlyList<DeckSummary>> ListAsync(string ownerId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Decks
            .Where(d => d.OwnerId == ownerId)
            .OrderByDescending(d => d.UpdatedAt)
            .Select(d => new DeckSummary(
                d.Id, d.Name, d.Format, d.IsPublic,
                d.Cards.Where(c => c.Board != Board.Side).Sum(c => c.Quantity),
                d.Cards.Where(c => c.Board == Board.Side).Sum(c => c.Quantity),
                d.UpdatedAt))
            .ToListAsync(ct);
    }

    public async Task<Deck> CreateAsync(string ownerId, string name, string format, bool isPublic = false, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var deck = new Deck
        {
            OwnerId = ownerId,
            Name = CleanName(name),
            Format = CleanFormat(format),
            IsPublic = isPublic,
            CreatedAt = now,
            UpdatedAt = now,
        };

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.Decks.Add(deck);
        await db.SaveChangesAsync(ct);
        return deck;
    }

    /// <summary>The deck with card data, or null if it doesn't exist or is private and <paramref name="viewerId"/> isn't the owner.</summary>
    public async Task<DeckView?> GetAsync(Guid deckId, string? viewerId, CancellationToken ct = default)
    {
        Deck? deck;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            deck = await db.Decks.AsNoTracking().Include(d => d.Cards).FirstOrDefaultAsync(d => d.Id == deckId, ct);
        }

        var isOwner = deck is not null && viewerId is not null && deck.OwnerId == viewerId;
        if (deck is null || (!deck.IsPublic && !isOwner))
            return null;

        var cardData = await cards.GetCardsAsync(deck.Cards.Select(c => c.ScryfallId), ct);
        var entries = deck.Cards
            .OrderBy(c => c.CardName, StringComparer.OrdinalIgnoreCase)
            .Select(c => new DeckEntry(c, cardData.GetValueOrDefault(c.ScryfallId)))
            .ToList();

        return new DeckView(deck, isOwner, entries);
    }

    public Task UpdateSettingsAsync(Guid deckId, string ownerId, string name, string format, bool isPublic, CancellationToken ct = default) =>
        EditAsync(deckId, ownerId, deck =>
        {
            deck.Name = CleanName(name);
            deck.Format = CleanFormat(format);
            deck.IsPublic = isPublic;
            return Task.CompletedTask;
        }, ct);

    public async Task DeleteAsync(Guid deckId, string ownerId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var deck = await LoadOwnedAsync(db, deckId, ownerId, ct);
        db.Decks.Remove(deck);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Adds copies of a card by exact name. Returns the card's Scryfall name, or null if Scryfall has no such card.</summary>
    public async Task<string?> AddCardAsync(Guid deckId, string ownerId, string cardName, Board board, int quantity = 1, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var deck = await LoadOwnedAsync(db, deckId, ownerId, ct);

        var lookup = await cards.LookupAsync([CardIdentifier.ForName(cardName.Trim())], ct);
        if (lookup.Cards.FirstOrDefault()?.Card is not { } card)
            return null;

        AddToDeck(deck, card, board, quantity);
        await SaveAsync(db, deck, ct);
        return card.Name;
    }

    /// <summary>Changes a card's count by <paramref name="delta"/>; at zero the card is removed.</summary>
    public Task AdjustQuantityAsync(Guid deckId, string ownerId, Guid scryfallId, Board board, int delta, CancellationToken ct = default) =>
        EditAsync(deckId, ownerId, deck =>
        {
            var line = deck.Cards.FirstOrDefault(c => c.ScryfallId == scryfallId && c.Board == board);
            if (line is null)
                return Task.CompletedTask;

            line.Quantity = Math.Min(line.Quantity + delta, DeckCard.MaxQuantity);
            if (line.Quantity <= 0)
                deck.Cards.Remove(line);
            return Task.CompletedTask;
        }, ct);

    /// <summary>
    /// Imports a text deck list. Lines with a set code look up that printing first and fall back to the
    /// card name. With <paramref name="replace"/> the list replaces the deck; otherwise it adds to it.
    /// </summary>
    public async Task<DeckImportResult> ImportAsync(Guid deckId, string ownerId, string text, bool replace, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var deck = await LoadOwnedAsync(db, deckId, ownerId, ct);

        var parsed = DeckListParser.Parse(text);
        var requests = parsed.Lines.Select(line => (Line: line, Id: IdentifierFor(line))).ToList();
        var lookup = await cards.LookupAsync(requests.Select(r => r.Id), ct);

        // Printings Scryfall doesn't know (typo in the set or number) get a second try by name.
        var retry = requests.Where(r => !lookup.Found.ContainsKey(r.Id) && r.Id.Name is null).ToList();
        var byName = retry.Count > 0
            ? await cards.LookupAsync(retry.Select(r => CardIdentifier.ForName(r.Line.Name)), ct)
            : null;

        // Replacing zeroes every line first, so cards that stay keep their row instead of being deleted and re-added.
        if (replace)
        {
            foreach (var existing in deck.Cards)
                existing.Quantity = 0;
        }

        var added = 0;
        var notFound = new List<string>();
        foreach (var (line, id) in requests)
        {
            var card = lookup.Found.GetValueOrDefault(id)?.Card
                       ?? byName?.Found.GetValueOrDefault(CardIdentifier.ForName(line.Name))?.Card;
            if (card is null)
            {
                notFound.Add(line.Name);
                continue;
            }

            AddToDeck(deck, card, line.Board, line.Quantity);
            added += line.Quantity;
        }

        deck.Cards.RemoveAll(c => c.Quantity <= 0);
        await SaveAsync(db, deck, ct);
        return new DeckImportResult(added, notFound.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), parsed.Unreadable);
    }

    private static CardIdentifier IdentifierFor(DeckListLine line) => line switch
    {
        { SetCode: { } set, CollectorNumber: { } number } => CardIdentifier.ForPrinting(set, number),
        { SetCode: { } set } => new CardIdentifier(Name: line.Name, Set: set),
        _ => CardIdentifier.ForName(line.Name),
    };

    private static void AddToDeck(Deck deck, ScryfallCard card, Board board, int quantity)
    {
        var line = deck.Cards.FirstOrDefault(c => c.ScryfallId == card.Id && c.Board == board);
        if (line is null)
        {
            line = new DeckCard { DeckId = deck.Id, ScryfallId = card.Id, Board = board };
            deck.Cards.Add(line);
        }

        line.CardName = card.Name;
        line.SetCode = card.Set;
        line.CollectorNumber = card.CollectorNumber;
        line.Quantity = Math.Min(line.Quantity + quantity, DeckCard.MaxQuantity);
    }

    private async Task EditAsync(Guid deckId, string ownerId, Func<Deck, Task> edit, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var deck = await LoadOwnedAsync(db, deckId, ownerId, ct);
        await edit(deck);
        await SaveAsync(db, deck, ct);
    }

    private async Task SaveAsync(ApplicationDbContext db, Deck deck, CancellationToken ct)
    {
        deck.UpdatedAt = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
    }

    private static async Task<Deck> LoadOwnedAsync(ApplicationDbContext db, Guid deckId, string ownerId, CancellationToken ct)
    {
        var deck = await db.Decks.Include(d => d.Cards).FirstOrDefaultAsync(d => d.Id == deckId, ct)
                   ?? throw new KeyNotFoundException($"Deck {deckId} not found.");
        if (deck.OwnerId != ownerId)
            throw new DeckAccessException();
        return deck;
    }

    private static string CleanName(string name)
    {
        var clean = name.Trim();
        if (clean.Length == 0)
            throw new ArgumentException("Give the deck a name.", nameof(name));
        return clean.Length > Deck.MaxNameLength ? clean[..Deck.MaxNameLength] : clean;
    }

    private static string CleanFormat(string format) =>
        DeckFormats.IsKnown(format) ? format : throw new ArgumentException($"Unknown format '{format}'.", nameof(format));
}
