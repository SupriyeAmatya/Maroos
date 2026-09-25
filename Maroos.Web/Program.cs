// =====================================================================================================
// FILE: Maroos.Web/Program.cs
// PURPOSE: The app's entry point. It runs once at startup and does two jobs:
//
//   PART 1 — REGISTER SERVICES ("dependency injection"). Each builder.Services.AddXxx line tells
//   ASP.NET Core how to create something the app needs (database, Scryfall client, deck service...).
//   Pages and services then just ask for what they need in their constructor / @inject, and ASP.NET
//   creates and hands it over. Lifetimes:
//     Singleton — one instance for the whole app (shared state: the throttle, the event hub, the photo store)
//     Scoped    — one per request / per Blazor circuit (the services that use the database)
//     Transient — a new one every time (typed HttpClients such as ScryfallClient)
//
//   PART 2 — BUILD THE REQUEST PIPELINE ("middleware"). Every HTTP request flows through the
//   app.UseXxx steps in order (security headers, errors, HTTPS, antiforgery...), then reaches an
//   endpoint: a Blazor page, the /uploads photo endpoint, /health, or the SignalR hub.
//
// Top-level statements: there's no `class Program` / `Main` written out; C# generates them.
// =====================================================================================================

using Maroos.Core.Scryfall;
using Maroos.Web.Services;
using Maroos.Web.Services.Email;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection;
using Maroos.Web.Components;
using Maroos.Web.Components.Account;
using Maroos.Web.Data;

// builder: collects configuration (appsettings.json, environment variables, user secrets) and services.
var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Blazor: Razor components, with "Interactive Server" mode = pages that update live over a SignalR connection.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Makes the signed-in user (AuthenticationState) available to every component.
builder.Services.AddCascadingAuthenticationState();
// Template helpers for the Identity account pages (redirects, re-checking the user's login every 30 minutes).
builder.Services.AddScoped<IdentityRedirectManager>();
builder.Services.AddScoped<AuthenticationStateProvider, IdentityRevalidatingAuthenticationStateProvider>();

// ASP.NET Core Identity (local accounts) is the primary auth; GitHub is an optional external login on top.
// DefaultScheme: the normal login cookie. DefaultSignInScheme: the temporary cookie used mid-way through
// an external (GitHub) login, before the user's local account is linked.
var authBuilder = builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = IdentityConstants.ApplicationScheme;
        options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
    });

// GitHub login is only switched on when both secrets are configured (user-secrets in development,
// GitHub__ClientId / GitHub__ClientSecret environment variables in production).
var gitHubClientId = builder.Configuration["GitHub:ClientId"];
var gitHubClientSecret = builder.Configuration["GitHub:ClientSecret"];
if (!string.IsNullOrEmpty(gitHubClientId) && !string.IsNullOrEmpty(gitHubClientSecret))
{
    authBuilder.AddGitHub(options =>
    {
        options.ClientId = gitHubClientId;
        options.ClientSecret = gitHubClientSecret;
        // Ask GitHub for the user's email so Identity can create the local account.
        options.Scope.Add("user:email");
    });
}

// The cookies Identity uses (login cookie, external-login cookie, 2FA cookies).
authBuilder.AddIdentityCookies();

// SQLite in development, PostgreSQL in production (Database:Provider). The factory lets interactive components
// create a short-lived DbContext per operation; ApplicationDbContext is also registered as scoped for Identity.
// (See Data/DatabaseSetup.cs.)
builder.Services.AddMaroosDatabase(builder.Configuration, builder.Environment);
// In development, shows a helpful page for database errors (e.g. "apply migrations").
builder.Services.AddDatabaseDeveloperPageExceptionFilter();

// Login cookies are signed with Data Protection keys; keep them in the database so they survive redeploys.
// Without this, a container restart would create new keys and log everyone out.
builder.Services.AddDataProtection()
    .SetApplicationName("Maroos")
    .PersistKeysToDbContext<ApplicationDbContext>();

// /health endpoint support: reports Healthy only when the database answers.
builder.Services.AddHealthChecks().AddDbContextCheck<ApplicationDbContext>("database");

// Card data: Scryfall through one throttled client, then the DB/memory cache in CardService.
// TimeProvider.System: the real clock. Services ask for TimeProvider instead of calling DateTime.UtcNow,
// so tests can swap in a fake clock and "fast-forward" time.
builder.Services.AddSingleton(TimeProvider.System);
// In-memory cache used for search and autocomplete results (10 minutes).
builder.Services.AddMemoryCache();
// One throttle for the whole app (>= 100 ms between Scryfall calls).
builder.Services.AddSingleton<ScryfallThrottle>();
// A "typed HttpClient": ScryfallClient gets an HttpClient already configured with Scryfall's base address
// and the headers Scryfall asks every app to send (a User-Agent naming the app, and Accept: JSON).
builder.Services.AddHttpClient<ScryfallClient>(client =>
{
    client.BaseAddress = new Uri("https://api.scryfall.com/");
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Maroos/0.1");
    client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
});
// The app's own services (one instance per request / per Blazor circuit).
builder.Services.AddScoped<CardService>();
builder.Services.AddScoped<DeckService>();
builder.Services.AddScoped<AuctionService>();
builder.Services.AddScoped<BiddingService>();
builder.Services.AddScoped<WatchService>();
// SignalR: real-time messaging, used by the AuctionHub (/hubs/auctions).
builder.Services.AddSignalR();
// AuctionEvents: one shared "notice board" for bid/ending events, so every open auction page hears them.
builder.Services.AddSingleton<AuctionEvents>();
// AuctionCloser: a background task that runs for the app's whole life, closing ended auctions every minute.
builder.Services.AddHostedService<AuctionCloser>();

// Auction photos: re-encoded, then stored on disk (Uploads:Storage=Local) or in an S3-compatible bucket (S3).
// PhotoStorageOptions.From also picks up BUCKET_NAME / AWS_* variables that hosts like Fly.io set.
var photoOptions = PhotoStorageOptions.From(builder.Configuration);
builder.Services.AddSingleton(photoOptions);
if (photoOptions.Storage.Equals("S3", StringComparison.OrdinalIgnoreCase))
{
    // Fail at startup (with a clear message) rather than on the first upload.
    if (string.IsNullOrWhiteSpace(photoOptions.S3.Bucket))
        throw new InvalidOperationException("Uploads:Storage is S3 but no bucket is set (Uploads__S3__Bucket or BUCKET_NAME).");
    builder.Services.AddSingleton(S3PhotoBlobStore.CreateClient(photoOptions.S3));
    builder.Services.AddSingleton<IPhotoBlobStore>(sp => new S3PhotoBlobStore(sp.GetRequiredService<Amazon.S3.IAmazonS3>(), photoOptions.S3));
}
else
{
    // Development: a folder on disk (wwwroot/uploads by default, relative to the project folder).
    builder.Services.AddSingleton<IPhotoBlobStore>(new LocalPhotoBlobStore(Path.Combine(builder.Environment.ContentRootPath, photoOptions.Path)));
}
builder.Services.AddSingleton<AuctionPhotoStore>();

// Email: queued in the OutgoingEmails table and sent in the background over SMTP (Mailpit locally, any provider in production).
// EmailOptions comes from the "Email" settings (appsettings.Development.json points at Mailpit on localhost:1025).
var emailOptions = EmailOptions.Bind(builder.Configuration);
// Outside Development, stop at startup if a required email setting is missing (like the bucket check above).
if (!builder.Environment.IsDevelopment() && emailOptions.MissingSettings() is { Count: > 0 } missingEmailSettings)
    throw new InvalidOperationException($"Email isn't configured: set {string.Join(", ", missingEmailSettings)} (see DEPLOY.md).");
// The outbox saves emails, the signal wakes the dispatcher early, the transport talks SMTP, and the
// dispatcher (registered once as a singleton and once as a background service) sends what's due.
builder.Services.AddSingleton(emailOptions);
builder.Services.AddSingleton<EmailSignal>();
builder.Services.AddSingleton<EmailOutbox>();
builder.Services.AddSingleton<IEmailTransport, SmtpEmailTransport>();
builder.Services.AddSingleton<EmailDispatcher>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<EmailDispatcher>());

// ASP.NET Core Identity: user accounts, password hashing, lockout, 2FA, passkeys.
builder.Services.AddIdentityCore<ApplicationUser>(options =>
    {
        // New accounts confirm their email before logging in (IdentityEmailSender sends the link).
        options.SignIn.RequireConfirmedAccount = true;
        // One account per email address.
        options.User.RequireUniqueEmail = true;
        // Passwords of at least 8 characters.
        options.Password.RequiredLength = 8;
        // 5 wrong passwords in a row lock the account for 15 minutes (stops password guessing).
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
        // Version3 of Identity's tables includes passkeys; the migrations were generated with this setting.
        options.Stores.SchemaVersion = IdentitySchemaVersions.Version3;
    })
    // Store users in our ApplicationDbContext (the AspNetUsers table etc.).
    .AddEntityFrameworkStores<ApplicationDbContext>()
    .AddSignInManager()
    // Tokens for password reset, email confirmation, 2FA.
    .AddDefaultTokenProviders();

// Identity's account emails (confirm, reset, email change) are queued in the outbox (Services/Email/IdentityEmailSender.cs).
builder.Services.AddSingleton<IEmailSender<ApplicationUser>, IdentityEmailSender>();

// ===== PART 2: build the app and its request pipeline =====
var app = builder.Build();

// If Database:MigrateOnStartup=true (as in Docker / Render / Fly), bring the database schema up to date now.
await app.MigrateIfConfiguredAsync();

// Custom middleware: add security headers to EVERY response, then pass the request on (`await next()`).
app.Use(async (context, next) =>
{
    // Basic hardening for every response. Blazor sends the frame-ancestors CSP itself (configured below).
    var headers = context.Response.Headers;
    // Browsers must trust the declared content type (stops a photo being run as a script).
    headers.XContentTypeOptions = "nosniff";
    // Other sites only learn our domain, not full page URLs, when users follow links.
    headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    // The site may not be shown inside another site's frame (anti-clickjacking).
    headers["X-Frame-Options"] = "DENY";
    // The site never needs the camera, microphone or location.
    headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    await next();
});

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    // Development: a page that offers to apply pending migrations.
    app.UseMigrationsEndPoint();
}
else
{
    // Production: show the friendly /Error page instead of error details.
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    // HSTS tells browsers "only ever use HTTPS for this site".
    app.UseHsts();
}
// 404s etc. render the app's own not-found page.
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
// In a container the host's proxy handles HTTPS (and redirects to it); the app itself only speaks HTTP on 8080.
if (!app.Configuration.GetValue<bool>("DOTNET_RUNNING_IN_CONTAINER"))
    app.UseHttpsRedirection();

// Checks antiforgery tokens on form posts (protects against cross-site request forgery).
app.UseAntiforgery();

// Serves the static files that exist at build time (CSS, JavaScript, Bootstrap, favicon), fingerprinted and compressed.
app.MapStaticAssets();

// Auction photos, from disk or the S3 bucket. Keys are random and never reused, so they cache forever.
// Minimal API endpoint: GET /uploads/{key}. AuctionPhotoStore.OpenAsync only accepts keys it generated
// (32 hex characters + ".jpg"), so a request like /uploads/../appsettings.json can never read other files.
app.MapGet("/uploads/{key}", async (string key, AuctionPhotoStore photos, HttpContext context, CancellationToken ct) =>
{
    var photo = await photos.OpenAsync(key, ct);
    if (photo is null)
        return Results.NotFound();
    // One year, "immutable": browsers and CDNs never need to re-check a photo.
    context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
    return Results.Stream(photo, "image/jpeg");
});

// Health check for hosting platforms (Render / Fly call it to know the app is up).
app.MapHealthChecks("/health");
// All Blazor pages, starting from the App component. Interactive pages use the Server render mode.
// ContentSecurityFrameAncestorsPolicy = 'none' makes Blazor's CSP header forbid framing, matching X-Frame-Options.
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode(options => options.ContentSecurityFrameAncestorsPolicy = "'none'");

// Live auction updates for non-Blazor clients (see Services/AuctionEvents.cs).
app.MapHub<AuctionHub>(AuctionHub.Path);

// Add additional endpoints required by the Identity /Account Razor components.
// (logout, external login callbacks, passkey endpoints...)
app.MapAdditionalIdentityEndpoints();

// Start listening for requests. This call blocks until the app shuts down.
app.Run();
