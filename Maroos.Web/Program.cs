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

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<IdentityRedirectManager>();
builder.Services.AddScoped<AuthenticationStateProvider, IdentityRevalidatingAuthenticationStateProvider>();

// ASP.NET Core Identity (local accounts) is the primary auth; GitHub is an optional external login on top.
var authBuilder = builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = IdentityConstants.ApplicationScheme;
        options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
    });

var gitHubClientId = builder.Configuration["GitHub:ClientId"];
var gitHubClientSecret = builder.Configuration["GitHub:ClientSecret"];
if (!string.IsNullOrEmpty(gitHubClientId) && !string.IsNullOrEmpty(gitHubClientSecret))
{
    authBuilder.AddGitHub(options =>
    {
        options.ClientId = gitHubClientId;
        options.ClientSecret = gitHubClientSecret;
        options.Scope.Add("user:email");
    });
}

authBuilder.AddIdentityCookies();

// SQLite in development, PostgreSQL in production (Database:Provider). The factory lets interactive components
// create a short-lived DbContext per operation; ApplicationDbContext is also registered as scoped for Identity.
builder.Services.AddMaroosDatabase(builder.Configuration, builder.Environment);
builder.Services.AddDatabaseDeveloperPageExceptionFilter();

// Login cookies are signed with Data Protection keys; keep them in the database so they survive redeploys.
builder.Services.AddDataProtection()
    .SetApplicationName("Maroos")
    .PersistKeysToDbContext<ApplicationDbContext>();

builder.Services.AddHealthChecks().AddDbContextCheck<ApplicationDbContext>("database");

// Card data: Scryfall through one throttled client, then the DB/memory cache in CardService.
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<ScryfallThrottle>();
builder.Services.AddHttpClient<ScryfallClient>(client =>
{
    client.BaseAddress = new Uri("https://api.scryfall.com/");
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Maroos/0.1");
    client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
});
builder.Services.AddScoped<CardService>();
builder.Services.AddScoped<DeckService>();
builder.Services.AddScoped<AuctionService>();
builder.Services.AddScoped<BiddingService>();
builder.Services.AddScoped<WatchService>();
builder.Services.AddSignalR();
builder.Services.AddSingleton<AuctionEvents>();
builder.Services.AddHostedService<AuctionCloser>();

// Auction photos: re-encoded, then stored on disk (Uploads:Storage=Local) or in an S3-compatible bucket (S3).
var photoOptions = PhotoStorageOptions.From(builder.Configuration);
builder.Services.AddSingleton(photoOptions);
if (photoOptions.Storage.Equals("S3", StringComparison.OrdinalIgnoreCase))
{
    if (string.IsNullOrWhiteSpace(photoOptions.S3.Bucket))
        throw new InvalidOperationException("Uploads:Storage is S3 but no bucket is set (Uploads__S3__Bucket or BUCKET_NAME).");
    builder.Services.AddSingleton(S3PhotoBlobStore.CreateClient(photoOptions.S3));
    builder.Services.AddSingleton<IPhotoBlobStore>(sp => new S3PhotoBlobStore(sp.GetRequiredService<Amazon.S3.IAmazonS3>(), photoOptions.S3));
}
else
{
    builder.Services.AddSingleton<IPhotoBlobStore>(new LocalPhotoBlobStore(Path.Combine(builder.Environment.ContentRootPath, photoOptions.Path)));
}
builder.Services.AddSingleton<AuctionPhotoStore>();

// Email: queued in the OutgoingEmails table and sent in the background over SMTP (Mailpit locally, any provider in production).
var emailOptions = EmailOptions.Bind(builder.Configuration);
if (!builder.Environment.IsDevelopment() && emailOptions.MissingSettings() is { Count: > 0 } missingEmailSettings)
    throw new InvalidOperationException($"Email isn't configured: set {string.Join(", ", missingEmailSettings)} (see DEPLOY.md).");
builder.Services.AddSingleton(emailOptions);
builder.Services.AddSingleton<EmailSignal>();
builder.Services.AddSingleton<EmailOutbox>();
builder.Services.AddSingleton<IEmailTransport, SmtpEmailTransport>();
builder.Services.AddSingleton<EmailDispatcher>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<EmailDispatcher>());

builder.Services.AddIdentityCore<ApplicationUser>(options =>
    {
        // New accounts confirm their email before logging in (IdentityEmailSender sends the link).
        options.SignIn.RequireConfirmedAccount = true;
        options.User.RequireUniqueEmail = true;
        options.Password.RequiredLength = 8;
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
        options.Stores.SchemaVersion = IdentitySchemaVersions.Version3;
    })
    .AddEntityFrameworkStores<ApplicationDbContext>()
    .AddSignInManager()
    .AddDefaultTokenProviders();

builder.Services.AddSingleton<IEmailSender<ApplicationUser>, IdentityEmailSender>();

var app = builder.Build();

await app.MigrateIfConfiguredAsync();

app.Use(async (context, next) =>
{
    // Basic hardening for every response. Blazor sends the frame-ancestors CSP itself (configured below).
    var headers = context.Response.Headers;
    headers.XContentTypeOptions = "nosniff";
    headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    headers["X-Frame-Options"] = "DENY";
    headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    await next();
});

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseMigrationsEndPoint();
}
else
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
// In a container the host's proxy handles HTTPS (and redirects to it); the app itself only speaks HTTP on 8080.
if (!app.Configuration.GetValue<bool>("DOTNET_RUNNING_IN_CONTAINER"))
    app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();

// Auction photos, from disk or the S3 bucket. Keys are random and never reused, so they cache forever.
app.MapGet("/uploads/{key}", async (string key, AuctionPhotoStore photos, HttpContext context, CancellationToken ct) =>
{
    var photo = await photos.OpenAsync(key, ct);
    if (photo is null)
        return Results.NotFound();
    context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
    return Results.Stream(photo, "image/jpeg");
});

app.MapHealthChecks("/health");
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode(options => options.ContentSecurityFrameAncestorsPolicy = "'none'");

app.MapHub<AuctionHub>(AuctionHub.Path);

// Add additional endpoints required by the Identity /Account Razor components.
app.MapAdditionalIdentityEndpoints();

app.Run();
