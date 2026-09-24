# Email for Maroos: design

**Date:** 2026-09-24  
**Status:** Implemented.  
**Scope:** The first item of the "what to add next" list: real email.

## 1. Goal

Maroos sends no email today. `IdentityNoOpEmailSender` drops every message. That leaves three gaps:

- Someone who forgets their password is locked out for good.
- Email addresses are never confirmed, even though winners and sellers contact each other by email.
- Nobody hears about outbids or auction results unless they have the page open.

This design adds real email for the account flows and for auction notices. Everything is sent over SMTP, so any email provider works. On development machines and in Docker, a local test inbox catches every message.

## 2. Decisions

| Question | Decision |
|---|---|
| Which emails | Password reset, sign-up confirmation, email-change confirmation, and auction notices (outbid, won, sold, ended unsold) |
| How to send | SMTP (MailKit), provider-agnostic. Mailpit catches everything locally and in Docker |
| Existing accounts | Marked as confirmed once, so nobody is locked out |
| Notification controls | One "Email me about my bids and auctions" switch, on by default. Account emails always send |
| Outbid frequency | At most one outbid email per auction per person, until they bid on it again |
| Delivery | A database-backed outbox plus a background sender, with retries |

## 3. What users experience

### 3.1 Account emails

- **Sign-up:** after registering, the user sees "Check your inbox". They can't log in until they click the confirmation link.
  - This also applies to accounts created through GitHub login (the template's existing flow).
- **Logging in before confirming:** the login page says the email isn't confirmed yet and links to "Resend confirmation email". Today this case falls through to "Invalid login attempt", because `Login.razor` doesn't handle `result.IsNotAllowed`.
- **Forgot password:** a reset link valid for 1 day, which is Identity's default token lifespan.
- **Changing email** (`/Account/Manage/Email`): the new address must be confirmed before it takes effect. The template already does this once a real sender exists.
- **Existing accounts:** marked confirmed by the migration and unaffected.

### 3.2 Auction emails

All auction emails are governed by `ApplicationUser.AuctionEmails` (default `true`), which can be edited on `/Account/Manage`. Each email links to the auction and ends with "Turn these emails off in your account settings: {base}/Account/Manage".

| Email | Recipient | When | Content |
|---|---|---|---|
| You've been outbid | The previous high bidder | After a bid by someone else commits | Card, new high bid, minimum next bid, time left, link |
| You won | The winner | When the auction closes (`CloseExpiredAsync`) | Card, winning bid, seller's display name and email, link |
| Your card sold | The seller | When the auction closes | Card, final price, winner's display name and email, link |
| No bids | The seller | When the auction closes with no bids | Card, link |

**Outbid rule.** Send only if both of these hold:

- the previous high bidder is not the new bidder;
- their `AuctionEmails` switch is on.

"At most once per auction until they bid again" follows from this without keeping any state. Only the *previous* high bidder is emailed. Once outbid, a person can't become the high bidder again except by bidding. And `BidRules` never lets the current high bidder bid again.

**Privacy.** The winner/seller emails contain exactly the contact details that `AuctionView.ContactEmail` already shows each of them. No other email address ever appears in any email.

**Not included:** cancelled auctions (they can't have bids), one-click unsubscribe links, branded templates.

## 4. Architecture

```
page / service ──► EmailOutbox.Queue(...) ──► OutgoingEmails table
                                                   │  (nudge)
EmailDispatcher (hosted service, every 5 s) ◄──────┘
    ├─ picks due rows (not sent, attempts < max, NextAttemptAtUtc ≤ now)
    ├─ IEmailTransport.SendAsync ──► SmtpEmailTransport (MailKit) ──► Mailpit / provider
    ├─ success: SentAtUtc = now, bodies cleared
    ├─ failure: Attempts++, NextAttemptAtUtc = now + EmailRetry.Delay(Attempts), LastError
    └─ hourly: delete rows sent more than 7 days ago
```

### 4.1 Maroos.Core/Email (pure, fully unit-tested)

- `OutgoingEmail`: the entity (see §5).
- `EmailKind` enum: `ConfirmAccount`, `PasswordReset`, `ConfirmEmailChange`, `Outbid`, `AuctionWon`, `AuctionSold`, `AuctionUnsold`.
- `EmailRetry`: the retry delays after failed attempt 1, 2, 3, 4 and 5 are 1, 5, 15, 60 and 180 minutes. `MaxAttempts = 6`, after which the dispatcher gives up.
- `OutbidRule.ShouldNotify(previousHighBidderId, newBidderId, previousBidderOptedIn)`.
- `AuctionEmailContent`: builds the subject, plain-text body and HTML body for each auction email from plain values (card name, set, amounts in `long` cents via `Money.FormatUsd`, names, URLs). All user-supplied text is HTML-encoded in the HTML body.

### 4.2 Maroos.Web/Services/Email

- **`EmailOptions`**, bound from `Email`:
  - `From`
  - `PublicBaseUrl`
  - `Smtp`: `Host`, `Port`, `Username`, `Password`, `Security` (`StartTls` | `SslOnConnect` | `None`)
- **`EmailOutbox`**: `QueueAsync(...)` adds an `OutgoingEmail`, either to a caller-supplied `ApplicationDbContext` (so it saves with the caller's changes) or in its own context. After saving, it nudges the dispatcher.
- **`EmailDispatcher`** (`BackgroundService`):
  - Runs every 5 s, and wakes early when nudged.
  - Sends due emails one at a time. Single instance only, as documented for the rest of the app.
  - Test web hosts remove hosted services (existing rule), so tests drive it through a `SendDueAsync()` method.
- **`IEmailTransport`**: implemented by `SmtpEmailTransport` (MailKit). The message is `multipart/alternative`, with a text part and an HTML part.
- **`IdentityEmailSender : IEmailSender<ApplicationUser>`**:
  - Replaces `IdentityNoOpEmailSender`, which is deleted, along with its `RegisterConfirmation.razor` branch (as the template comment instructs).
  - Queues `ConfirmAccount` / `PasswordReset` / `ConfirmEmailChange` emails.
  - The template pages pass links that are already HTML-encoded (`HtmlEncoder.Default.Encode(callbackUrl)`). The sender decodes them once (`WebUtility.HtmlDecode`). The content builders put the raw link in the text part and encode it themselves for the HTML part.

### 4.3 Changes to existing code

- **`BiddingService.PlaceBidAsync`:** the transaction already reads the previous high bid. After commit, alongside `events.PublishAsync`, it queues the outbid email if `OutbidRule` says so. The queue write uses its own context, so it can never undo or retry a bid. A failure is logged.
- **`AuctionService.CloseExpiredAsync`:** queues won/sold/unsold emails into the same context as the status change. They save in the same `SaveChangesAsync`, so they are queued exactly once. Recipient emails and opt-ins are loaded with the existing single bids query (no per-auction queries; `QueryCountTests` still apply).
- **`Program.cs`:**
  - sets `RequireConfirmedAccount = true`;
  - registers `EmailOptions`, `EmailOutbox`, `EmailDispatcher`, `IEmailTransport` and `IdentityEmailSender`;
  - fails fast outside Development when `Email:Smtp:Host`, `Email:From` or `Email:PublicBaseUrl` is missing.
- **Account pages:**
  - `Login.razor` handles `IsNotAllowed`.
  - `RegisterConfirmation.razor` says "check your inbox".
  - `Manage/Index.razor` gets the `AuctionEmails` checkbox.
- **`ApplicationUser`:** gets `bool AuctionEmails { get; set; } = true;`.

## 5. Data

### New table `OutgoingEmails`

| Column | Type | Notes |
|---|---|---|
| `Id` | Guid | Key |
| `Kind` | int (`EmailKind`) | |
| `ToAddress` | string (≤ 256) | |
| `Subject` | string (≤ 200) | |
| `TextBody` / `HtmlBody` | string, nullable | Cleared once sent |
| `UserId` | string, nullable | Recipient account (FK → user, cascade) |
| `AuctionId` | Guid, nullable | For auction emails (tracing and tests). No FK: a queued email doesn't need its auction row to exist |
| `CreatedAtUtc`, `NextAttemptAtUtc`, `SentAtUtc` | DateTime | `SentAtUtc` is null until sent |
| `Attempts` | int | |
| `LastError` | string (≤ 500), nullable | |

Index: `(SentAtUtc, NextAttemptAtUtc)`, for finding due rows.

### Other schema changes

- **`AspNetUsers.AuctionEmails`:** bool, default `true`.
- **Migrations:** both sets, per DEVELOPMENT.md:
  - `dotnet ef migrations add Email --context ApplicationDbContext --project Maroos.Web`
  - `dotnet ef migrations add Email --context PostgresDbContext --project Maroos.Web --output-dir Data/Migrations/Postgres`

  Each also runs `UPDATE "AspNetUsers" SET "EmailConfirmed" = TRUE, "AuctionEmails" = TRUE` (SQLite: `= 1`). Existing accounts stay usable and start with auction emails on. EF's generated column default for a new `bool` column is `false`, so the update sets it.

## 6. Configuration and deployment

| Setting | Development | Docker (compose) | Production |
|---|---|---|---|
| `Email__From` | `Maroos <no-reply@maroos.local>` | same | required |
| `Email__PublicBaseUrl` | `http://localhost:5126` | `http://localhost:8080` | required, e.g. `https://maroos.example` |
| `Email__Smtp__Host` / `Port` | `localhost` / `1025` | `mailpit` / `1025` | required (provider) |
| `Email__Smtp__Username` / `Password` | none | none | provider credentials (secrets) |
| `Email__Smtp__Security` | `None` | `None` | usually `StartTls` (port 587) |

- **`compose.yaml`:** a new `mailpit` service (`axllent/mailpit`), with SMTP on 1025 and the web inbox at http://localhost:8025. The `web` service gets the settings above. For `dotnet watch` development, run `docker compose up -d mailpit`.
- **`render.yaml` / `fly.toml`:** the `Email__*` entries, with secrets marked `sync: false` or set via `fly secrets set`.
- **`DEPLOY.md`:** a new "Email" section. It covers choosing a provider (Postmark, Brevo, SendGrid, Amazon SES…), verifying the sending domain (SPF/DKIM) so mail isn't marked as spam, and the settings table.
- **`DEVELOPMENT.md`:** the Auth section is updated (confirmation on, real sender), and a short Email section is added.

## 7. Error handling

- **SMTP down or rejected:** the row is retried with backoff and gives up after 6 attempts. The dispatcher logs a warning on each failure and an error on giving up. Users and bids are never affected.
- **Invalid recipient address:** it fails like any other send and is retried, then given up. Identity already validates addresses.
- **App restart mid-send:** a row is only marked sent after `SendAsync` succeeds. A crash between the send and saving the row can therefore cause, at worst, one duplicate email, which is acceptable.
- **Missing config in production:** startup fails with a message naming the missing setting.

## 8. Testing

- **Core:**
  - `EmailRetry` delays and the give-up point.
  - Every branch of `OutbidRule`.
  - `AuctionEmailContent` for each kind: subject, amounts, link, and HTML-encoding of `<script>`-style card and display names.
- **Services** (`AuctionWorld` with a `RecordingEmailTransport`):
  - Outbid emails: queued for the previous bidder only. In a three-way bidding war, each person gets one email per time they're outbid. Nobody is emailed about their own bid, and nothing is sent when opted out.
  - Closing queues won + sold, or unsold, exactly once (running the closer twice sends nothing new).
  - The dispatcher sends due rows, sets `SentAtUtc`, clears bodies, retries with the right delays using `ManualClock`, gives up after max attempts, and purges old rows.
  - `QueryCountTests` still pass for the closer.
- **HTTP** (`MaroosWebFactory`):
  - Registering queues a confirmation email, and logging in before confirming shows the new message.
  - Following the link from the queued email confirms the account, and login then works.
  - Forgot password → queued link → reset → login with the new password.
  - The Manage page saves the `AuctionEmails` switch.
- **Existing tests:** `PageForms.RegisterAsync` gains automatic confirmation, reading the queued email and following its link, so tests that expect to be signed in after registering keep working.
- **Optional integration test:** sends through a real Mailpit and reads it back via Mailpit's HTTP API. It is skipped unless `MAROOS_TEST_SMTP_HOST` (and the Mailpit API URL) are set, like the Postgres and S3 tests.
- **Migrations:** `MigrationsAreCompleteTests` covers the model. The "existing accounts confirmed" update is checked by migrating a database that has an unconfirmed user from before the migration, on SQLite in a test and on Postgres in the optional Postgres tests.

## 9. Out of scope

- Unsubscribe links and headers
- Email for cancelled auctions
- Digest or batched emails
- Branded HTML templates
- Multiple app instances
