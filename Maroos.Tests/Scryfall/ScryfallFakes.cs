using System.Net;
using System.Text;
using System.Text.Json;
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
    public static readonly Guid SolRingC21Id = Guid.Parse("4cbc6901-6a4a-4d0a-83ea-7eefa3b35021");
    public static readonly Guid MountainId = Guid.Parse("2a844b96-6616-4c39-8f4f-5d14a3b2bd55");

    /// <summary>Every saved single-card fixture: the cards the fake Scryfall "knows".</summary>
    public static readonly IReadOnlyList<string> CardFiles =
        ["card-lightning-bolt.json", "card-delver-dfc.json", "card-sol-ring-c21.json", "card-mountain.json"];
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
            "/cards/collection" => Collection(request),
            var path when path.StartsWith("/cards/") && KnownCard(Guid.TryParse(path[7..], out var id) ? id : Guid.Empty) is { } json
                => Json(HttpStatusCode.OK, json),
            _ => Json(HttpStatusCode.NotFound, """{"object":"error","code":"not_found","status":404}"""),
        },
    };

    private static string? KnownCard(Guid id) =>
        Fixtures.CardFiles.Select(Fixtures.Read).FirstOrDefault(json => ScryfallClient.ParseCard(json).Id == id);

    /// <summary>
    /// Like Scryfall's <c>/cards/collection</c>: found cards in request order, unknown identifiers echoed in not_found.
    /// </summary>
    private static HttpResponseMessage Collection(HttpRequestMessage request)
    {
        var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
        var identifiers = JsonSerializer.Deserialize<CollectionRequest>(body, ScryfallJson.Options)!.Identifiers;
        var known = Fixtures.CardFiles.Select(Fixtures.Read).Select(json => (Card: ScryfallClient.ParseCard(json), Json: json)).ToList();

        var data = new List<string>();
        var notFound = new List<CardIdentifier>();
        foreach (var identifier in identifiers)
        {
            if (known.FirstOrDefault(k => identifier.Matches(k.Card)) is { Json: not null } match)
                data.Add(match.Json);
            else
                notFound.Add(identifier);
        }

        var json = $$"""{"object":"list","not_found":{{JsonSerializer.Serialize(notFound, ScryfallJson.Options)}},"data":[{{string.Join(",", data)}}]}""";
        return Json(HttpStatusCode.OK, json);
    }

    private sealed record CollectionRequest(IReadOnlyList<CardIdentifier> Identifiers);

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
