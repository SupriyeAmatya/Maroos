// =====================================================================================================
// FILE: Maroos.Tests/Auctions/AuctionWorld.cs
// PURPOSE: A ready-made "world" for auction tests: every real service (cards, auctions, bidding, watchlist,
// decks) wired together over one test database, a fake Scryfall and a clock the test controls.
// Typical test:  using var world = new AuctionWorld();
//                var seller = world.AddUser("Seller");  var auction = await world.ListAsync(seller);
//                world.Clock.Advance(TimeSpan.FromDays(8));  await world.Auctions.CloseExpiredAsync();
// Also: FileTestDb — a SQLite file database for tests where transactions must truly run at the same time.
// =====================================================================================================

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
    // The database factory all services share.
    public IDbContextFactory<ApplicationDbContext> Db { get; }
    // The fake clock starts at a fixed moment (23 Sep 2026, 12:00 UTC) so results are repeatable.
    public ManualClock Clock { get; } = new(new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero));
    // The real event hub (without SignalR); tests subscribe to it to check what was published.
    public AuctionEvents Events { get; } = new(NullLogger<AuctionEvents>.Instance);
    // Email: a fake SMTP server that records what's sent, the real outbox, and the real dispatcher
    // (tests call EmailDispatcher.SendDueAsync() themselves).
    public RecordingEmailTransport EmailTransport { get; } = new();
    public EmailOutbox Emails { get; }
    public EmailDispatcher EmailDispatcher { get; }
    // The real services under test.
    public CardService Cards { get; }
    public AuctionService Auctions { get; }
    public BiddingService Bidding { get; }
    public WatchService Watches { get; }
    public DeckService Decks { get; }

    // The database object to dispose at the end.
    private readonly IDisposable _database;

    /// <param name="database">Defaults to an in-memory database; pass a <see cref="FileTestDb"/> for concurrency tests.</param>
    public AuctionWorld(IDbContextFactory<ApplicationDbContext>? database = null)
    {
        var db = database ?? new TestDb();
        Db = db;
        _database = (IDisposable)db;
        // Wire the services exactly as Program.cs does, but with fakes for the outside world.
        Cards = new CardService(FakeScryfallHandler.WithFixtures().CreateClient(), Db, new MemoryCache(new MemoryCacheOptions()), Clock);
        // Email: the outbox and the dispatcher share one signal, as in Program.cs.
        var emailSignal = new EmailSignal();
        Emails = new EmailOutbox(Db, TestEmail.Options, emailSignal, Clock);
        EmailDispatcher = new EmailDispatcher(Db, EmailTransport, emailSignal, Clock, NullLogger<EmailDispatcher>.Instance);
        Auctions = new AuctionService(Db, Cards, Events, Emails, Clock);
        Bidding = new BiddingService(Db, Events, Emails, Clock, NullLogger<BiddingService>.Instance);
        Watches = new WatchService(Db, Clock);
        Decks = new DeckService(Db, Cards, Clock);
    }

    public void Dispose() => _database.Dispose();

    // AddUser: create a user with a display name (email derived from it unless given) and their
    // auction-email switch. Returns the user id.
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
    // A unique temp file per test.
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"maroos-concurrency-{Guid.NewGuid():N}.db");
    private readonly DbContextOptions<ApplicationDbContext> _options;

    public FileTestDb()
    {
        _options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite($"DataSource={_path}").Options;
        using var db = CreateDbContext();
        db.Database.EnsureCreated();
    }

    public ApplicationDbContext CreateDbContext() => new(_options);

    // Close pooled connections (Windows can't delete an open file), then delete the file.
    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }
}
