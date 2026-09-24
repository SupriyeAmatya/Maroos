# Deploying Maroos

Maroos ships as one Docker image (`Dockerfile`) that needs four things from its host:

1. **PostgreSQL** for everything the site stores.
2. **An S3-compatible bucket** for auction photos (AWS S3, Cloudflare R2, Backblaze B2, Fly Tigris…). The bucket can stay private: the app serves photos itself at `/uploads/…`.
3. **HTTPS in front of it.** Render and Fly.io both provide this.
4. **An SMTP email provider** for account and auction emails (Postmark, Brevo, SendGrid, Amazon SES…).

Run it locally first with `docker compose up --build` (PostgreSQL + an S3-compatible gateway + the app) and open http://localhost:8080.

## Settings

Set these as environment variables (double underscore = nested setting).

| Variable | Required | What it is |
|---|---|---|
| `ConnectionStrings__DefaultConnection` | yes (or `DATABASE_URL`) | PostgreSQL connection. Npgsql form (`Host=…;Database=…;Username=…;Password=…`) or a `postgres://user:pass@host:5432/db?sslmode=require` URL. If unset, `DATABASE_URL` is used. |
| `Database__MigrateOnStartup` | recommended | `true` applies database migrations when the app starts. Fine for one instance; see *Migrations* for more. |
| `ASPNETCORE_FORWARDEDHEADERS_ENABLED` | yes behind a proxy | `true` so the app knows requests arrived over HTTPS (login cookies, GitHub callback URLs). |
| `Uploads__S3__Bucket` | yes (or `BUCKET_NAME`) | Bucket name. |
| `Uploads__S3__ServiceUrl` | for non-AWS (or `AWS_ENDPOINT_URL_S3`) | Endpoint, e.g. `https://<account>.r2.cloudflarestorage.com` (R2), `https://fly.storage.tigris.dev` (Tigris), `https://s3.us-west-004.backblazeb2.com` (B2). Leave empty for AWS. |
| `Uploads__S3__Region` | usually | AWS region, `auto` for R2 and Tigris. Default `us-east-1`. |
| `Uploads__S3__AccessKey` / `Uploads__S3__SecretKey` | yes, unless `AWS_ACCESS_KEY_ID` / `AWS_SECRET_ACCESS_KEY` or an IAM role are available | Bucket credentials. |
| `Uploads__S3__ForcePathStyle` | rarely | `true` for MinIO and some self-hosted stores. |
| `Uploads__S3__Prefix` | no | Key prefix, default `auction-photos/`. |
| `Email__From` | yes | Sender, e.g. `Maroos <no-reply@yourdomain.com>`. Use an address on a domain you've verified with your provider. |
| `Email__PublicBaseUrl` | yes | The site's address as users reach it, e.g. `https://maroos.example`. Links in auction emails use it. |
| `Email__Smtp__Host` / `Email__Smtp__Port` | yes | The provider's SMTP server, usually port `587`. |
| `Email__Smtp__Username` / `Email__Smtp__Password` | yes | SMTP credentials from the provider. |
| `Email__Smtp__Security` | no | `StartTls` (default, port 587), `SslOnConnect` (port 465) or `None`. |
| `GitHub__ClientId` / `GitHub__ClientSecret` | no | Enables "Log in with GitHub". Leave unset for email/password only. |

Already set by the image: `ASPNETCORE_ENVIRONMENT=Production`, port `8080`, `Database__Provider=Postgres`, `Uploads__Storage=S3`.

The app refuses to start if it's missing the database connection or the bucket name, and `/health` returns 200 only when the database is reachable.

## Option A: Render

1. Push this repository to GitHub.
2. In Render: **New → Blueprint**, choose the repository. `render.yaml` creates the web service and a PostgreSQL database and wires the connection string.
3. When asked, fill in the `Uploads__S3__*` values for your bucket (and GitHub values if you want GitHub login).
4. Choose plans in the dashboard. The web service must stay running (no sleeping plan) for live bidding and the auction closer.
5. Deploy. Watch the log for `Applying … database migration(s)`, then open `https://<your-service>.onrender.com/health`.

## Option B: Fly.io

```bash
fly launch --no-deploy --copy-config   # keeps fly.toml; pick an app name and region
fly postgres create --name maroos-db   # or Neon/Supabase: then set DATABASE_URL yourself
fly postgres attach maroos-db          # sets the DATABASE_URL secret, which Maroos reads
fly storage create                     # Tigris bucket; sets BUCKET_NAME, AWS_ENDPOINT_URL_S3, AWS_REGION and keys
fly deploy
```

Maroos picks up `DATABASE_URL`, `BUCKET_NAME`, `AWS_ENDPOINT_URL_S3`, `AWS_REGION`, `AWS_ACCESS_KEY_ID` and `AWS_SECRET_ACCESS_KEY` on its own, so there's nothing to copy by hand. `fly.toml` keeps one machine running and checks `/health`.

## GitHub login in production

In GitHub → Settings → Developer settings → OAuth Apps, add a production OAuth app (or edit yours):

- Homepage URL: `https://<your-domain>`
- Authorization callback URL: `https://<your-domain>/signin-github`

Then set `GitHub__ClientId` and `GitHub__ClientSecret` on the host.

## Email

Maroos sends sign-up confirmations, password resets, email-change confirmations and auction notices (outbid, won, sold, ended unsold) over SMTP. Emails are queued in the `OutgoingEmails` table and sent by a background worker. A failed send is retried after 1, 5, 15, 60 and 180 minutes, then given up (the log says so). Sent emails are deleted after 7 days.

1. Pick a provider and create an account. Common SMTP settings:

   | Provider | Host | Username / password |
   |---|---|---|
   | Postmark | `smtp.postmarkapp.com` | server API token for both |
   | Brevo | `smtp-relay.brevo.com` | your SMTP login and SMTP key |
   | SendGrid | `smtp.sendgrid.net` | `apikey` and an API key |
   | Amazon SES | `email-smtp.<region>.amazonaws.com` | SES SMTP credentials |

2. Verify your sending domain with the provider (it gives you SPF and DKIM DNS records to add). Without this, emails land in spam or are refused.
3. Set the `Email__*` settings above. The app refuses to start in production without `Email__From`, `Email__PublicBaseUrl` and `Email__Smtp__Host`.
4. Register a new account on the live site: the confirmation email should arrive within a few seconds.

Locally, `docker compose up` includes Mailpit, which catches every email. Read them at http://localhost:8025.

## Migrations

There are two migration sets, and they must stay in step:

- `Maroos.Web/Data/Migrations` for SQLite (development), context `ApplicationDbContext`
- `Maroos.Web/Data/Migrations/Postgres` for PostgreSQL (production), context `PostgresDbContext`

After changing the model, add both:

```bash
dotnet ef migrations add <Name> --context ApplicationDbContext --project Maroos.Web
dotnet ef migrations add <Name> --context PostgresDbContext --project Maroos.Web --output-dir Data/Migrations/Postgres
```

`dotnet test` fails if either set is missing a change.

With `Database__MigrateOnStartup=true` the app migrates itself on start. With several instances, turn that off and migrate once per release instead:

```bash
ConnectionStrings__DefaultConnection="<production connection>" Database__Provider=Postgres \
  dotnet ef database update --context PostgresDbContext --project Maroos.Web
```

## After the first deploy

1. `/health` says `Healthy`.
2. Register an account, build a deck, paste a list.
3. List a card with a photo, then open the listing in a private window: the photo loads.
4. From a second account, bid; the first window updates without refreshing.
5. The footer shows the Scryfall credit and the Fan Content Policy notice.
6. Register with a real address you control: the confirmation email arrives, and "Forgot your password?" sends a reset link.

## Things to know

- **One instance.** Live bid updates and the auction closer run inside the app, and Blazor keeps each visitor on one server connection. Running several instances needs sticky sessions and a SignalR backplane (e.g. Redis); until then keep a single instance, sized up as needed.
- **Email.** New accounts must confirm their email before logging in. Accounts that existed before email was added were marked confirmed. People can turn auction emails off in their account settings; account emails always send.
- **Payments.** Maroos doesn't take payment. When an auction ends, the winner and seller see each other's email and settle between themselves.
- **Login keys.** The keys that sign login cookies are stored in the `DataProtectionKeys` table, so everyone stays signed in across redeploys. They're stored unencrypted (the app logs a one-time warning about it); anyone who can read your database could forge a login, so treat database access as the keys to the site.
- **Backups.** Turn on your database host's automated backups, and versioning or backups on the photo bucket: a lost photo shows as a broken image on its listing.
- **Scryfall and Wizards.** Card data and images come from Scryfall's free API: keep it free to view and keep the footer credit. The footer carries the Wizards of the Coast Fan Content Policy notice, which the policy requires.
