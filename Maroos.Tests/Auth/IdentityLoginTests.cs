using Maroos.Core.Scryfall;
using Maroos.Tests.Scryfall;
using Maroos.Web.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Maroos.Tests.Auth;

public class MaroosWebFactory : WebApplicationFactory<Program>
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"maroos-test-{Guid.NewGuid():N}.db");

    public string UploadsPath { get; } = Path.Combine(Path.GetTempPath(), $"maroos-uploads-{Guid.NewGuid():N}");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:DefaultConnection", $"DataSource={_dbPath}");
        builder.UseSetting("Uploads:Path", UploadsPath);
        // Tests never reach the real Scryfall API.
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton(new ScryfallThrottle(TimeSpan.Zero));
            services.AddHttpClient<ScryfallClient>().ConfigurePrimaryHttpMessageHandler(() => FakeScryfallHandler.WithFixtures());
        });
    }

    public async Task InitializeDatabaseAsync()
    {
        using var scope = Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.MigrateAsync();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_dbPath);
        if (Directory.Exists(UploadsPath))
            Directory.Delete(UploadsPath, recursive: true);
    }
}

public class IdentityLoginTests : IClassFixture<MaroosWebFactory>, IAsyncLifetime
{
    private readonly MaroosWebFactory _factory;

    public IdentityLoginTests(MaroosWebFactory factory) => _factory = factory;

    public Task InitializeAsync() => _factory.InitializeDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private (UserManager<ApplicationUser>, SignInManager<ApplicationUser>, IServiceScope) Managers()
    {
        var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var signIn = scope.ServiceProvider.GetRequiredService<SignInManager<ApplicationUser>>();
        return (users, signIn, scope);
    }

    private static ApplicationUser NewUser(string email) => new() { UserName = email, Email = email };

    [Fact]
    public async Task Registered_user_can_log_in_with_email_and_password_without_confirming()
    {
        var (users, signIn, scope) = Managers();
        using var _ = scope;
        var user = NewUser($"{Guid.NewGuid():N}@example.com");

        Assert.True((await users.CreateAsync(user, "Correct-horse-1")).Succeeded);

        var result = await signIn.CheckPasswordSignInAsync(user, "Correct-horse-1", lockoutOnFailure: true);
        Assert.True(result.Succeeded);
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

    [Fact]
    public async Task Password_shorter_than_eight_characters_is_rejected()
    {
        var (users, _, scope) = Managers();
        using var _s = scope;

        var result = await users.CreateAsync(NewUser($"{Guid.NewGuid():N}@example.com"), "Ab1-xyz");
        Assert.False(result.Succeeded);
    }
}
