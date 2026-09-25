# Deck extras for Maroos: design

**Date:** 2026-09-25  
**Status:** Implemented. Differences from the plan below: the sample-hand panel uses `Random.Shared` (only `SampleHand` itself takes a seeded `Random`); the picture views and sample hand are components in `Components/Decks` (`DeckPictures`, `DeckCardTile`, `SampleHandPanel`); `?copied=` is a string; hovered stack cards rise with `z-index`.  
**Scope:** The "Deck extras" item of the "what to add next" list: picture views of a deck, sample opening hands, and copying a deck.

## 1. Goal

Today the deck page at `/decks/{id}` lists the cards as text, grouped by type, with a single preview image on hover. This leaves three gaps:

- You can't see a deck at a glance as card pictures.
- You can't try out how the deck draws.
- Adopting someone else's deck means exporting it, creating a new deck, and importing it by hand.

This design adds two picture views, a sample-hand panel, and a one-click copy that credits the original deck.

## 2. Decisions

| Question | Decision |
|---|---|
| Picture views | Both: **Stacks** (groups as overlapping columns) and **Grid** (full-size cards). List stays the default |
| Where the view choice lives | In the URL: `?view=list\|stacks\|grid`, like `/profile?tab=` |
| Editing | Only in List view. Stacks and Grid are for viewing |
| Sample hand | 7 cards from the main deck, plus **Draw**, **Mulligan** (London mulligan note) and **New hand** |
| Who can copy | Any signed-in user who can see the deck. For the owner, the button says **Duplicate** |
| Credit | The copy stores `CopiedFromDeckId` and shows "Copied from *deck* by *display name*" while the original is still visible to the viewer |

## 3. What users experience

### 3.1 Views

- **The switch.** The toolbar gets a three-way switch: List, Stacks, Grid. It's made of links (`?view=stacks` and so on), so it works for owners and visitors, can be bookmarked or shared, and Back works.
  - The current choice is highlighted, with `aria-current="page"` so screen readers announce it.
  - A missing or unknown `view` value means List.
- **List.** Today's view, unchanged: text rows, −/+ buttons for the owner, and the hover preview.
- **Stacks.** The same groups as List: Commander, then each card type in `CardTypes.DisplayOrder`, then Sideboard. Each group is a heading with its count, then a column of card images.
  - The images overlap so that only the top strip (the name bar) of each card shows. The last card in the column is fully visible.
  - Hovering or keyboard-focusing a card brings it fully into view.
  - Each card links to its card page, `/card/{scryfallId}`.
  - A ×N badge shows the quantity when it's more than 1.
- **Grid.** Every card at full size in a responsive grid, in the same order as Stacks. Group headings stay, as full-width rows. Each card has the same link and ×N badge.
- **Missing images.** Only the front face is shown. If Scryfall has no image for a card, a text tile with the card name appears in its place, using the existing placeholder style.
- **Where the extras go.** The legality badge, the issues list and the stats panel show in every view. The owner's "Add a card" box and the Import panel stay available in every view. Only the −/+ buttons are list-only.

### 3.2 Sample hand

- **The button.** A toolbar button, "Sample hand", opens a panel in the same way as Import and Export. Anyone who can see the deck can use it, including visitors on public decks. Nothing is saved.
- **Opening the panel** deals a new hand: the main deck is shuffled and 7 cards are dealt. The panel shows them as a row of images, using the same tiles as the Grid view.
- **The panel's buttons:**
  - **Draw** adds the next card from the library to the hand. It's disabled when the library is empty.
  - **Mulligan** shuffles again, deals 7 again, and shows "Mulligan *N*: put *N* card(s) on the bottom". Maroos doesn't pick those cards; the note is the London mulligan rule, for the player to follow.
  - **New hand** starts over, with no mulligan count.
- **A status line** reads "Hand: 7 · Library: 53", so the deck size is always visible.
- **What's in the library.** Only the **main deck**: every copy counts, and commander and sideboard cards are left out.
- **Small decks.** A main deck with fewer than 7 cards deals everything it has.
- **Empty decks.** A main deck with no cards shows "Add cards to the main deck to draw a sample hand." instead of the buttons.

### 3.3 Copying a deck

- **The button.** The toolbar shows it on every deck the viewer can see:
  - **Signed in, not the owner:** "Copy deck". The copy is created and opened, with the status message "Copied. This is your private copy." The message shows once, because `?copied=1` is dropped from the URL after it's read.
  - **Owner:** "Duplicate". It works the same way.
  - **Not signed in:** a "Log in to copy" link to `Account/Login?ReturnUrl=/decks/{id}`, the pattern `WatchButton` uses.
- **The copy.**
  - It's owned by the copier and always **private**.
  - It has the same format and the same card lines: board, quantity, Scryfall printing, set code and collector number.
  - It's named "Copy of *name*", cut to `Deck.MaxNameLength` (100).
- **The credit line.** Under the copy's header: "Copied from **deck name** by *display name*", with the deck name linking to the original.
  - It shows only while the original exists and the viewer can see it: the original is public, or the viewer owns it.
  - Otherwise the line is simply absent, so it never links to a "not found" page and never reveals a private deck's name.
  - The display name comes from `ApplicationUser.PublicName(...)`; an email is never shown.
- **Nothing else happens.** Copying notifies no one and creates no watches.

## 4. Architecture

### 4.1 Core (`Maroos.Core/Decks`)

- **`DeckGroups`** (new): the grouping that `DeckPage.Group(...)` does inline today, moved to Core so every view and the tests share one definition of the groups.
  - `IReadOnlyList<DeckGroup> For(IEnumerable<DeckEntry> entries)` returns groups in page order:
    1. Commander.
    2. Each `CardTypes.DisplayOrder` type from the main deck, using `CardTypes.GroupHeading`.
    3. Sideboard.
  - Empty groups are left out. Within a group, entries keep their incoming order (`DeckService` already sorts by name).
  - Each `DeckGroup` has `record DeckGroup(string Heading, Board Board, IReadOnlyList<DeckEntry> Entries)` and `int Count` (the sum of quantities).
- **`DeckViewMode`** (new): `enum DeckViewMode { List, Stacks, Grid }`, plus `static DeckViewMode Parse(string? value)`, which ignores case and falls back to List.
- **`SampleHand`** (new): the state of one sample-hand session.
  - `SampleHand(IEnumerable<DeckEntry> entries, Random random)` builds the library from the main-board entries, one item per copy.
  - `Hand` (the cards drawn so far) and `LibraryCount`.
  - `Mulligans`: 0 after `NewHand`, then +1 per `Mulligan()`.
  - `NewHand()`: shuffle (Fisher–Yates with the given `Random`), deal `min(7, size)`, and set `Mulligans = 0`.
  - `Mulligan()`: shuffle and deal again, and increment `Mulligans`.
  - `Draw()`: move the top library card to the hand. Returns false, doing nothing, when the library is empty.
  - `const int OpeningHandSize = 7`.
- **`Deck`:** gains `Guid? CopiedFromDeckId`.

### 4.2 Data

- **`ApplicationDbContext`:** add the self-referencing foreign key `deck.HasOne<Deck>().WithMany().HasForeignKey(d => d.CopiedFromDeckId).OnDelete(DeleteBehavior.SetNull)`, plus an index on `CopiedFromDeckId`.
- **Migrations:** `DeckCopies` for both `ApplicationDbContext` (SQLite) and `PostgresDbContext` (Postgres). Only a nullable column, a foreign key and an index are added; no data changes.
- **Why `SET NULL`:** deleting the original deck, or deleting its owner's account, which cascades to their decks, leaves every copy in place without its credit.

### 4.3 Service (`Maroos.Web/Services/DeckService`)

- **`DeckView`** gains `DeckCopySource? CopiedFrom`, where `record DeckCopySource(Guid DeckId, string DeckName, string OwnerName)`.
- **`GetAsync`**, when `CopiedFromDeckId` is set, runs one extra query. It joins the source deck to its owner and keeps the source only if it's public or the viewer owns it. `OwnerName` comes from `ApplicationUser.PublicName(displayName)`.
- **`CopyAsync(Guid sourceId, string userId, CancellationToken)`** returns `Task<Guid>`:
  1. Load the source with its cards.
  2. If it's missing, or private and not the caller's, throw `DeckNotFoundException`, a new exception. The page shows "This deck doesn't exist, or it's private." instead of `DeckAccessException`.
  3. Otherwise add a new `Deck` with `Name = CleanName("Copy of " + source.Name)`, the same `Format`, `IsPublic = false`, `CopiedFromDeckId = source.Id`, fresh `CreatedAt` and `UpdatedAt` from `TimeProvider`, and a clone of each `DeckCard`.
  4. Save once and return the new deck's id.
- **`CleanName`** already cuts names to `MaxNameLength`, so the "Copy of" prefix can't push a name over the limit.

### 4.4 Page (`Maroos.Web/Components/Pages/DeckPage.razor`)

- **New parameters:** `[SupplyParameterFromQuery] string? View` and `[SupplyParameterFromQuery] bool Copied`.
- **Rendering:**
  - The card area branches on `DeckViewMode.Parse(View)`. List keeps today's rows, now built from `DeckGroups.For(...)`.
  - Stacks and Grid are new render fragments, and card tiles are a small shared fragment so the Sample hand panel reuses them.
  - The page is getting large, so if it passes about 500 lines, the picture views move into a `Components/Decks/DeckPictures.razor` component that takes `IReadOnlyList<DeckGroup>` and a layout.
- **Toolbar:**
  - The view switch, as a Bootstrap `btn-group` of links.
  - A Sample hand toggle, `Panel.SampleHand`.
  - Copy deck / Duplicate / Log in to copy.
- **Sample hand panel:**
  - It holds a `SampleHand` created with `Random.Shared` when the panel opens.
  - The component takes a `Func<Random>` so bUnit tests can supply a seeded one. The default is `Random.Shared`.
- **Copying:** `CopyAsync` → `Navigation.NavigateTo($"decks/{newId}?copied=1")`. The page reads `Copied` and shows the status message, then replaces the URL without it.
- **Styles:** new CSS in `app.css` (`.deck-stacks`, `.deck-stack`, `.deck-grid`, `.deck-card-tile`, `.deck-card-qty`). Missing images reuse the existing `.card-grid-placeholder` style.
  - Card images have `alt="{card name}"` and `loading="lazy"`.
  - The overlap in Stacks uses a negative top margin, and a hovered or focused card gets `z-index` and `margin-bottom` so it shows in full.
  - `prefers-reduced-motion` turns off the transition.

### 4.5 What stays the same

- The export, import, settings, add-card, legality and stats code.
- `WatchService`.
- Auctions.
- Every other page.

## 5. Error handling

| Situation | Result |
|---|---|
| `?view=` has an unknown value | List view |
| A card has no image | A text tile with the card's name |
| Copying a private deck that isn't yours (e.g. a guessed id) | `DeckNotFoundException` → "This deck doesn't exist, or it's private." |
| Copying while signed out | No button, only the "Log in to copy" link. The service also requires a user id |
| The original is deleted or made private | The copy's credit line disappears; the copy is untouched |
| The main deck has fewer than 7 cards | Deals all of them; Draw is disabled |
| The main deck is empty | The panel explains, and shows no buttons |
| A copied name is too long | Cut to 100 characters |

## 6. Testing

- **Core:**
  - `DeckGroupsTests`: the group order is Commander, the types, then Sideboard; empty groups are skipped; counts are sums of quantities.
  - `DeckViewModeTests`: parsing, including a missing value, a mixed-case value and an unknown value.
  - `SampleHandTests`, with a fixed seed:
    - 7 cards are dealt, from the main deck only, and a 4-of can appear up to 4 times.
    - Commander and sideboard cards never appear.
    - Draw moves one card and returns false when the library is empty.
    - Mulligan deals 7 again and increments the count; New hand resets it.
    - A 5-card deck deals 5.
    - Hand + library always equals the main deck count.
- **Service (`DeckServiceTests`):**
  - Copying a public deck: the copy is private, has the same format and card lines, is named "Copy of …", and has `CopiedFromDeckId` set.
  - A copy of a deck with a 100-character name is still 100 characters, and starts with "Copy of ".
  - Copying someone else's private deck throws `DeckNotFoundException`.
  - The owner can copy their own private deck.
  - `GetAsync` includes `CopiedFrom` while the original is public, and leaves it out once the original is private or deleted. The copy still loads after the original is deleted, because the foreign key is set to null.
- **Page (bUnit, `DeckPageTests`):**
  - Each `?view=` renders its layout, and the default is List.
  - The picture views have no −/+ buttons.
  - The Sample hand panel, with a seeded `Random`, deals 7 tiles; Draw makes 8; Mulligan shows the "Mulligan 1" note.
  - The copy button reads "Copy deck" for other users, "Duplicate" for the owner, and "Log in to copy" for visitors.
  - Clicking Copy deck navigates to the new deck.
  - The credit line shows the public name, never an email.
- **HTTP (`DeckPagesHttpTests`):** `/decks/{id}?view=grid` for a public deck returns the grid markup.
- **Existing guards:**
  - `MigrationsAreCompleteTests` fails if either migration is missing.
  - A new case in `QueryCountTests` (`Maroos.Tests/Deploy/DeployReadinessTests.cs`, which covers only auctions today): `GetAsync` for a copied deck makes exactly one more SELECT than for a deck that isn't a copy.

## 7. Out of scope

- Showing both faces of double-faced cards in the picture views: front face only for now.
- Dragging cards between groups.
- Editing from the picture views.
- A saved view preference.
- A "copied N times" counter, or a list of a deck's copies.
- Copying from other sites. Import already covers that.
