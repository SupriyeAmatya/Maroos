using Maroos.Tests.Auth;
using Maroos.Tests.Deploy;
using Maroos.Web.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace Maroos.Tests.Email;

/// <summary>Turning email confirmation on must not lock out accounts that existed before it.</summary>
public sealed class EmailMigrationTests : IDisposable
{
    private const string LastSqliteMigrationBeforeEmail = "20260923182601_DataProtectionKeys";
    private const string LastPostgresMigrationBeforeEmail = "20260923182618_InitialCreate";

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"maroos-migrate-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    private const string OldUserSql = """
        INSERT INTO "AspNetUsers" ("Id", "UserName", "NormalizedUserName", "Email", "NormalizedEmail", "EmailConfirmed",
            "PhoneNumberConfirmed", "TwoFactorEnabled", "LockoutEnabled", "AccessFailedCount")
        VALUES ('old-user', 'old@example.com', 'OLD@EXAMPLE.COM', 'old@example.com', 'OLD@EXAMPLE.COM', {0}, {0}, {0}, {1}, 0)
        """;

    [Fact]
    public async Task Accounts_from_before_email_are_confirmed_and_get_auction_emails_on_sqlite()
    {
        // Built through the app so Identity's schema options apply, like MigrationsAreCompleteTests.
        using var factory = new MaroosWebFactory().WithWebHostBuilder(builder =>
            builder.UseSetting("ConnectionStrings:DefaultConnection", $"DataSource={_path}"));
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var migrator = db.GetService<IMigrator>();

        await migrator.MigrateAsync(LastSqliteMigrationBeforeEmail);
        await db.Database.ExecuteSqlRawAsync(string.Format(OldUserSql, "0", "1"));
        await migrator.MigrateAsync();

        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == "old-user");
        Assert.True(user.EmailConfirmed);
        Assert.True(user.AuctionEmails);
    }

    [SkippableFact]
    public async Task Accounts_from_before_email_are_confirmed_and_get_auction_emails_on_postgres()
    {
        Skip.If(PostgresTestDb.ServerConnectionString is null, "Set MAROOS_TEST_POSTGRES to run PostgreSQL tests (see compose.yaml).");
        using var database = new PostgresTestDb(migrateTo: LastPostgresMigrationBeforeEmail);
        await using var db = database.CreateDbContext();

        await db.Database.ExecuteSqlRawAsync(string.Format(OldUserSql, "FALSE", "TRUE"));
        await db.GetService<IMigrator>().MigrateAsync();

        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == "old-user");
        Assert.True(user.EmailConfirmed);
        Assert.True(user.AuctionEmails);
    }
}
