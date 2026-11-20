using Maroos.Core.Auctions;
using Maroos.Core.Email;
using Maroos.Tests.Email;
using Maroos.Tests.Scryfall;
using Maroos.Web.Data;
using Maroos.Web.Services;
using Maroos.Web.Services.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maroos.Tests.Auctions;

/// <summary>Everything the auction services need, over one database, with a clock the test controls.</summary>
public sealed class AuctionWorld : IDisposable
{
    public IDbContextFactory<ApplicationDbContext> Db { get; }
    public ManualClock Clock { get; } = new(new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero));
    public AuctionEvents Events { get; } = new(NullLogger<AuctionEvents>.Instance);
    public RecordingEmailTransport EmailTransport { get; } = new();
    public EmailOutbox Emails { get; }
    public EmailDispatcher EmailDispatcher { get; }
    public CardService Cards { get; }
    public AuctionService Auctions { get; }
    public BiddingService Bidding { get; }
    public WatchService Watches { get; }
    public DeckService Decks { get; }

    private readonly IDisposable _database;

    /// <param name="database">Defaults to an in-memory database; pass a <see cref="FileTestDb"/> for concurrency tests.</param>
    public AuctionWorld(IDbContextFactory<ApplicationDbContext>? database = null)
    {
        var db = database ?? new TestDb();
        Db = db;
        _database = (IDisposable)db;
        Cards = new CardService(FakeScryfallHandler.WithFixtures().CreateClient(), Db, new MemoryCache(new MemoryCacheOptions()), Clock);
        var emailSignal = new EmailSignal();
        Emails = new EmailOutbox(Db, TestEmail.Options, emailSignal, Clock);
        EmailDispatcher = new EmailDispatcher(Db, EmailTransport, emailSignal, Clock, NullLogger<EmailDispatcher>.Instance);
        Auctions = new AuctionService(Db, Cards, Events, Emails, Clock);
        Bidding = new BiddingService(Db, Events, Emails, Clock, NullLogger<BiddingService>.Instance);
        Watches = new WatchService(Db, Clock);
        Decks = new DeckService(Db, Cards, Clock);
    }

    public void Dispose() => _database.Dispose();

    public string AddUser(string displayName, string? email = null, bool auctionEmails = true)
    {
        using var db = Db.CreateDbContext();
        email ??= $"{displayName.Replace(' ', '.').ToLowerInvariant()}@example.com";
        var user = new ApplicationUser { UserName = email, Email = email, DisplayName = displayName, AuctionEmails = auctionEmails };
        db.Users.Add(user);
        db.SaveChanges();
        return user.Id;
    }

    /// <summary>Every email queued so far.</summary>
    public List<OutgoingEmail> QueuedEmails()
    {
        using var db = Db.CreateDbContext();
        return db.OutgoingEmails.AsNoTracking().ToList();
    }

    /// <summary>Lists Sol Ring (C21) starting at $5.00 with a $1.00 increment.</summary>
    public Task<Auction> ListAsync(string sellerId, long startCents = 500, long incrementCents = 100, int days = 7) =>
        Auctions.CreateAsync(sellerId, new NewAuction(Fixtures.SolRingC21Id, CardCondition.NearMint, false, startCents, incrementCents, days));
}

/// <summary>
/// A SQLite database in a temp file. Unlike <see cref="TestDb"/> every context gets its own connection,
/// so transactions can really run at the same time.
/// </summary>
public sealed class FileTestDb : IDbContextFactory<ApplicationDbContext>, IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"maroos-concurrency-{Guid.NewGuid():N}.db");
    private readonly DbContextOptions<ApplicationDbContext> _options;

    public FileTestDb()
    {
        _options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite($"DataSource={_path}").Options;
        using var db = CreateDbContext();
        db.Database.EnsureCreated();
    }

    public ApplicationDbContext CreateDbContext() => new(_options);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }
}
