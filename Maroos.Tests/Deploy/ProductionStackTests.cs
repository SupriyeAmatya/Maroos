using Maroos.Core.Decks;
using Maroos.Core.Watchlist;
using Maroos.Tests.Auctions;
using Maroos.Tests.Auth;
using Maroos.Web.Data;
using Maroos.Web.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SkiaSharp;

namespace Maroos.Tests.Deploy;

/// <summary>Both migration sets must match the model: a schema change without migrations for SQLite and PostgreSQL fails here.</summary>
public class MigrationsAreCompleteTests
{
    [Fact]
    public void The_sqlite_migrations_match_the_model()
    {
        using var factory = new MaroosWebFactory();
        using var scope = factory.Services.CreateScope();

        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.IsType<ApplicationDbContext>(db);
        Assert.False(db.Database.HasPendingModelChanges(), "Run: dotnet ef migrations add <Name> --context ApplicationDbContext --project Maroos.Web");
    }

    [Fact]
    public void The_postgres_migrations_match_the_model()
    {
        // Built through the app's own services, so Identity's schema options apply; no database is contacted.
        using var factory = new MaroosWebFactory().WithWebHostBuilder(builder => builder
            .UseSetting("Database:Provider", "Postgres")
            .UseSetting("ConnectionStrings:DefaultConnection", "Host=localhost;Database=model_check_only"));
        using var scope = factory.Services.CreateScope();

        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.IsType<PostgresDbContext>(db);
        Assert.False(db.Database.HasPendingModelChanges(),
            "Run: dotnet ef migrations add <Name> --context PostgresDbContext --project Maroos.Web --output-dir Data/Migrations/Postgres");
    }
}

/// <summary>
/// A fresh PostgreSQL database per test class, migrated with the real PostgreSQL migrations.
/// Needs MAROOS_TEST_POSTGRES (a connection string to a server, e.g. from compose.yaml); tests skip without it.
/// </summary>
public sealed class PostgresTestDb : IDbContextFactory<ApplicationDbContext>, IDisposable
{
    public static string? ServerConnectionString => Environment.GetEnvironmentVariable("MAROOS_TEST_POSTGRES");

    private readonly string _database = $"maroos_test_{Guid.NewGuid():N}";
    private readonly DbContextOptions<PostgresDbContext> _options;

    public PostgresTestDb(string? migrateTo = null)
    {
        var builder = new NpgsqlConnectionStringBuilder(ServerConnectionString) { Database = _database };
        // Identity reads its schema options from the application's services; give it the same ones as Program.cs.
        var appServices = new ServiceCollection()
            .Configure<IdentityOptions>(o => o.Stores.SchemaVersion = IdentitySchemaVersions.Version3)
            .BuildServiceProvider();
        _options = new DbContextOptionsBuilder<PostgresDbContext>()
            .UseNpgsql(builder.ConnectionString)
            .UseApplicationServiceProvider(appServices)
            .Options;

        using var db = CreateDbContext();
        db.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>().Migrate(migrateTo);
    }

    public ApplicationDbContext CreateDbContext() => new PostgresDbContext(_options);

    public void Dispose()
    {
        using var db = CreateDbContext();
        db.Database.EnsureDeleted();
    }
}

public sealed class PostgresTests : IDisposable
{
    private readonly AuctionWorld? _world;

    public PostgresTests()
    {
        if (PostgresTestDb.ServerConnectionString is not null)
            _world = new AuctionWorld(new PostgresTestDb());
    }

    public void Dispose() => _world?.Dispose();

    private AuctionWorld World
    {
        get
        {
            Skip.If(_world is null, "Set MAROOS_TEST_POSTGRES to run PostgreSQL tests (see compose.yaml).");
            return _world!;
        }
    }

    [SkippableFact]
    public async Task Migrations_apply_and_nothing_is_pending()
    {
        await using var db = World.Db.CreateDbContext();
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.Contains("20260923182618_InitialCreate", await db.Database.GetAppliedMigrationsAsync());
    }

    [SkippableFact]
    public async Task Decks_import_and_load_with_card_data()
    {
        var owner = World.AddUser("Pg Owner");
        var deck = await World.Decks.CreateAsync(owner, "Pg Burn", "modern", isPublic: true);

        var result = await World.Decks.ImportAsync(deck.Id, owner, "4 Lightning Bolt\n4 Delver of Secrets\n52 Mountain\nSideboard\n1 Sol Ring (C21) 263", replace: false);
        var view = (await World.Decks.GetAsync(deck.Id, owner))!;

        Assert.Equal(61, result.CardsAdded);
        Assert.Equal(60, view.Count(Board.Main));
        Assert.Equal([0, 8, 0, 0, 0, 0, 0, 0], view.Stats.ManaCurve);
        Assert.Contains(view.Issues, i => i.CardName == "Sol Ring"); // not legal in Modern
    }

    [SkippableFact]
    public async Task Auctions_bids_and_the_closer_work_end_to_end()
    {
        var seller = World.AddUser("Pg Seller", "pg-seller@example.com");
        var bob = World.AddUser("Pg Bob");
        var carol = World.AddUser("Pg Carol");
        var auction = await World.ListAsync(seller, startCents: 500, incrementCents: 100, days: 1);

        Assert.True((await World.Bidding.PlaceBidAsync(auction.Id, bob, 500)).Accepted);
        Assert.Equal("Bids must be at least $6.00.", (await World.Bidding.PlaceBidAsync(auction.Id, carol, 550)).Error);
        Assert.True((await World.Bidding.PlaceBidAsync(auction.Id, carol, 700)).Accepted);

        var listed = Assert.Single(await World.Auctions.ListAsync(new AuctionFilter(CardName: "SOL", MaxCents: 700)));
        Assert.Equal((700L, 2), (listed.CurrentCents, listed.BidCount));
        Assert.Equal(DateTimeKind.Utc, listed.EndsAtUtc.Kind);

        World.Clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal(1, await World.Auctions.CloseExpiredAsync());
        var view = (await World.Auctions.GetAsync(auction.Id, carol))!;
        Assert.Equal("Pg Carol", view.WinnerName);
        Assert.Equal("pg-seller@example.com", view.ContactEmail);
    }

    [SkippableFact]
    public async Task Parallel_bids_on_postgres_never_break_the_rules()
    {
        var seller = World.AddUser("Pg Seller");
        var bidders = Enumerable.Range(1, 8).Select(i => World.AddUser($"Pg Bidder {i}")).ToList();
        var auction = await World.ListAsync(seller, startCents: 500, incrementCents: 100);

        var results = await Task.WhenAll(bidders.SelectMany((bidder, i) => new[] { 500L, 600L, 700L }
            .Select(amount => Task.Run(() => World.Bidding.PlaceBidAsync(auction.Id, bidder, amount + i % 2 * 50)))));

        await using var db = World.Db.CreateDbContext();
        var saved = await db.Bids.Where(b => b.AuctionId == auction.Id).OrderBy(b => b.AmountCents).ToListAsync();
        Assert.Equal(results.Count(r => r.Accepted), saved.Count);
        Assert.True(saved[0].AmountCents >= 500);
        for (var i = 1; i < saved.Count; i++)
            Assert.True(saved[i].AmountCents >= saved[i - 1].AmountCents + 100, $"bid {saved[i].AmountCents} accepted over {saved[i - 1].AmountCents}");
    }

    [SkippableFact]
    public async Task Watchlist_works()
    {
        var owner = World.AddUser("Pg Deck Owner");
        var fan = World.AddUser("Pg Fan");
        var deck = await World.Decks.CreateAsync(owner, "Pg Public", "legacy", isPublic: true);

        Assert.True(await World.Watches.ToggleAsync(fan, WatchTarget.Deck, deck.Id));
        Assert.Equal("Pg Deck Owner", Assert.Single((await World.Watches.ListAsync(fan)).Decks).OwnerName);
    }
}

/// <summary>
/// Photos in a real S3-compatible bucket (the Versity S3 gateway in compose.yaml). Needs MAROOS_TEST_S3_URL, MAROOS_TEST_S3_ACCESS_KEY,
/// MAROOS_TEST_S3_SECRET_KEY and MAROOS_TEST_S3_BUCKET; skips without them.
/// </summary>
public class S3PhotoStorageTests
{
    private static S3PhotoOptions? Options()
    {
        var url = Environment.GetEnvironmentVariable("MAROOS_TEST_S3_URL");
        Skip.If(string.IsNullOrEmpty(url), "Set MAROOS_TEST_S3_* to run S3 tests (see compose.yaml).");
        return new S3PhotoOptions
        {
            ServiceUrl = url,
            Bucket = Environment.GetEnvironmentVariable("MAROOS_TEST_S3_BUCKET")!,
            AccessKey = Environment.GetEnvironmentVariable("MAROOS_TEST_S3_ACCESS_KEY"),
            SecretKey = Environment.GetEnvironmentVariable("MAROOS_TEST_S3_SECRET_KEY"),
            Region = "us-east-1",
            ForcePathStyle = true,
            Prefix = $"test-{Guid.NewGuid():N}/",
        };
    }

    [SkippableFact]
    public async Task Photos_round_trip_through_the_bucket()
    {
        var options = Options()!;
        using var client = S3PhotoBlobStore.CreateClient(options);
        var store = new AuctionPhotoStore(new S3PhotoBlobStore(client, options));

        var path = await store.SaveAsync(new MemoryStream(AuctionPhotoStoreTests.Image(30, 20, SKEncodedImageFormat.Png)));
        var key = path["uploads/".Length..];

        await using (var photo = await store.OpenAsync(key))
        {
            Assert.NotNull(photo);
            using var copy = new MemoryStream();
            await photo!.CopyToAsync(copy);
            using var bitmap = SKBitmap.Decode(copy.ToArray());
            Assert.Equal((30, 20), (bitmap.Width, bitmap.Height));
        }

        await store.DeleteAsync(path);
        Assert.Null(await store.OpenAsync(key));
        Assert.Null(await store.OpenAsync($"{Guid.NewGuid():N}.jpg"));
    }
}
