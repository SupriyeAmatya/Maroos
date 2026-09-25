// =====================================================================================================
// FILE: Maroos.Tests/Auth/IdentityLoginTests.cs
// PURPOSE: Two things:
//   1. MaroosWebFactory — starts the WHOLE web app in memory for tests (WebApplicationFactory), with a
//      temporary SQLite file, a temporary uploads folder, the fake Scryfall, and no background jobs.
//      Most HTTP and page tests use it.
//   2. IdentityLoginTests — checks the account rules configured in Program.cs: confirmed accounts log in,
//      unconfirmed ones can't, wrong passwords rejected, lockout after 5 failures, unique emails, 8-char passwords.
// =====================================================================================================

using Maroos.Core.Scryfall;
using Maroos.Tests.Scryfall;
using Maroos.Web.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Maroos.Tests.Auth;

// MaroosWebFactory: the real app (Program.cs) running inside the test process, reachable with an HttpClient.
public class MaroosWebFactory : WebApplicationFactory<Program>
{
    // A unique database file per factory, deleted in Dispose.
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"maroos-test-{Guid.NewGuid():N}.db");

    // A unique folder for uploaded photos.
    public string UploadsPath { get; } = Path.Combine(Path.GetTempPath(), $"maroos-uploads-{Guid.NewGuid():N}");

    // ConfigureWebHost: override settings and services before the app starts.
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:DefaultConnection", $"DataSource={_dbPath}");
        builder.UseSetting("Uploads:Path", UploadsPath);
        // Tests never reach the real Scryfall API.
        builder.ConfigureTestServices(services =>
        {
            // No throttle delay, and the ScryfallClient's HttpClient talks to the fake handler.
            services.AddSingleton(new ScryfallThrottle(TimeSpan.Zero));
            services.AddHttpClient<ScryfallClient>().ConfigurePrimaryHttpMessageHandler(() => FakeScryfallHandler.WithFixtures());
            // Tests close auctions themselves; the minute timer would only race the test database's creation.
            services.RemoveAll<IHostedService>();
        });
    }

    // InitializeDatabaseAsync: apply the real migrations (so tests also prove the migrations work).
    public async Task InitializeDatabaseAsync()
    {
        using var scope = Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.MigrateAsync();
    }

    // Dispose: stop the app, then delete the temp database and uploads.
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_dbPath);
        if (Directory.Exists(UploadsPath))
            Directory.Delete(UploadsPath, recursive: true);
    }
}

// IClassFixture: one MaroosWebFactory shared by all tests in this class. IAsyncLifetime: async setup per test.
public class IdentityLoginTests : IClassFixture<MaroosWebFactory>, IAsyncLifetime
{
    private readonly MaroosWebFactory _factory;

    // xUnit passes the shared factory into the constructor.
    public IdentityLoginTests(MaroosWebFactory factory) => _factory = factory;

    // Before each test: make sure the database exists and is migrated.
    public Task InitializeAsync() => _factory.InitializeDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    // Managers: Identity's UserManager and SignInManager from the app, plus the scope to dispose afterwards.
    private (UserManager<ApplicationUser>, SignInManager<ApplicationUser>, IServiceScope) Managers()
    {
        var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var signIn = scope.ServiceProvider.GetRequiredService<SignInManager<ApplicationUser>>();
        return (users, signIn, scope);
    }

    // NewUser: an unsaved user whose user name is the email (as the Register page does). Confirmed by
    // default, because unconfirmed accounts can't log in (see the Unconfirmed_user test).
    /// <summary>A confirmed account, as after clicking the sign-up email's link (RequireConfirmedAccount is on).</summary>
    private static ApplicationUser NewUser(string email, bool confirmed = true) =>
        new() { UserName = email, Email = email, EmailConfirmed = confirmed };

    // TEST: a confirmed account can sign in with its email and password.
    [Fact]
    public async Task Confirmed_user_can_log_in_with_email_and_password()
    {
        var (users, signIn, scope) = Managers();
        using var _ = scope;
        var user = NewUser($"{Guid.NewGuid():N}@example.com");

        Assert.True((await users.CreateAsync(user, "Correct-horse-1")).Succeeded);

        var result = await signIn.CheckPasswordSignInAsync(user, "Correct-horse-1", lockoutOnFailure: true);
        Assert.True(result.Succeeded);
    }

    // TEST: a wrong password fails.
    [Fact]
    // TEST: an account that hasn't confirmed its email is refused (IsNotAllowed), even with the right password.
    public async Task Unconfirmed_user_cannot_log_in_until_they_confirm_their_email()
    {
        var (users, signIn, scope) = Managers();
        using var _ = scope;
        var user = NewUser($"{Guid.NewGuid():N}@example.com", confirmed: false);
        await users.CreateAsync(user, "Correct-horse-1");

        var result = await signIn.CheckPasswordSignInAsync(user, "Correct-horse-1", lockoutOnFailure: true);
        Assert.True(result.IsNotAllowed);
    }

    [Fact]
    public async Task Wrong_password_is_rejected()
    {
        var (users, signIn, scope) = Managers();
        using var _ = scope;
        var user = NewUser($"{Guid.NewGuid():N}@example.com");
        await users.CreateAsync(user, "Correct-horse-1");

        var result = await signIn.CheckPasswordSignInAsync(user, "wrong-password", lockoutOnFailure: true);
        Assert.False(result.Succeeded);
    }

    // TEST: after 5 wrong passwords, even the right password is refused (locked out).
    [Fact]
    public async Task Account_locks_after_five_failed_attempts()
    {
        var (users, signIn, scope) = Managers();
        using var _ = scope;
        var user = NewUser($"{Guid.NewGuid():N}@example.com");
        await users.CreateAsync(user, "Correct-horse-1");

        for (var i = 0; i < 5; i++)
            await signIn.CheckPasswordSignInAsync(user, "wrong-password", lockoutOnFailure: true);

        var result = await signIn.CheckPasswordSignInAsync(user, "Correct-horse-1", lockoutOnFailure: true);
        Assert.True(result.IsLockedOut);
    }

    // TEST: two accounts can't share an email (RequireUniqueEmail).
    [Fact]
    public async Task Duplicate_email_is_rejected()
    {
        var (users, _, scope) = Managers();
        using var _s = scope;
        var email = $"{Guid.NewGuid():N}@example.com";
        await users.CreateAsync(NewUser(email), "Correct-horse-1");

        var second = new ApplicationUser { UserName = $"other-{email}", Email = email };
        Assert.False((await users.CreateAsync(second, "Correct-horse-1")).Succeeded);
    }

    // TEST: a 7-character password is refused (RequiredLength = 8).
    [Fact]
    public async Task Password_shorter_than_eight_characters_is_rejected()
    {
        var (users, _, scope) = Managers();
        using var _s = scope;

        var result = await users.CreateAsync(NewUser($"{Guid.NewGuid():N}@example.com"), "Ab1-xyz");
        Assert.False(result.Succeeded);
    }
}
