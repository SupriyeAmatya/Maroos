using System.Net;
using System.Text;
using Maroos.Core.Scryfall;
using Maroos.Web.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Maroos.Tests.Scryfall;

public static class Fixtures
{
    public static string Read(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Scryfall", "Fixtures", name));

    public static readonly Guid LightningBoltId = Guid.Parse("7673784e-db4b-43a1-8d55-1bb9fc1e284f");
    public static readonly Guid DelverId = Guid.Parse("6904ea20-e504-47da-95a0-08739fdde260");
}

/// <summary>Records every request and answers with whatever <see cref="Respond"/> returns.</summary>
public sealed class FakeScryfallHandler : HttpMessageHandler
{
    public List<(HttpMethod Method, string PathAndQuery, string? Body)> Requests { get; } = [];

    public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => Json(HttpStatusCode.NotFound, "{}");

    public static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Fixture(string name) => Json(HttpStatusCode.OK, Fixtures.Read(name));

    /// <summary>Serves saved Scryfall JSON for the routes the tests use.</summary>
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
            "/cards/collection" => Fixture("collection-sol-ring.json"),
            var path when path == $"/cards/{Fixtures.LightningBoltId}" => Fixture("card-lightning-bolt.json"),
            var path when path == $"/cards/{Fixtures.DelverId}" => Fixture("card-delver-dfc.json"),
            _ => Json(HttpStatusCode.NotFound, """{"object":"error","code":"not_found","status":404}"""),
        },
    };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add((request.Method, request.RequestUri!.PathAndQuery, body));
        return Respond(request);
    }

    public ScryfallClient CreateClient(TimeSpan? minInterval = null) =>
        new(new HttpClient(this) { BaseAddress = new Uri("https://api.scryfall.com/") }, new ScryfallThrottle(minInterval ?? TimeSpan.Zero));
}

public sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;
    public override DateTimeOffset GetUtcNow() => Now;
    public void Advance(TimeSpan by) => Now += by;
}

/// <summary>An in-memory SQLite database that lives as long as this object.</summary>
public sealed class TestDb : IDbContextFactory<ApplicationDbContext>, IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly DbContextOptions<ApplicationDbContext> _options;

    public TestDb()
    {
        _connection.Open();
        _options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        using var db = CreateDbContext();
        db.Database.EnsureCreated();
    }

    public ApplicationDbContext CreateDbContext() => new(_options);

    public void Dispose() => _connection.Dispose();
}
