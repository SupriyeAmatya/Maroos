// =====================================================================================================
// FILE: Maroos.Tests/Deploy/DeployReadinessTests.cs
// PURPOSE: Phase 7 checks: DatabaseSetupTests (provider choice, postgres:// URLs, DATABASE_URL, fail-fast),
//   SitePolishHttpTests (footer, security headers, /health, 404 page, uploads), SignedOutGuardTests, and
//   QueryCountTests (list pages use a fixed number of queries, not one per row).
// Each test's name says what it proves; the '// TEST:' line above each one repeats it in plain words.
// =====================================================================================================

using Maroos.Core.Auctions;
using Maroos.Core.Decks;
using Maroos.Core.Watchlist;
using Maroos.Tests.Auctions;
using Maroos.Tests.Auth;
using Maroos.Tests.Scryfall;
using Maroos.Web.Data;
using Maroos.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using static Maroos.Tests.Auth.PageForms;

namespace Maroos.Tests.Deploy;

public class DatabaseSetupTests
{
    // Helper: Config.
    private static IConfiguration Config(string? provider) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Database:Provider"] = provider }).Build();

    private sealed class Env(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "Maroos";
        public string ContentRootPath { get; set; } = "";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    // TEST: Provider defaults by environment and can be overridden.
    [Theory]
    [InlineData(null, "Development", "Sqlite")]
    [InlineData(null, "Production", "Postgres")]
    [InlineData("postgresql", "Development", "Postgres")]
    [InlineData("SQLITE", "Production", "Sqlite")]
    public void Provider_defaults_by_environment_and_can_be_overridden(string? configured, string environment, string expected) =>
        Assert.Equal(expected, DatabaseSetup.ProviderFor(Config(configured), new Env(environment)));

    // TEST: An unknown provider is an error.
    [Fact]
    public void An_unknown_provider_is_an_error() =>
        Assert.Throws<InvalidOperationException>(() => DatabaseSetup.ProviderFor(Config("mysql"), new Env("Production")));

    // TEST: Postgres urls from hosting providers become npgsql connection strings.
    [Fact]
    public void Postgres_urls_from_hosting_providers_become_npgsql_connection_strings()
    {
        var builder = new NpgsqlConnectionStringBuilder(DatabaseSetup.ToNpgsqlConnectionString(
            "postgres://maroos_user:p%40ss%3Aword@dpg-abc.oregon-postgres.render.com:6543/maroos_db?sslmode=require"));

        Assert.Equal("dpg-abc.oregon-postgres.render.com", builder.Host);
        Assert.Equal(6543, builder.Port);
        Assert.Equal("maroos_db", builder.Database);
        Assert.Equal("maroos_user", builder.Username);
        Assert.Equal("p@ss:word", builder.Password);
        Assert.Equal(SslMode.Require, builder.SslMode);
    }

    // TEST: DATABASE URL is used when no connection string is set.
    [Fact]
    public void DATABASE_URL_is_used_when_no_connection_string_is_set()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:Provider"] = "Postgres",
            ["ConnectionStrings:DefaultConnection"] = "DataSource=Data/app.db;Cache=Shared",
            ["DATABASE_URL"] = "postgres://fly:secret@maroos-db.flycast:5432/maroos",
        }).Build();

        var services = new ServiceCollection().AddMaroosDatabase(config, new Env("Production")).BuildServiceProvider();

        // Read the registered options rather than building a context: EF caches a context type's model per process,
        // and a model built here (without Identity's options) would leak into other tests.
        var options = services.GetRequiredService<DbContextOptions<PostgresDbContext>>();
        var connection = new NpgsqlConnectionStringBuilder(
            Microsoft.EntityFrameworkCore.Infrastructure.RelationalOptionsExtension.Extract(options).ConnectionString);
        Assert.Equal(("maroos-db.flycast", "maroos", "fly"), (connection.Host, connection.Database, connection.Username));
    }

    // TEST: Production without any postgres connection refuses to start.
    [Fact]
    public void Production_without_any_postgres_connection_refuses_to_start()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = "DataSource=Data/app.db;Cache=Shared",
        }).Build();

        var error = Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddMaroosDatabase(config, new Env("Production")));
        Assert.Contains("ConnectionStrings__DefaultConnection (or DATABASE_URL)", error.Message);
    }

    // TEST: Photo storage falls back to the bucket variables hosts set.
    [Fact]
    public void Photo_storage_falls_back_to_the_bucket_variables_hosts_set()
    {
        var fromFly = PhotoStorageOptions.From(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Uploads:Storage"] = "S3",
            ["BUCKET_NAME"] = "maroos-tigris",
            ["AWS_ENDPOINT_URL_S3"] = "https://fly.storage.tigris.dev",
            ["AWS_REGION"] = "auto",
        }).Build());
        Assert.Equal(("maroos-tigris", "https://fly.storage.tigris.dev", "auto"), (fromFly.S3.Bucket, fromFly.S3.ServiceUrl, fromFly.S3.Region));

        var explicitWins = PhotoStorageOptions.From(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Uploads:S3:Bucket"] = "mine",
            ["Uploads:S3:Region"] = "eu-west-1",
            ["BUCKET_NAME"] = "theirs",
            ["AWS_REGION"] = "auto",
        }).Build());
        Assert.Equal(("mine", "eu-west-1"), (explicitWins.S3.Bucket, explicitWins.S3.Region));
    }

    // TEST: Kerberos probing is off unless asked for.
    [Fact]
    public void Kerberos_probing_is_off_unless_asked_for()
    {
        Assert.Equal(GssEncryptionMode.Disable,
            new NpgsqlConnectionStringBuilder(DatabaseSetup.WithoutKerberosProbe("Host=db;Database=maroos")).GssEncryptionMode);
        Assert.Equal(GssEncryptionMode.Require,
            new NpgsqlConnectionStringBuilder(DatabaseSetup.WithoutKerberosProbe("Host=db;Database=maroos;GSS Encryption Mode=Require")).GssEncryptionMode);
    }

    // TEST: Default port and plain connection strings are handled.
    [Fact]
    public void Default_port_and_plain_connection_strings_are_handled()
    {
        Assert.Equal(5432, new NpgsqlConnectionStringBuilder(DatabaseSetup.ToNpgsqlConnectionString("postgresql://u:p@db.internal/maroos")).Port);
        Assert.Equal("Host=db;Database=maroos", DatabaseSetup.ToNpgsqlConnectionString("Host=db;Database=maroos"));
    }
}

/// <summary>Plan phase 7, steps 1 and 3 plus production hardening, checked over real HTTP.</summary>
public class SitePolishHttpTests : IClassFixture<MaroosWebFactory>, IAsyncLifetime
{
    private readonly MaroosWebFactory _factory;

    public SitePolishHttpTests(MaroosWebFactory factory) => _factory = factory;

    public Task InitializeAsync() => _factory.InitializeDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    // TEST: Every page credits scryfall and carries the fan content notice.
    [Theory]
    [InlineData("/")]
    [InlineData("/auctions")]
    [InlineData("/Account/Login")]
    public async Task Every_page_credits_scryfall_and_carries_the_fan_content_notice(string url)
    {
        var html = await _factory.NewBrowser().GetStringAsync(url);

        Assert.Contains("Card data and images from <a href=\"https://scryfall.com\"", html);
        Assert.Contains("unofficial Fan Content permitted under the", html);
        Assert.Contains("https://company.wizards.com/en/legal/fancontentpolicy", html);
        Assert.Contains("Not approved/endorsed by Wizards.", html);
        Assert.Contains("©Wizards of the Coast LLC.", html);
    }

    // TEST: Responses carry security headers.
    [Fact]
    public async Task Responses_carry_security_headers()
    {
        var response = await _factory.NewBrowser().GetAsync("/");

        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("strict-origin-when-cross-origin", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.Equal("frame-ancestors 'none'", response.Headers.GetValues("Content-Security-Policy").Single());
    }

    // TEST: Health endpoint reports the database is reachable.
    [Fact]
    public async Task Health_endpoint_reports_the_database_is_reachable()
    {
        var response = await _factory.NewBrowser().GetAsync("/health");

        response.EnsureSuccessStatusCode();
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }

    // TEST: Uploads only serve existing generated photo keys.
    [Theory]
    [InlineData("/uploads/..%2F..%2Fappsettings.json")]
    [InlineData("/uploads/web.config")]
    [InlineData("/uploads/0123456789abcdef0123456789abcdef.jpg")]
    public async Task Uploads_only_serve_existing_generated_photo_keys(string url)
    {
        var response = await _factory.NewBrowser().GetAsync(url);

        Assert.NotEqual(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.NotEqual("image/jpeg", response.Content.Headers.ContentType?.MediaType);
    }

    // TEST: Uploaded photos are cached forever.
    [Fact]
    public async Task Uploaded_photos_are_cached_forever()
    {
        using var scope = _factory.Services.CreateScope();
        var path = await scope.ServiceProvider.GetRequiredService<AuctionPhotoStore>()
            .SaveAsync(new MemoryStream(AuctionPhotoStoreTests.Image(12, 12, SkiaSharp.SKEncodedImageFormat.Png)));

        var response = await _factory.NewBrowser().GetAsync("/" + path);

        response.EnsureSuccessStatusCode();
        Assert.Equal("public, max-age=31536000, immutable", response.Headers.CacheControl!.ToString());
    }

    // TEST: Login cookie keys are stored in the database so redeploys keep people signed in.
    [Fact]
    public async Task Login_cookie_keys_are_stored_in_the_database_so_redeploys_keep_people_signed_in()
    {
        (await RegisterAsync(_factory.NewBrowser(), $"{Guid.NewGuid():N}@example.com")).EnsureSuccessStatusCode();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.NotEmpty(await db.DataProtectionKeys.ToListAsync());
    }
}

/// <summary>Services refuse writes without a signed-in user, even if a page forgot to hide the control.</summary>
public sealed class SignedOutGuardTests : IDisposable
{
    private readonly AuctionWorld _world = new();

    public void Dispose() => _world.Dispose();

    // TEST: Writes without a user are refused.
    [Fact]
    public async Task Writes_without_a_user_are_refused()
    {
        var seller = _world.AddUser("Seller");
        var auction = await _world.ListAsync(seller);
        var deck = await _world.Decks.CreateAsync(seller, "Burn", "modern", isPublic: true);

        Assert.Equal("Log in to bid.", (await _world.Bidding.PlaceBidAsync(auction.Id, "", 900)).Error);
        Assert.Equal("Log in to bid.", (await _world.Bidding.PlaceBidAsync(auction.Id, null!, 900)).Error);
        await Assert.ThrowsAsync<AuctionAccessException>(() => _world.Auctions.CancelAsync(auction.Id, null!));
        await Assert.ThrowsAsync<AuctionAccessException>(() => _world.ListAsync(""));
        await Assert.ThrowsAsync<DeckAccessException>(() => _world.Decks.AddCardAsync(deck.Id, null!, "Sol Ring", Board.Main));
        await Assert.ThrowsAsync<DeckAccessException>(() => _world.Decks.CreateAsync("", "Mine", "modern"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _world.Watches.ToggleAsync(null!, WatchTarget.Deck, deck.Id));
    }
}

/// <summary>Plan phase 7, step 2: pages that list related data use a fixed number of queries, not one per row.</summary>
public sealed class QueryCountTests : IDisposable
{
    private readonly QueryCounter _queries = new();
    private readonly AuctionWorld _world;
    private readonly string _seller;
    private readonly string[] _bidders;

    public QueryCountTests()
    {
        _world = new AuctionWorld(new TestDb(_queries));
        _seller = _world.AddUser("Seller");
        _bidders = Enumerable.Range(1, 3).Select(i => _world.AddUser($"Bidder {i}")).ToArray();
    }

    public void Dispose() => _world.Dispose();

    // Helper: List many async.
    private async Task<List<Auction>> ListManyAsync(int count, int days = 7)
    {
        var auctions = new List<Auction>();
        for (var i = 0; i < count; i++)
        {
            var auction = await _world.ListAsync(_seller, days: days);
            await _world.Bidding.PlaceBidAsync(auction.Id, _bidders[i % 3], 500);
            await _world.Watches.ToggleAsync(_bidders[0], WatchTarget.Auction, auction.Id);
            auctions.Add(auction);
        }
        return auctions;
    }

    // Helper: Count async.
    private async Task<int> CountAsync(Func<Task> action)
    {
        _queries.Reset();
        await action();
        return _queries.Count;
    }

    // TEST: Listing pages use the same number of queries for 2 or 20 rows.
    [Theory]
    [InlineData(2)]
    [InlineData(20)]
    public async Task Listing_pages_use_the_same_number_of_queries_for_2_or_20_rows(int rows)
    {
        await ListManyAsync(rows);
        await _world.Auctions.ListAsync(new AuctionFilter()); // warm the card cache

        Assert.Equal(2, await CountAsync(() => _world.Auctions.ListAsync(new AuctionFilter())));            // auctions + cached cards
        Assert.Equal(1, await CountAsync(() => _world.Auctions.ListForSellerAsync(_seller)));
        Assert.Equal(1, await CountAsync(() => _world.Bidding.ListForBidderAsync(_bidders[0])));
        Assert.Equal(2, await CountAsync(() => _world.Watches.ListAsync(_bidders[0])));                    // decks + auctions
    }

    // TEST: A deck page loads in two queries however many cards it has.
    [Fact]
    public async Task A_deck_page_loads_in_two_queries_however_many_cards_it_has()
    {
        var deck = await _world.Decks.CreateAsync(_seller, "Pile", "modern");
        await _world.Decks.ImportAsync(deck.Id, _seller, "4 Lightning Bolt\n4 Delver of Secrets\n1 Sol Ring\n51 Mountain", replace: false);

        Assert.Equal(2, await CountAsync(() => _world.Decks.GetAsync(deck.Id, _seller))); // deck with cards + cached cards
    }

    // TEST: A copied deck needs just one more query for its credit.
    [Fact]
    public async Task A_copied_deck_needs_just_one_more_query_for_its_credit()
    {
        var deck = await _world.Decks.CreateAsync(_seller, "Pile", "modern", isPublic: true);
        await _world.Decks.ImportAsync(deck.Id, _seller, "4 Lightning Bolt\n4 Delver of Secrets\n1 Sol Ring\n51 Mountain", replace: false);
        var copyId = await _world.Decks.CopyAsync(deck.Id, _bidders[0]);

        Assert.Equal(3, await CountAsync(() => _world.Decks.GetAsync(copyId, _bidders[0]))); // + the original and its owner
    }

    // TEST: Closing many auctions does not query each one.
    [Fact]
    public async Task Closing_many_auctions_does_not_query_each_one()
    {
        await ListManyAsync(10, days: 1);
        _world.Clock.Advance(TimeSpan.FromDays(1));

        // One query for the expired auctions and one for all their bids, not one bid lookup per auction.
        Assert.Equal(2, await CountAsync(() => _world.Auctions.CloseExpiredAsync()));
    }
}
