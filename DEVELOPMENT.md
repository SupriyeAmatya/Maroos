# Maroos

Magic: The Gathering deck site with a built-in auction house. Scryfall is the card data source (free public API); Moxfield is the UX reference only — never scrape it.

## Solution

- `Maroos.Web` — Blazor Web App (Interactive Server), Minimal API endpoints, SignalR, EF Core `ApplicationDbContext`
- `Maroos.Core` — models, deck rules, deck list parser, bidding rules
- `Maroos.Tests` — xUnit + bUnit

Run: `dotnet watch --project Maroos.Web` · Test: `dotnet test Maroos.slnx` · Production-like: `docker compose up --build` · Deploy: DEPLOY.md

## Auth

ASP.NET Core Identity (local email/password accounts, 2FA, account management under `/Account`) is the primary auth. Register at `/Account/Register`, log in at `/Account/Login`. Rules: unique email, password ≥ 8 chars, lockout for 15 min after 5 failed logins. New accounts must confirm their email (`RequireConfirmedAccount = true`); `IdentityEmailSender` queues the confirm, reset and email-change emails. GitHub is an external login layered on top and is only registered when `GitHub:ClientId` and `GitHub:ClientSecret` are configured (user-secrets in dev, `GitHub__ClientId` / `GitHub__ClientSecret` env vars in prod). Callback path: `/signin-github`.

## Build phases

- [x] Phase 0 — Tools (.NET 10 SDK, dotnet-ef)
- [x] Phase 1 — Solution setup, Identity + GitHub login, nav (Search, Decks, Auctions, Profile)
- [x] Phase 2 — Card search + card page (`ScryfallClient`, `CachedCard`, `/search`, `/card/{id}`)
- [x] Phase 3 — Deck builder (`Deck`, `DeckCard`, `DeckListParser`, `DeckService`, `/decks`)
- [x] Phase 4 — Deck stats + legality (`DeckStats`, `DeckRules`, `DeckStatsPanel`)
- [x] Phase 5 — Auction listings (`Auction`, `AuctionService`, `/auctions`, `/auctions/new`)
- [x] Phase 6 — Bidding + watchlist (`Bid`, `Watch`, `BiddingService`, `AuctionHub`, `AuctionCloser`, `/profile`)
- [x] Phase 7 — Polish + deploy prep (Postgres in prod, Dockerfile, S3 photo storage, Scryfall + WotC Fan Content Policy footer). Deploying itself: see DEPLOY.md.

Each phase ends with a working site and a commit.

## Environments and deploy

- Database: `Maroos.Web/Data/DatabaseSetup.cs`. `Database:Provider` = `Sqlite` (default in Development) or `Postgres` (default elsewhere). `ApplicationDbContext` carries the SQLite migrations (`Data/Migrations`); `PostgresDbContext` (same model) carries the PostgreSQL ones (`Data/Migrations/Postgres`). **Every model change needs both**:
  `dotnet ef migrations add <Name> --context ApplicationDbContext --project Maroos.Web` and
  `dotnet ef migrations add <Name> --context PostgresDbContext --project Maroos.Web --output-dir Data/Migrations/Postgres`.
  `MigrationsAreCompleteTests` fails otherwise. Don't add `IDesignTimeDbContextFactory`s: design time goes through Program (`EF.IsDesignTime` registers both contexts) so Identity's schema options apply.
- Postgres connection: `ConnectionStrings__DefaultConnection` (Npgsql string or `postgres://` URL) or `DATABASE_URL`. `Database__MigrateOnStartup=true` migrates at startup.
- Data Protection keys (login cookies) live in the `DataProtectionKeys` table.
- Photos: `IPhotoBlobStore` = `LocalPhotoBlobStore` (dev) or `S3PhotoBlobStore` (`Uploads:Storage=S3`, any S3-compatible bucket; falls back to BUCKET_NAME / AWS_ENDPOINT_URL_S3 / AWS_REGION). The app always serves them at `/uploads/{32-hex}.jpg` (only generated keys; immutable cache), so the bucket stays private.
- `/health` (DB check), security headers middleware, Blazor `ContentSecurityFrameAncestorsPolicy = 'none'`. Footer carries the Scryfall credit and the WotC Fan Content Policy notice — keep both.
- Docker: `Dockerfile` (non-root, port 8080), `compose.yaml` (Postgres + Versity S3 gateway + Mailpit + app; MinIO images are no longer on Docker Hub). The Dockerfile publishes for `TARGETARCH` only (keeps the image ~430 MB) and must not use `--no-restore` on publish, or `_framework/blazor.web.js` goes missing and nothing is interactive (a build step checks for it), `render.yaml`, `fly.toml`. Single instance only (in-process auction events + Blazor circuits).
- Integration tests against real services skip unless `MAROOS_TEST_POSTGRES` / `MAROOS_TEST_S3_*` are set (see compose.yaml).
- `QueryCounter` (tests) counts SELECTs; `QueryCountTests` keep list pages from going one-query-per-row.

## Card data

- `Maroos.Core/Scryfall/ScryfallClient` is the only code that calls Scryfall. `ScryfallThrottle` (singleton) spaces requests ≥100 ms apart.
- Components use `Maroos.Web/Services/CardService`: `GetCardAsync` goes through the `CachedCards` table (24 h); search/autocomplete are memory-cached for 10 min.
- Use `IDbContextFactory<ApplicationDbContext>` in services called from interactive components (one context per operation).
- Bulk lookups (`CardService.LookupAsync`, `GetCardsAsync`) use `/cards/collection` by id, name or `(set, collector number)`, 75 per request; `ScryfallCollectionResult.Found` pairs each identifier with its card (Scryfall returns found cards in request order and echoes unknown identifiers in `not_found`).
- Tests: saved Scryfall JSON in `Maroos.Tests/Scryfall/Fixtures`, served by `FakeScryfallHandler.WithFixtures()` (it also fakes `/cards/collection` from the single-card fixtures); `TestDb` gives an in-memory SQLite database. `MaroosWebFactory` swaps in the fake, so no test calls the real API.

## Decks

- `Maroos.Core/Decks`: `Deck`/`DeckCard` (key: deck + Scryfall printing + board), `DeckListParser`/`DeckListWriter` (Moxfield/Arena text format), `CardTypes` (grouping).
- `Maroos.Web/Services/DeckService` does every deck change and throws `DeckAccessException` unless the caller owns the deck. Private decks look like "not found" to anyone else.
- Pages: `/decks` (mine, `[Authorize]`), `/decks/new` (`[Authorize]`), `/decks/{id}` (interactive; owner edits, anyone views if public).
- `Maroos.Web/Components/Cards/CardAutocomplete` is the shared card-name box (search page, deck "add card").
- `DeckStats` (curve 0–6/7+ of non-land main + commander cards, colors, types, total price) and `DeckRules` (legality, size, copies, restricted, commander, commander color identity) live in `Maroos.Core/Decks`; `DeckView.Stats` / `DeckView.Issues` compute them. `DeckRules.RulesFor(format)` holds the per-format size and copy limits.
- Deck extras (design: `docs/deck-extras-design.md`):
  - Views: `/decks/{id}?view=list|stacks|grid`, parsed by `DeckViewModes.Parse` (unknown → List). Every view groups cards with `DeckGroups.For` (Core). Stacks and Grid are `Components/Decks/DeckPictures` built from `DeckCardTile`s. Editing (−/+) is list-only.
  - Sample hand: `SampleHand` (Core) deals from the main board only; pass a seeded `Random` in tests. `SampleHandPanel` shows it and saves nothing.
  - Copy: `DeckService.CopyAsync` makes a private "Copy of …" for any signed-in user who can see the deck (owners get "Duplicate"). It throws `DeckNotFoundException` for decks the caller can't see. `Deck.CopiedFromDeckId` has a foreign key with `ON DELETE SET NULL`. `DeckView.CopiedFrom` is the credit (original + owner's public name), shown only while the original is visible to the viewer. `?copied=1` is read as a string (a bool query parameter throws on other values), shown once, then removed from the URL.
- Charts: `Maroos.Web/Components/Decks/DeckStatsPanel` is a hand-drawn SVG; colors are `--viz-*` tokens on `.viz-root` in `app.css`. Format SVG numbers with invariant culture. The site is light-only; dark chart tokens apply under `[data-bs-theme="dark"]`, not the OS setting.
- If `dotnet watch` has the Debug build locked, build/test/migrate with `-c Release` / `--configuration Release`.

## Layout

- Navigation is a sticky top bar (`Components/Layout/NavMenu`): icons only, with each label shown under its icon on hover or keyboard focus (labels stay in the DOM for screen readers). The pin button toggles "always show labels"; `wwwroot/nav.js` (loaded in `<head>`) stores it in localStorage (`maroos.navPinned`) as `.nav-pinned` on `<html>` and re-applies it after Blazor enhanced navigation. Sticky page elements offset by `--site-nav-height` (app.css).

## Accounts

- `ApplicationUser.DisplayName` is what other players see (required at registration, editable at `/Account/Manage`, prefilled from GitHub). Show it via `ApplicationUser.PublicName(...)`; **never show a user's email** to other users.


## Email

- Everything goes through the outbox: `Maroos.Web/Services/Email/EmailOutbox` writes `OutgoingEmails` rows. `EmailDispatcher` (hosted service, every 5 s, or when nudged via `EmailSignal`) sends them with `SmtpEmailTransport` (MailKit), retries per `EmailRetry` (1/5/15/60/180 min, 6 attempts), clears bodies once sent and purges sent rows after 7 days. Never send email directly from a page or service.
- Text lives in `Maroos.Core/Email` (`AccountEmailContent`, `AuctionEmailContent`), with user text HTML-encoded via `EmailHtml.Encode`. Outbid: `OutbidRule` (previous high bidder only, if `ApplicationUser.AuctionEmails`), queued after the bid commits. Won/sold/unsold: queued in the same save as `CloseExpiredAsync`.
- Settings: `Email:From`, `Email:PublicBaseUrl`, `Email:Smtp:*`. Development and compose use Mailpit (`localhost:1025`, inbox `http://localhost:8025`; `docker compose up -d mailpit` for `dotnet watch`). Production fails fast when they're missing.
- Tests: test hosts don't run the dispatcher, so emails stay queued. `PageForms.RegisterAndConfirmAsync` gives a signed-in browser. `AuctionWorld.EmailTransport` records what `EmailDispatcher.SendDueAsync()` sends. The Mailpit test skips unless `MAROOS_TEST_SMTP_HOST` / `MAROOS_TEST_SMTP_PORT` / `MAROOS_TEST_MAILPIT_URL` are set.
## Auctions

- `Maroos.Core/Auctions`: `Auction` (money in `long` cents, `DateTime` UTC), `AuctionRules` (validation: start/increment > 0, 1–7 days; condition labels; `TimeLeft`). `Auction.IsOpenAt(now)` = Active and not past `EndsAtUtc` — an expired auction may still be `Active` until the phase 6 closer runs, so always use `IsOpenAt`.
- `Maroos.Web/Services/AuctionService`: create (card details from Scryfall), list/filter/sort open auctions by current price (high bid, else start), get (seller's public name, bids, winner), cancel (seller only, while open, no bids), `CloseExpiredAsync` (Ended + `WinnerId`), seller's listings.
- Bidding: `BidRules` (Core) — first bid ≥ start, then ≥ high + increment; not the seller; not the current high bidder; auction open. `BiddingService.PlaceBidAsync` runs in a **serializable transaction** that re-reads the high bid, retries serialization conflicts (Postgres 40001/40P01, SQLite busy/locked) up to 5× with jittered backoff, and publishes only after commit.
- Live updates: `AuctionEvents` (singleton) publishes `BidPlaced` / `AuctionEnded` to the `AuctionHub` group (`/hubs/auctions`, `JoinAuction(id)`) and to in-process subscribers. Blazor pages subscribe in-process (they already run on the server); the hub is for other clients. `AuctionPage` ticks a countdown every second and shows the winner the moment time runs out.
- `AuctionCloser` (hosted service) calls `CloseExpiredAsync` every minute. Test web hosts remove hosted services.
- Settlement is "winner contacts seller": after a won auction, only the winner sees the seller's email and only the seller sees the winner's (`AuctionView.ContactEmail`). Nobody else ever sees an email.
- Watchlist: `Watch` (user + Deck/Auction + id); `WatchService` only lets you watch what you can see and hides decks that went private; deleting a deck removes its watches. `WatchButton` component.
- `/profile` (`[Authorize]`, `?tab=decks|listings|bids|watchlist`).
- Tests: `AuctionWorld` wires the services over one database with a `ManualClock`; use `FileTestDb` when transactions or pages must run in parallel (the in-memory `TestDb` shares one connection).
- Photos: `AuctionPhotoStore` decodes and re-encodes every upload to JPEG with SkiaSharp (strips EXIF/GPS, applies orientation, max 1600 px, rejects non-images, 5 MB cap) and hands the bytes to `IPhotoBlobStore` (see Environments and deploy); the `/uploads/{key}` endpoint serves them.
- Pages: `/auctions` (static SSR, GET filters), `/auctions/new` (`ListCard.razor`, interactive, `[Authorize]`), `/auctions/{id}` (`AuctionPage.razor`, interactive).

## Coding rules

- Never call Scryfall from Blazor components; always go through `CardService` (→ `ScryfallClient` + DB cache).
- Money is `long` cents; never `double` or `float`.
- Every page or endpoint that writes data has `[Authorize]` and checks ownership.
- Keep business rules (parser, deck rules, bidding) in `Maroos.Core`, not in `.razor` files.
- Add an EF migration for every schema change: `dotnet ef migrations add <Name> --project Maroos.Web`.
- Run `dotnet build` and `dotnet test` before saying a task is done.
