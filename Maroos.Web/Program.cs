using Maroos.Core.Scryfall;
using Maroos.Web.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
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

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection") ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");
// The factory lets interactive components create a short-lived DbContext per operation; it also
// registers ApplicationDbContext as scoped for Identity.
builder.Services.AddDbContextFactory<ApplicationDbContext>(options =>
    options.UseSqlite(connectionString));
builder.Services.AddDatabaseDeveloperPageExceptionFilter();

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

// Auction photos: saved re-encoded under Uploads:Path and served at Uploads:RequestPath.
var photoOptions = builder.Configuration.GetSection("Uploads").Get<PhotoStorageOptions>() ?? new PhotoStorageOptions();
builder.Services.AddSingleton(photoOptions);
builder.Services.AddSingleton<AuctionPhotoStore>();

builder.Services.AddIdentityCore<ApplicationUser>(options =>
    {
        // No real email sender yet (IdentityNoOpEmailSender), so don't block sign-in on email confirmation.
        // Flip this back to true once a real IEmailSender is registered.
        options.SignIn.RequireConfirmedAccount = false;
        options.User.RequireUniqueEmail = true;
        options.Password.RequiredLength = 8;
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
        options.Stores.SchemaVersion = IdentitySchemaVersions.Version3;
    })
    .AddEntityFrameworkStores<ApplicationDbContext>()
    .AddSignInManager()
    .AddDefaultTokenProviders();

builder.Services.AddSingleton<IEmailSender<ApplicationUser>, IdentityNoOpEmailSender>();

var app = builder.Build();

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
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();

// MapStaticAssets only serves files that exist at build time; uploaded photos need their own mapping.
var photoStore = app.Services.GetRequiredService<AuctionPhotoStore>();
Directory.CreateDirectory(photoStore.Root);
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(photoStore.Root),
    RequestPath = photoOptions.RequestPath,
    OnPrepareResponse = context =>
    {
        context.Context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Context.Response.Headers.CacheControl = "public, max-age=604800";
    },
});
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapHub<AuctionHub>(AuctionHub.Path);

// Add additional endpoints required by the Identity /Account Razor components.
app.MapAdditionalIdentityEndpoints();

app.Run();
