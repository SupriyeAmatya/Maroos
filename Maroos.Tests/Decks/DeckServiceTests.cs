// =====================================================================================================
// FILE: Maroos.Tests/Decks/DeckServiceTests.cs
// PURPOSE: DeckService: create/list/get, owner-only changes (DeckAccessException), add card, quantities,
//   import (batched lookups, name fallback, replace keeps rows), delete removes watches, and copying
//   (a private "Copy of …" with credit that disappears when the original goes private or is deleted).
// Each test's name says what it proves; the '// TEST:' line above each one repeats it in plain words.
// =====================================================================================================

using Maroos.Core.Decks;
using Maroos.Tests.Scryfall;
using Maroos.Web.Data;
using Maroos.Web.Services;
using Microsoft.Extensions.Caching.Memory;

namespace Maroos.Tests.Decks;

public sealed class DeckServiceTests : IDisposable
{
    private const string SixtyCardList = """
        4 Lightning Bolt
        4 Delver of Secrets
        1 Sol Ring (C21) 263
        51 Mountain

        Sideboard
        3 Sol Ring
        """;

    private readonly TestDb _db = new();
    private readonly FakeScryfallHandler _scryfall = FakeScryfallHandler.WithFixtures();
    private readonly DeckService _decks;
    private readonly string _alice;
    private readonly string _bob;

    public DeckServiceTests()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero));
        var cards = new CardService(_scryfall.CreateClient(), _db, new MemoryCache(new MemoryCacheOptions()), clock);
        _decks = new DeckService(_db, cards, clock);
        _alice = AddUser("alice@example.com", "Alice the Brewer");
        _bob = AddUser("bob@example.com");
    }

    public void Dispose() => _db.Dispose();

    // Helper: Add user (optionally with a display name, which the copy credit shows).
    private string AddUser(string email, string? displayName = null)
    {
        using var db = _db.CreateDbContext();
        var user = new ApplicationUser { UserName = email, Email = email, DisplayName = displayName };
        db.Users.Add(user);
        db.SaveChanges();
        return user.Id;
    }

    // Helper: New deck async.
    private async Task<Guid> NewDeckAsync(string owner, bool isPublic = false) =>
        (await _decks.CreateAsync(owner, "Burn", "modern", isPublic)).Id;

    // TEST: Import of a 60 card list survives a reload and exports the same list.
    [Fact]
    public async Task Import_of_a_60_card_list_survives_a_reload_and_exports_the_same_list()
    {
        var deckId = await NewDeckAsync(_alice);

        var result = await _decks.ImportAsync(deckId, _alice, SixtyCardList, replace: false);

        Assert.Equal(63, result.CardsAdded);
        Assert.Empty(result.NotFound);
        Assert.Empty(result.Unreadable);

        // A fresh load reads everything back from the database.
        var view = (await _decks.GetAsync(deckId, _alice))!;
        Assert.Equal(60, view.Count(Board.Main));
        Assert.Equal(3, view.Count(Board.Side));
        Assert.All(view.Entries, e => Assert.NotNull(e.Card));

        Assert.Equal("""
            4 Delver of Secrets // Insectile Aberration
            4 Lightning Bolt
            51 Mountain
            1 Sol Ring

            Sideboard
            3 Sol Ring
            """.ReplaceLineEndings("\n"), view.Export().ReplaceLineEndings("\n"));

        // Importing the export into a new deck gives the same deck.
        var copyId = await NewDeckAsync(_alice);
        await _decks.ImportAsync(copyId, _alice, view.Export(includePrintings: true), replace: false);
        var copy = (await _decks.GetAsync(copyId, _alice))!;
        Assert.Equal(view.Export(includePrintings: true), copy.Export(includePrintings: true));
    }

    // TEST: Import uses the printing from the set code.
    [Fact]
    public async Task Import_uses_the_printing_from_the_set_code()
    {
        var deckId = await NewDeckAsync(_alice);

        await _decks.ImportAsync(deckId, _alice, "1 Sol Ring (C21) 263", replace: false);

        var line = Assert.Single((await _decks.GetAsync(deckId, _alice))!.Entries).Line;
        Assert.Equal(Fixtures.SolRingC21Id, line.ScryfallId);
        Assert.Equal("c21", line.SetCode);
        Assert.Equal("263", line.CollectorNumber);
    }

    // TEST: Import falls back to the name when the printing does not exist.
    [Fact]
    public async Task Import_falls_back_to_the_name_when_the_printing_does_not_exist()
    {
        var deckId = await NewDeckAsync(_alice);

        var result = await _decks.ImportAsync(deckId, _alice, "4 Lightning Bolt (XXX) 999", replace: false);

        Assert.Equal(4, result.CardsAdded);
        Assert.Empty(result.NotFound);
        Assert.Equal(Fixtures.LightningBoltId, Assert.Single((await _decks.GetAsync(deckId, _alice))!.Entries).Line.ScryfallId);
    }

    // TEST: Import reports unknown cards and unreadable lines but keeps the rest.
    [Fact]
    public async Task Import_reports_unknown_cards_and_unreadable_lines_but_keeps_the_rest()
    {
        var deckId = await NewDeckAsync(_alice);

        var result = await _decks.ImportAsync(deckId, _alice, "4 Lightning Bolt\n2 Not A Real Card\n0 Mountain", replace: false);

        Assert.Equal(4, result.CardsAdded);
        Assert.Equal(["Not A Real Card"], result.NotFound);
        Assert.Equal(["0 Mountain"], result.Unreadable);
        Assert.Equal(4, (await _decks.GetAsync(deckId, _alice))!.Count(Board.Main));
    }

    // TEST: Import merges repeated lines and adds to existing cards.
    [Fact]
    public async Task Import_merges_repeated_lines_and_adds_to_existing_cards()
    {
        var deckId = await NewDeckAsync(_alice);
        await _decks.ImportAsync(deckId, _alice, "2 Lightning Bolt", replace: false);

        await _decks.ImportAsync(deckId, _alice, "1 Lightning Bolt\n1 lightning bolt", replace: false);

        var line = Assert.Single((await _decks.GetAsync(deckId, _alice))!.Entries).Line;
        Assert.Equal(4, line.Quantity);
        Assert.Equal("Lightning Bolt", line.CardName);
    }

    // TEST: Import with replace swaps the whole list.
    [Fact]
    public async Task Import_with_replace_swaps_the_whole_list()
    {
        var deckId = await NewDeckAsync(_alice);
        await _decks.ImportAsync(deckId, _alice, "4 Lightning Bolt\n20 Mountain", replace: false);

        await _decks.ImportAsync(deckId, _alice, "2 Lightning Bolt\n1 Sol Ring", replace: true);

        var view = (await _decks.GetAsync(deckId, _alice))!;
        Assert.Equal(["2 Lightning Bolt", "1 Sol Ring"], view.Export().ReplaceLineEndings("\n").Split('\n'));
    }

    // TEST: AddCard adds by exact name and reports unknown names.
    [Fact]
    public async Task AddCard_adds_by_exact_name_and_reports_unknown_names()
    {
        var deckId = await NewDeckAsync(_alice);

        Assert.Equal("Lightning Bolt", await _decks.AddCardAsync(deckId, _alice, "lightning bolt", Board.Main));
        Assert.Equal("Lightning Bolt", await _decks.AddCardAsync(deckId, _alice, "Lightning Bolt", Board.Main));
        Assert.Equal("Mountain", await _decks.AddCardAsync(deckId, _alice, "Mountain", Board.Side, quantity: 3));
        Assert.Null(await _decks.AddCardAsync(deckId, _alice, "Not A Real Card", Board.Main));

        var view = (await _decks.GetAsync(deckId, _alice))!;
        Assert.Equal(2, view.Count(Board.Main));
        Assert.Equal(3, view.Count(Board.Side));
    }

    // TEST: AdjustQuantity changes counts and removes at zero.
    [Fact]
    public async Task AdjustQuantity_changes_counts_and_removes_at_zero()
    {
        var deckId = await NewDeckAsync(_alice);
        await _decks.ImportAsync(deckId, _alice, "2 Lightning Bolt", replace: false);

        await _decks.AdjustQuantityAsync(deckId, _alice, Fixtures.LightningBoltId, Board.Main, +1);
        Assert.Equal(3, (await _decks.GetAsync(deckId, _alice))!.Count(Board.Main));

        await _decks.AdjustQuantityAsync(deckId, _alice, Fixtures.LightningBoltId, Board.Main, -3);
        Assert.Empty((await _decks.GetAsync(deckId, _alice))!.Entries);
    }

    // TEST: Only the owner can change a deck.
    [Fact]
    public async Task Only_the_owner_can_change_a_deck()
    {
        var deckId = await NewDeckAsync(_alice, isPublic: true);
        await _decks.ImportAsync(deckId, _alice, "4 Lightning Bolt", replace: false);

        await Assert.ThrowsAsync<DeckAccessException>(() => _decks.ImportAsync(deckId, _bob, "1 Sol Ring", replace: true));
        await Assert.ThrowsAsync<DeckAccessException>(() => _decks.AddCardAsync(deckId, _bob, "Sol Ring", Board.Main));
        await Assert.ThrowsAsync<DeckAccessException>(() => _decks.AdjustQuantityAsync(deckId, _bob, Fixtures.LightningBoltId, Board.Main, -4));
        await Assert.ThrowsAsync<DeckAccessException>(() => _decks.UpdateSettingsAsync(deckId, _bob, "Mine now", "modern", true));
        await Assert.ThrowsAsync<DeckAccessException>(() => _decks.DeleteAsync(deckId, _bob));

        var view = (await _decks.GetAsync(deckId, _alice))!;
        Assert.Equal("Burn", view.Deck.Name);
        Assert.Equal("4 Lightning Bolt", view.Export());
    }

    // TEST: Private decks are visible only to their owner and public ones to everyone.
    [Fact]
    public async Task Private_decks_are_visible_only_to_their_owner_and_public_ones_to_everyone()
    {
        var privateDeck = await NewDeckAsync(_alice, isPublic: false);
        var publicDeck = await NewDeckAsync(_alice, isPublic: true);

        Assert.True((await _decks.GetAsync(privateDeck, _alice))!.IsOwner);
        Assert.Null(await _decks.GetAsync(privateDeck, _bob));
        Assert.Null(await _decks.GetAsync(privateDeck, viewerId: null));

        var asBob = (await _decks.GetAsync(publicDeck, _bob))!;
        Assert.False(asBob.IsOwner);
        Assert.False((await _decks.GetAsync(publicDeck, viewerId: null))!.IsOwner);

        Assert.Null(await _decks.GetAsync(Guid.NewGuid(), _alice));
    }

    // TEST: List shows only the owners decks with counts.
    [Fact]
    public async Task List_shows_only_the_owners_decks_with_counts()
    {
        var deckId = await NewDeckAsync(_alice);
        await _decks.ImportAsync(deckId, _alice, SixtyCardList, replace: false);
        await NewDeckAsync(_bob);

        var summary = Assert.Single(await _decks.ListAsync(_alice));
        Assert.Equal(deckId, summary.Id);
        Assert.Equal(60, summary.MainCount);
        Assert.Equal(3, summary.SideCount);
    }

    // TEST: Settings rename change format and visibility.
    [Fact]
    public async Task Settings_rename_change_format_and_visibility()
    {
        var deckId = await NewDeckAsync(_alice);

        await _decks.UpdateSettingsAsync(deckId, _alice, "  Izzet Tempo  ", "legacy", isPublic: true);

        var deck = (await _decks.GetAsync(deckId, _bob))!.Deck;
        Assert.Equal("Izzet Tempo", deck.Name);
        Assert.Equal("legacy", deck.Format);
        Assert.True(deck.IsPublic);
    }

    // TEST: Create rejects blank names and unknown formats.
    [Theory]
    [InlineData("   ", "modern")]
    [InlineData("Burn", "not-a-format")]
    public async Task Create_rejects_blank_names_and_unknown_formats(string name, string format)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _decks.CreateAsync(_alice, name, format));
    }

    // TEST: Delete removes the deck and its cards.
    [Fact]
    public async Task Delete_removes_the_deck_and_its_cards()
    {
        var deckId = await NewDeckAsync(_alice);
        await _decks.ImportAsync(deckId, _alice, "4 Lightning Bolt", replace: false);

        await _decks.DeleteAsync(deckId, _alice);

        Assert.Null(await _decks.GetAsync(deckId, _alice));
        using var db = _db.CreateDbContext();
        Assert.Empty(db.DeckCards);
    }

    // TEST: Copying a public deck gives a private copy with the same cards and credit.
    [Fact]
    public async Task Copying_a_public_deck_gives_a_private_copy_with_the_same_cards_and_credit()
    {
        var sourceId = await NewDeckAsync(_alice, isPublic: true);
        await _decks.ImportAsync(sourceId, _alice, SixtyCardList, replace: false);

        var copyId = await _decks.CopyAsync(sourceId, _bob);

        var copy = (await _decks.GetAsync(copyId, _bob))!;
        var source = (await _decks.GetAsync(sourceId, _bob))!;
        Assert.True(copy.IsOwner);
        Assert.Equal("Copy of Burn", copy.Deck.Name);
        Assert.Equal("modern", copy.Deck.Format);
        Assert.False(copy.Deck.IsPublic);
        Assert.Equal(sourceId, copy.Deck.CopiedFromDeckId);
        Assert.Equal(source.Export(includePrintings: true), copy.Export(includePrintings: true));
        Assert.Equal(new DeckCopySource(sourceId, "Burn", "Alice the Brewer"), copy.CopiedFrom);

        // The original is untouched and isn't a copy of anything.
        Assert.Null(source.CopiedFrom);
        Assert.Equal(63, source.Count(Board.Main) + source.Count(Board.Side));
    }

    // TEST: A copy of a long name stays within the limit.
    [Fact]
    public async Task A_copy_of_a_long_name_stays_within_the_limit()
    {
        var sourceId = (await _decks.CreateAsync(_alice, new string('x', Deck.MaxNameLength), "modern", isPublic: true)).Id;

        var copy = (await _decks.GetAsync(await _decks.CopyAsync(sourceId, _bob), _bob))!;

        Assert.Equal(Deck.MaxNameLength, copy.Deck.Name.Length);
        Assert.StartsWith("Copy of x", copy.Deck.Name);
    }

    // TEST: Someone elses private deck cannot be copied.
    // A made-up id fails the same way, and a signed-out caller (empty user id) is refused outright.
    [Fact]
    public async Task Someone_elses_private_deck_cannot_be_copied()
    {
        var sourceId = await NewDeckAsync(_alice, isPublic: false);

        await Assert.ThrowsAsync<DeckNotFoundException>(() => _decks.CopyAsync(sourceId, _bob));
        await Assert.ThrowsAsync<DeckNotFoundException>(() => _decks.CopyAsync(Guid.NewGuid(), _bob));
        await Assert.ThrowsAsync<DeckAccessException>(() => _decks.CopyAsync(sourceId, ""));
    }

    // TEST: Owners can duplicate their own private decks.
    [Fact]
    public async Task Owners_can_duplicate_their_own_private_decks()
    {
        var sourceId = await NewDeckAsync(_alice, isPublic: false);
        await _decks.ImportAsync(sourceId, _alice, "4 Lightning Bolt", replace: false);

        var copy = (await _decks.GetAsync(await _decks.CopyAsync(sourceId, _alice), _alice))!;

        Assert.Equal(4, copy.Count(Board.Main));
        // Alice can see her own private original, so the credit shows for her.
        Assert.Equal(sourceId, copy.CopiedFrom?.DeckId);
    }

    // TEST: The credit disappears when the original goes private or is deleted.
    // The copy survives the original's deletion: the database sets CopiedFromDeckId to null (ON DELETE SET NULL).
    [Fact]
    public async Task The_credit_disappears_when_the_original_goes_private_or_is_deleted()
    {
        var sourceId = await NewDeckAsync(_alice, isPublic: true);
        var copyId = await _decks.CopyAsync(sourceId, _bob);

        await _decks.UpdateSettingsAsync(sourceId, _alice, "Burn", "modern", isPublic: false);
        var afterPrivate = (await _decks.GetAsync(copyId, _bob))!;
        Assert.Null(afterPrivate.CopiedFrom);
        Assert.Equal(sourceId, afterPrivate.Deck.CopiedFromDeckId);

        await _decks.DeleteAsync(sourceId, _alice);
        var afterDelete = (await _decks.GetAsync(copyId, _bob))!;
        Assert.Null(afterDelete.CopiedFrom);
        Assert.Null(afterDelete.Deck.CopiedFromDeckId);
    }
}
