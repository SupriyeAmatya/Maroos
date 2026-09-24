# Maroos

Maroos is a website for Magic: The Gathering players. You can search every card, build decks and check them against your format, and buy and sell single cards in live auctions.

Card data and images come from [Scryfall](https://scryfall.com)'s free public API.

## Features

- **Card search.** Search by name or with full [Scryfall syntax](https://scryfall.com/docs/syntax). Suggestions appear as you type. A card page shows its images, rules text, prices and legality in every format.
- **Deck builder.** Build public or private decks. Paste a list from Moxfield or Arena, or export your deck to them.
  - The mana curve, colors, card types and total price are worked out for you.
  - Rules problems are flagged: deck size, copy limits, banned or restricted cards, the commander and its color identity.
- **Auctions.** List a specific printing with its condition, whether it's foil, and an optional photo. Uploaded photos are re-encoded, which removes location data and any other metadata.
  - Bids update live on every open page. A countdown shows the time left.
  - Two bids placed at the same moment can never both win.
- **Email.** New accounts confirm their email address, and you can reset a forgotten password.
  - Auction emails cover being outbid, winning, a card selling, and an auction ending with no bids.
  - Auction emails can be turned off in account settings.
- **Accounts.** Sign up with an email and password, or log in with GitHub. Two-factor authentication and passkeys are supported.
  - Other players only ever see your display name. The one exception: after an auction ends, the winner and the seller see each other's email so they can arrange payment.
- **Profile and watchlist.** Your decks, listings and bids in one place. Save any deck or auction with ♡ Watch.

## Tech stack

| Area | Technology |
|---|---|
| App | .NET 10, Blazor Web App (Interactive Server), SignalR |
| Data | Entity Framework Core. SQLite in development, PostgreSQL in production |
| Accounts | ASP.NET Core Identity, optional GitHub OAuth |
| Photos | SkiaSharp for re-encoding. Stored on local disk, or in any S3-compatible bucket |
| Email | MailKit (SMTP), sent in the background with retries. [Mailpit](https://mailpit.axllent.org) catches emails locally |
| Tests | xUnit, bUnit, `WebApplicationFactory` |
| Hosting | Docker. Blueprints are included for Render and Fly.io |

## Getting started

### Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- The EF Core tools:

  ```bash
  dotnet tool install --global dotnet-ef
  ```

- [Docker](https://www.docker.com/products/docker-desktop/). Optional, but it runs the local email inbox and the production-like stack.

### Run it in development

1. Create the development database (SQLite, at `Maroos.Web/Data/app.db`):

   ```bash
   dotnet ef database update --context ApplicationDbContext --project Maroos.Web
   ```

2. Start the local email inbox, so sign-up and password-reset emails have somewhere to go:

   ```bash
   docker compose up -d mailpit
   ```

3. Run the site:

   ```bash
   dotnet watch --project Maroos.Web
   ```

4. Open the site and the inbox:
   - The site is at http://localhost:5126. Register an account.
   - The confirmation email arrives at http://localhost:8025.

### Optional: GitHub login

Create an OAuth app on GitHub with the callback URL `http://localhost:5126/signin-github`. Then store its keys as user secrets:

```bash
dotnet user-secrets set "GitHub:ClientId" "<client id>" --project Maroos.Web
```

```bash
dotnet user-secrets set "GitHub:ClientSecret" "<client secret>" --project Maroos.Web
```

## Tests

```bash
dotnet test Maroos.slnx
```

No test calls the real Scryfall API: saved responses stand in for it. A few integration tests use real PostgreSQL, S3 and SMTP servers, and they're skipped unless you point them at those servers. To run everything:

```bash
docker compose up -d db s3 s3-bucket mailpit
```

Then set these environment variables and run `dotnet test Maroos.slnx` again:

| Variable | Value |
|---|---|
| `MAROOS_TEST_POSTGRES` | `Host=localhost;Port=5433;Username=postgres;Password=maroos-local` |
| `MAROOS_TEST_S3_URL` | `http://localhost:9000` |
| `MAROOS_TEST_S3_BUCKET` | `maroos-photos` |
| `MAROOS_TEST_S3_ACCESS_KEY` | `maroos` |
| `MAROOS_TEST_S3_SECRET_KEY` | `maroos-local-secret` |
| `MAROOS_TEST_SMTP_HOST` | `localhost` |
| `MAROOS_TEST_SMTP_PORT` | `1025` |
| `MAROOS_TEST_MAILPIT_URL` | `http://localhost:8025` |

## Production-like stack in Docker

```bash
docker compose up --build
```

This runs PostgreSQL, an S3-compatible photo store, Mailpit and the app, which is at http://localhost:8080. To stop everything and delete the local data:

```bash
docker compose down -v
```

The passwords in `compose.yaml` are for local use only.

## Deploying

Maroos ships as one Docker image. It needs four things from its host:

- PostgreSQL
- an S3-compatible bucket for photos
- an SMTP email provider
- HTTPS in front of it

[DEPLOY.md](DEPLOY.md) has every setting, and step-by-step instructions for [Render](https://render.com) and [Fly.io](https://fly.io). Run a single instance: live bidding and the auction closer run inside the app.

## Project layout

```
Maroos.Core/     Rules and models with no web or database code: Scryfall client, deck parser, deck stats and legality, bidding rules, email text
Maroos.Web/      The website: Blazor pages, services, EF Core data and migrations, SignalR hub, background jobs
Maroos.Tests/    xUnit and bUnit tests, with saved Scryfall responses and test helpers
docs/            Design notes (email-design.md)
DEVELOPMENT.md   Conventions and notes for working on the code
DEPLOY.md        Hosting guide and settings reference
```

## Contributing

Read [DEVELOPMENT.md](DEVELOPMENT.md) first. The key rules:

- **Money** is stored as `long` cents, never as a floating-point number.
- **Business rules** live in `Maroos.Core`.
- **Scryfall** is only reached through `CardService`.
- **Pages that change data** require login and check who owns the data.
- **Database migrations:** every model change needs one for SQLite and one for PostgreSQL. A test fails if either is missing.

## Credits

Card data and images are from [Scryfall](https://scryfall.com). Prices are Scryfall's daily market prices and may be out of date.

Maroos is unofficial Fan Content permitted under the [Fan Content Policy](https://company.wizards.com/en/legal/fancontentpolicy). Not approved/endorsed by Wizards. Portions of the materials used are property of Wizards of the Coast. ©Wizards of the Coast LLC.
