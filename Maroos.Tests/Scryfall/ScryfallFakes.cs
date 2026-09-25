// =====================================================================================================
// FILE: Maroos.Tests/Scryfall/ScryfallFakes.cs
// PURPOSE: Shared test helpers. No test ever calls the real Scryfall API or a real database server by default.
//
//   Fixtures            — saved real Scryfall JSON responses (Scryfall/Fixtures/*.json) + well-known card ids
//   FakeScryfallHandler — pretends to be api.scryfall.com: records each request and answers from the fixtures
//   ManualClock         — a clock the test sets and moves forward by hand ("fast-forward 7 days")
//   TestDb              — a fresh in-memory SQLite database per test
//   QueryCounter        — counts SELECT queries, to prove list pages don't run one query per row
// =====================================================================================================

using System.Net;
using System.Text;
using System.Text.Json;
using Maroos.Core.Scryfall;
using Maroos.Web.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Maroos.Tests.Scryfall;

// Fixtures: access to the saved JSON files (copied next to the test DLL at build time).
public static class Fixtures
{
    // Read: the text of one fixture file, e.g. Read("card-lightning-bolt.json").
    public static string Read(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Scryfall", "Fixtures", name));

    // Scryfall ids of the fixture cards (a normal card, a double-faced card, a specific printing, a basic land).
    public static readonly Guid LightningBoltId = Guid.Parse("7673784e-db4b-43a1-8d55-1bb9fc1e284f");
    public static readonly Guid DelverId = Guid.Parse("6904ea20-e504-47da-95a0-08739fdde260");
    public static readonly Guid SolRingC21Id = Guid.Parse("4cbc6901-6a4a-4d0a-83ea-7eefa3b35021");
    public static readonly Guid MountainId = Guid.Parse("2a844b96-6616-4c39-8f4f-5d14a3b2bd55");

    /// <summary>Every saved single-card fixture: the cards the fake Scryfall "knows".</summary>
    public static readonly IReadOnlyList<string> CardFiles =
        ["card-lightning-bolt.json", "card-delver-dfc.json", "card-sol-ring-c21.json", "card-mountain.json"];
}

/// <summary>Records every request and answers with whatever <see cref="Respond"/> returns.</summary>
// An HttpMessageHandler sits under HttpClient; replacing it lets a test control every "network" response.
public sealed class FakeScryfallHandler : HttpMessageHandler
{
    // Requests: everything the code under test sent (tests assert on these, e.g. "only one request was made").
    public List<(HttpMethod Method, string PathAndQuery, string? Body)> Requests { get; } = [];

    // Respond: decides the answer for each request. Default: 404 for everything.
    public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => Json(HttpStatusCode.NotFound, "{}");

    // Json: build a response with a status code and a JSON body.
    public static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    // Fixture: a 200 OK response with a fixture file's JSON.
    public static HttpResponseMessage Fixture(string name) => Json(HttpStatusCode.OK, Fixtures.Read(name));

    /// <summary>Serves saved Scryfall JSON for the routes the tests use.</summary>
    // Routing by URL path, like the real API:
    //   /cards/search        — "zzzz" -> not found; exact-name (!"...") -> one card; otherwise the Lightning Bolt results
    //   /cards/autocomplete  — the "lightning b" suggestions
    //   /cards/collection    — batched lookup, answered from the known cards (see Collection below)
    //   /cards/{id}          — a known card's JSON, else 404
    public static FakeScryfallHandler WithFixtures() => new()
    {
        Respond = request => request.RequestUri!.AbsolutePath switch
        {
            "/cards/search" when request.RequestUri.Query.Contains("zzzz") => Json(HttpStatusCode.NotFound, Fixtures.Read("search-not-found.json")),
            // Exact-name searches (!"Name") match a single card.
            "/cards/search" when request.RequestUri.Query.Contains("%21%22") => Json(HttpStatusCode.OK,
                $$"""{"object":"list","total_cards":1,"has_more":false,"data":[{{Fixtures.Read("card-lightning-bolt.json")}}]}"""),
            "/cards/search" => Fixture("search-lightning-bolt.json"),
            "/cards/autocomplete" => Fixture("autocomplete-lightning-b.json"),
            "/cards/collection" => Collection(request),
            var path when path.StartsWith("/cards/") && KnownCard(Guid.TryParse(path[7..], out var id) ? id : Guid.Empty) is { } json
                => Json(HttpStatusCode.OK, json),
            _ => Json(HttpStatusCode.NotFound, """{"object":"error","code":"not_found","status":404}"""),
        },
    };

    // KnownCard: the fixture JSON whose card has this id, or null.
    private static string? KnownCard(Guid id) =>
        Fixtures.CardFiles.Select(Fixtures.Read).FirstOrDefault(json => ScryfallClient.ParseCard(json).Id == id);

    /// <summary>
    /// Like Scryfall's <c>/cards/collection</c>: found cards in request order, unknown identifiers echoed in not_found.
    /// </summary>
    private static HttpResponseMessage Collection(HttpRequestMessage request)
    {
        // Read the POSTed identifiers (ids, names, or set + number).
        var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
        var identifiers = JsonSerializer.Deserialize<CollectionRequest>(body, ScryfallJson.Options)!.Identifiers;
        var known = Fixtures.CardFiles.Select(Fixtures.Read).Select(json => (Card: ScryfallClient.ParseCard(json), Json: json)).ToList();

        // Match each identifier against the known cards (CardIdentifier.Matches is the same logic the app uses).
        var data = new List<string>();
        var notFound = new List<CardIdentifier>();
        foreach (var identifier in identifiers)
        {
            if (known.FirstOrDefault(k => identifier.Matches(k.Card)) is { Json: not null } match)
                data.Add(match.Json);
            else
                notFound.Add(identifier);
        }

        // Build the response in Scryfall's shape.
        var json = $$"""{"object":"list","not_found":{{JsonSerializer.Serialize(notFound, ScryfallJson.Options)}},"data":[{{string.Join(",", data)}}]}""";
        return Json(HttpStatusCode.OK, json);
    }

    // The request body shape of /cards/collection.
    private sealed record CollectionRequest(IReadOnlyList<CardIdentifier> Identifiers);

    // SendAsync: called by HttpClient for every request — record it, then answer.
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add((request.Method, request.RequestUri!.PathAndQuery, body));
        return Respond(request);
    }

    // CreateClient: a real ScryfallClient wired to this fake. The throttle defaults to zero so tests run fast
    // (throttle tests pass their own interval).
    public ScryfallClient CreateClient(TimeSpan? minInterval = null) =>
        new(new HttpClient(this) { BaseAddress = new Uri("https://api.scryfall.com/") }, new ScryfallThrottle(minInterval ?? TimeSpan.Zero));
}

// ManualClock: a TimeProvider whose "now" only changes when the test says so.
// Lets tests check cache expiry (24 h), auction endings (7 days) etc. instantly.
public sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    // Now: the current fake time (settable).
    public DateTimeOffset Now { get; set; } = start;
    public override DateTimeOffset GetUtcNow() => Now;
    // Advance: move time forward, e.g. Advance(TimeSpan.FromDays(7)).
    public void Advance(TimeSpan by) => Now += by;
}

/// <summary>An in-memory SQLite database that lives as long as this object.</summary>
// All contexts share ONE open connection (an in-memory database disappears when its last connection closes).
// That also means no true parallel transactions — use FileTestDb for concurrency tests.
public sealed class TestDb : IDbContextFactory<ApplicationDbContext>, IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly DbContextOptions<ApplicationDbContext> _options;

    // interceptors: optional EF hooks (e.g. a QueryCounter).
    public TestDb(params IInterceptor[] interceptors)
    {
        _connection.Open();
        _options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).AddInterceptors(interceptors).Options;
        // Create all tables straight from the model (no migrations needed for unit tests).
        using var db = CreateDbContext();
        db.Database.EnsureCreated();
    }

    public ApplicationDbContext CreateDbContext() => new(_options);

    // Closing the connection deletes the in-memory database.
    public void Dispose() => _connection.Dispose();
}

/// <summary>
/// Counts SELECT queries sent to the database, to catch one-query-per-row pages. (Writes are left out:
/// SQLite sends each UPDATE separately, while PostgreSQL batches them.)
/// </summary>
// A DbCommandInterceptor sees every SQL command EF sends. Used by QueryCountTests.
public sealed class QueryCounter : DbCommandInterceptor
{
    // _count: updated with Interlocked (thread-safe) because pages may query from several threads.
    private int _count;

    public int Count => _count;

    // Reset: start counting from zero (e.g. after setting up test data).
    public void Reset() => Interlocked.Exchange(ref _count, 0);

    // Track: count the command if it's a SELECT.
    private void Track(System.Data.Common.DbCommand command)
    {
        if (command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            Interlocked.Increment(ref _count);
    }

    // The three ways EF runs a query (reader sync/async, scalar async): count, then let it run unchanged.
    public override InterceptionResult<System.Data.Common.DbDataReader> ReaderExecuting(System.Data.Common.DbCommand command, CommandEventData eventData, InterceptionResult<System.Data.Common.DbDataReader> result)
    {
        Track(command);
        return result;
    }

    public override ValueTask<InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(System.Data.Common.DbCommand command, CommandEventData eventData, InterceptionResult<System.Data.Common.DbDataReader> result, CancellationToken cancellationToken = default)
    {
        Track(command);
        return ValueTask.FromResult(result);
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(System.Data.Common.DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
    {
        Track(command);
        return ValueTask.FromResult(result);
    }
}
