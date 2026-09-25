// =====================================================================================================
// FILE: Maroos.Web/Services/DeckService.cs
// PURPOSE: Everything that reads or changes decks: list, create, view, rename/settings, delete, add a card,
// +/- quantities, import a pasted deck list, and export.
//
// SECURITY: every change goes through LoadOwnedAsync, which throws DeckAccessException unless the caller
// owns the deck. Private decks look exactly like missing decks to anyone else (GetAsync returns null).
// The page hides edit buttons from non-owners, but THIS is the real check.
//
// IMPORT PROCESS (ImportAsync)
//   1. DeckListParser turns the text into lines ("4 Lightning Bolt (2X2) 117").
//   2. Each line becomes a Scryfall identifier: exact printing (set + number), name + set, or just name.
//   3. One batched Scryfall lookup for all lines (75 per request).
//   4. Printings Scryfall didn't know get a second lookup by name.
//   5. Found cards are added; the rest are reported back as "not found" / "unreadable".
//
// COPY PROCESS (CopyAsync)
//   1. Signed-out callers can't copy; someone else's private deck counts as "not found".
//   2. A new private deck "Copy of <name>" with the same format and card lines, pointing back at the original
//      (CopiedFromDeckId), all in one save.
//   3. GetAsync shows the credit ("Copied from <deck> by <name>") only while the viewer can still see the original.
// =====================================================================================================

using Maroos.Core.Decks;
using Maroos.Core.Scryfall;
using Maroos.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Maroos.Web.Services;

// DeckSummary: one row of "My decks" (counts are totals of quantities: main+commander vs. sideboard).
public sealed record DeckSummary(Guid Id, string Name, string Format, bool IsPublic, int MainCount, int SideCount, DateTime UpdatedAt);

// DeckCopySource: what the "Copied from <deck> by <name>" line needs. OwnerName is the public display name, never an email.
/// <summary>Credit for a copied deck: the original, and its owner's public name.</summary>
public sealed record DeckCopySource(Guid DeckId, string DeckName, string OwnerName);

// DeckView: a deck ready to display — its cards paired with Scryfall data (DeckEntry), plus derived info.
// CopiedFrom is null unless this deck is a copy whose original the viewer can still see.
/// <param name="CopiedFrom">The original this deck was copied from, when it still exists and the viewer can see it.</param>
public sealed record DeckView(Deck Deck, bool IsOwner, IReadOnlyList<DeckEntry> Entries, DeckCopySource? CopiedFrom = null)
{
    // Stats: mana curve, colors, types, total price (computed once when the view is created).
    public DeckStats Stats { get; } = DeckStats.Compute(Entries);

    /// <summary>Problems with the deck in its format; empty when it's legal.</summary>
    public IReadOnlyList<DeckIssue> Issues { get; } = DeckRules.Check(Deck.Format, Entries);

    // Count: how many cards are on one board.
    public int Count(Board board) => Entries.Where(e => e.Line.Board == board).Sum(e => e.Line.Quantity);

    // Export: the deck as text (Moxfield/Arena format), optionally with "(SET) number" printings.
    public string Export(bool includePrintings = false) => DeckListWriter.Write(Entries.Select(e => e.Line), includePrintings);
}

// DeckImportResult: what an import did: cards added, names Scryfall didn't find, lines that couldn't be parsed.
public sealed record DeckImportResult(int CardsAdded, IReadOnlyList<string> NotFound, IReadOnlyList<string> Unreadable);

/// <summary>Thrown when someone other than the owner tries to change a deck.</summary>
public sealed class DeckAccessException() : Exception("Only the deck's owner can change it.");

// DeckNotFoundException: thrown by CopyAsync. The page shows its message as it is, so it's worded for players.
/// <summary>Thrown when a deck doesn't exist or is private to someone else (the two look the same on purpose).</summary>
public sealed class DeckNotFoundException() : Exception("This deck doesn't exist, or it's private.");

/// <summary>Creates, edits, imports and exports decks. Every change checks that the caller owns the deck.</summary>
public sealed class DeckService(IDbContextFactory<ApplicationDbContext> dbFactory, CardService cards, TimeProvider clock)
{
    // ListAsync: the owner's decks, most recently changed first. The card counts are computed in SQL
    // (one query for the whole list — QueryCountTests guard this).
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

    // CreateAsync: a new empty deck (the /decks/new form). Name is trimmed/capped; format must be known.
    public async Task<Deck> CreateAsync(string ownerId, string name, string format, bool isPublic = false, CancellationToken ct = default)
    {
        // Must be signed in.
        if (string.IsNullOrEmpty(ownerId))
            throw new DeckAccessException();

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
        // Step 1: load the deck and its card rows (then close the database context right away).
        Deck? deck;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            deck = await db.Decks.AsNoTracking().Include(d => d.Cards).FirstOrDefaultAsync(d => d.Id == deckId, ct);
        }

        // Step 2: visibility. Missing and "private and not yours" both return null (so the page says "not found").
        var isOwner = deck is not null && viewerId is not null && deck.OwnerId == viewerId;
        if (deck is null || (!deck.IsPublic && !isOwner))
            return null;

        // Step 3: Scryfall data for every card (from the cache; batched fetch for the rest).
        var cardData = await cards.GetCardsAsync(deck.Cards.Select(c => c.ScryfallId), ct);
        // Step 4: pair each line with its card data, sorted by name. A card Scryfall no longer knows gets null data.
        var entries = deck.Cards
            .OrderBy(c => c.CardName, StringComparer.OrdinalIgnoreCase)
            .Select(c => new DeckEntry(c, cardData.GetValueOrDefault(c.ScryfallId)))
            .ToList();

        // Step 5: for a copy, look up the original's credit (one extra query, and only for copies).
        var copiedFrom = deck.CopiedFromDeckId is { } sourceId ? await CopySourceAsync(sourceId, viewerId, ct) : null;
        return new DeckView(deck, isOwner, entries, copiedFrom);
    }

    /// <summary>
    /// Copies a deck the caller can see (someone's public deck, or one of their own) into a new private deck they own,
    /// named "Copy of …" and remembering where it came from. Returns the new deck's id.
    /// </summary>
    public async Task<Guid> CopyAsync(Guid sourceId, string userId, CancellationToken ct = default)
    {
        // Step 1: signed-out callers can't copy (the page shows them a login link instead).
        if (string.IsNullOrEmpty(userId))
            throw new DeckAccessException();

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        // Step 2: load the original with its cards. Same visibility rule as viewing: someone else's private deck is
        // "not found", so guessing a deck id reveals nothing.
        var source = await db.Decks.AsNoTracking().Include(d => d.Cards).FirstOrDefaultAsync(d => d.Id == sourceId, ct);
        if (source is null || (!source.IsPublic && source.OwnerId != userId))
            throw new DeckNotFoundException();

        var now = clock.GetUtcNow().UtcDateTime;
        // Step 3: the new deck: private, owned by the caller, "Copy of …" (CleanName cuts it to 100 characters),
        // and pointing back at the original.
        var copy = new Deck
        {
            OwnerId = userId,
            Name = CleanName("Copy of " + source.Name),
            Format = source.Format,
            IsPublic = false,
            CopiedFromDeckId = source.Id,
            CreatedAt = now,
            UpdatedAt = now,
        };
        // Step 4: clone every card line (board, quantity, exact printing). New DeckCard objects, so EF inserts new rows.
        copy.Cards.AddRange(source.Cards.Select(c => new DeckCard
        {
            DeckId = copy.Id,
            ScryfallId = c.ScryfallId,
            Board = c.Board,
            CardName = c.CardName,
            Quantity = c.Quantity,
            SetCode = c.SetCode,
            CollectorNumber = c.CollectorNumber,
        }));

        // Step 5: one save writes the deck and all its cards together.
        db.Decks.Add(copy);
        await db.SaveChangesAsync(ct);
        return copy.Id;
    }

    /// <summary>The original deck's name and owner, or null when it's gone or the viewer can't see it (one query).</summary>
    private async Task<DeckCopySource?> CopySourceAsync(Guid sourceId, string? viewerId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        // One query: the deck joined to its owner, and only if the viewer may see it (public, or their own).
        // Otherwise null, so a private original's name never leaks through a copy.
        var source = await (
                from d in db.Decks
                join u in db.Users on d.OwnerId equals u.Id
                where d.Id == sourceId && (d.IsPublic || d.OwnerId == viewerId)
                select new { d.Id, d.Name, u.DisplayName })
            .FirstOrDefaultAsync(ct);
        return source is null ? null : new DeckCopySource(source.Id, source.Name, ApplicationUser.PublicName(source.DisplayName));
    }

    // UpdateSettingsAsync: rename, change format, public/private (owner only, via EditAsync).
    public Task UpdateSettingsAsync(Guid deckId, string ownerId, string name, string format, bool isPublic, CancellationToken ct = default) =>
        EditAsync(deckId, ownerId, deck =>
        {
            deck.Name = CleanName(name);
            deck.Format = CleanFormat(format);
            deck.IsPublic = isPublic;
            return Task.CompletedTask;
        }, ct);

    // DeleteAsync: owner only. Its cards go with it (cascade); watches are removed separately
    // because the Watches table has no foreign key to decks.
    public async Task DeleteAsync(Guid deckId, string ownerId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var deck = await LoadOwnedAsync(db, deckId, ownerId, ct);
        db.Decks.Remove(deck);
        await db.SaveChangesAsync(ct);
        await WatchService.RemoveAllAsync(db, Core.Watchlist.WatchTarget.Deck, deckId, ct);
    }

    // AddCardAsync: the deck page's "Add card" box (with autocomplete).
    /// <summary>Adds copies of a card by exact name. Returns the card's Scryfall name, or null if Scryfall has no such card.</summary>
    public async Task<string?> AddCardAsync(Guid deckId, string ownerId, string cardName, Board board, int quantity = 1, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        // Ownership check first.
        var deck = await LoadOwnedAsync(db, deckId, ownerId, ct);

        // Look the name up on Scryfall; `is not { } card` = "no card found" (else binds it to `card`).
        var lookup = await cards.LookupAsync([CardIdentifier.ForName(cardName.Trim())], ct);
        if (lookup.Cards.FirstOrDefault()?.Card is not { } card)
            return null;

        AddToDeck(deck, card, board, quantity);
        await SaveAsync(db, deck, ct);
        return card.Name;
    }

    // AdjustQuantityAsync: the + / − buttons next to each card (delta = +1 or −1).
    /// <summary>Changes a card's count by <paramref name="delta"/>; at zero the card is removed.</summary>
    public Task AdjustQuantityAsync(Guid deckId, string ownerId, Guid scryfallId, Board board, int delta, CancellationToken ct = default) =>
        EditAsync(deckId, ownerId, deck =>
        {
            var line = deck.Cards.FirstOrDefault(c => c.ScryfallId == scryfallId && c.Board == board);
            if (line is null)
                return Task.CompletedTask;

            // Never above the per-line maximum; at 0 or below, remove the line.
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

        // Steps 1–3: parse, build one identifier per line, look them all up in batches.
        var parsed = DeckListParser.Parse(text);
        var requests = parsed.Lines.Select(line => (Line: line, Id: IdentifierFor(line))).ToList();
        var lookup = await cards.LookupAsync(requests.Select(r => r.Id), ct);

        // Printings Scryfall doesn't know (typo in the set or number) get a second try by name.
        // (`r.Id.Name is null` = the first lookup was by printing, not by name.)
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

        // Step 5: add what was found, remember what wasn't.
        var added = 0;
        var notFound = new List<string>();
        foreach (var (line, id) in requests)
        {
            // The first lookup's result, else the by-name retry's result.
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

        // Drop lines still at 0 (cards that were in the deck but not in the replacing list).
        deck.Cards.RemoveAll(c => c.Quantity <= 0);
        await SaveAsync(db, deck, ct);
        return new DeckImportResult(added, notFound.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), parsed.Unreadable);
    }

    // IdentifierFor: the most specific Scryfall identifier a parsed line allows (property patterns on the line).
    private static CardIdentifier IdentifierFor(DeckListLine line) => line switch
    {
        // "(2X2) 117" -> that exact printing.
        { SetCode: { } set, CollectorNumber: { } number } => CardIdentifier.ForPrinting(set, number),
        // "(2X2)" only -> the card by name, from that set.
        { SetCode: { } set } => new CardIdentifier(Name: line.Name, Set: set),
        // Just a name -> Scryfall's default printing.
        _ => CardIdentifier.ForName(line.Name),
    };

    // AddToDeck: add copies of a card to a board — raise the existing line's quantity or create a new line.
    // Also refreshes the stored name/set/number from Scryfall's data.
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

    // EditAsync: the shared "load owned deck -> change it -> save" pattern. `edit` is the change to make.
    private async Task EditAsync(Guid deckId, string ownerId, Func<Deck, Task> edit, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var deck = await LoadOwnedAsync(db, deckId, ownerId, ct);
        await edit(deck);
        await SaveAsync(db, deck, ct);
    }

    // SaveAsync: bump UpdatedAt (so "My decks" sorts it to the top), then save.
    private async Task SaveAsync(ApplicationDbContext db, Deck deck, CancellationToken ct)
    {
        deck.UpdatedAt = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
    }

    // LoadOwnedAsync: THE ownership check. Loads the deck with its cards (tracked, so changes save),
    // or throws: KeyNotFoundException if missing, DeckAccessException if not yours.
    private static async Task<Deck> LoadOwnedAsync(ApplicationDbContext db, Guid deckId, string ownerId, CancellationToken ct)
    {
        // Pages only show edit controls to the owner; this refuses anyone else, including signed-out callers.
        if (string.IsNullOrEmpty(ownerId))
            throw new DeckAccessException();

        var deck = await db.Decks.Include(d => d.Cards).FirstOrDefaultAsync(d => d.Id == deckId, ct)
                   ?? throw new KeyNotFoundException($"Deck {deckId} not found.");
        if (deck.OwnerId != ownerId)
            throw new DeckAccessException();
        return deck;
    }

    // CleanName: trimmed, not empty, cut to the maximum length.
    private static string CleanName(string name)
    {
        var clean = name.Trim();
        if (clean.Length == 0)
            throw new ArgumentException("Give the deck a name.", nameof(name));
        return clean.Length > Deck.MaxNameLength ? clean[..Deck.MaxNameLength] : clean;
    }

    // CleanFormat: only formats listed in DeckFormats (standard, modern, commander...) are accepted.
    private static string CleanFormat(string format) =>
        DeckFormats.IsKnown(format) ? format : throw new ArgumentException($"Unknown format '{format}'.", nameof(format));
}
