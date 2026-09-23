using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Maroos.Core.Scryfall;

namespace Maroos.Tests.Scryfall;

public class ScryfallClientTests
{
    [Fact]
    public async Task SearchAsync_parses_results_and_escapes_the_query()
    {
        var handler = FakeScryfallHandler.WithFixtures();

        var results = await handler.CreateClient().SearchAsync("lightning bolt");

        Assert.Equal(2, results.TotalCards);
        Assert.Contains(results.Data, c => c.Name == "Lightning Bolt");
        Assert.All(results.Data, c => Assert.NotNull(c.FrontImage?.Normal));
        Assert.Equal("/cards/search?q=lightning%20bolt&page=1", handler.Requests.Single().PathAndQuery);
    }

    [Fact]
    public async Task SearchAsync_returns_empty_when_scryfall_finds_nothing()
    {
        var results = await FakeScryfallHandler.WithFixtures().CreateClient().SearchAsync("zzzznotacardzzzz");

        Assert.Empty(results.Data);
        Assert.Equal(0, results.TotalCards);
    }

    [Fact]
    public async Task SearchAsync_throws_on_bad_query_syntax()
    {
        var handler = new FakeScryfallHandler
        {
            Respond = _ => FakeScryfallHandler.Json(HttpStatusCode.BadRequest, """{"object":"error","status":400}"""),
        };

        await Assert.ThrowsAsync<HttpRequestException>(() => handler.CreateClient().SearchAsync("t:"));
    }

    [Fact]
    public async Task SearchAsync_skips_blank_queries()
    {
        var handler = FakeScryfallHandler.WithFixtures();

        var results = await handler.CreateClient().SearchAsync("   ");

        Assert.Empty(results.Data);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task AutocompleteAsync_returns_names()
    {
        var names = await FakeScryfallHandler.WithFixtures().CreateClient().AutocompleteAsync("lightning b");

        Assert.Equal(5, names.Count);
        Assert.Contains("Lightning Bolt", names);
    }

    [Fact]
    public async Task AutocompleteAsync_skips_text_shorter_than_two_characters()
    {
        var handler = FakeScryfallHandler.WithFixtures();

        Assert.Empty(await handler.CreateClient().AutocompleteAsync("l"));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task GetCardAsync_parses_card_fields_prices_and_legalities()
    {
        var card = await FakeScryfallHandler.WithFixtures().CreateClient().GetCardAsync(Fixtures.LightningBoltId);

        Assert.NotNull(card);
        Assert.Equal(Fixtures.LightningBoltId, card.Id);
        Assert.Equal("Lightning Bolt", card.Name);
        Assert.Equal("{R}", card.ManaCost);
        Assert.Equal(1m, card.Cmc);
        Assert.Equal("Instant", card.TypeLine);
        Assert.Equal(["R"], card.Colors);
        Assert.Equal(72, card.Prices.UsdCents);
        Assert.Equal(386, card.Prices.UsdFoilCents);
        Assert.Equal("legal", card.Legalities["modern"]);
        Assert.Equal("banned", card.Legalities["historic"]);
        Assert.NotNull(card.FrontImage?.Normal);
    }

    [Fact]
    public async Task GetCardAsync_takes_the_front_image_from_the_first_face_of_a_double_faced_card()
    {
        var card = await FakeScryfallHandler.WithFixtures().CreateClient().GetCardAsync(Fixtures.DelverId);

        Assert.NotNull(card);
        Assert.Null(card.ImageUris);
        Assert.Equal(2, card.CardFaces!.Count);
        Assert.Equal("Insectile Aberration", card.CardFaces[1].Name);
        Assert.Equal(card.CardFaces[0].ImageUris!.Normal, card.FrontImage!.Normal);
    }

    [Fact]
    public async Task GetCardAsync_returns_null_for_unknown_id()
    {
        Assert.Null(await FakeScryfallHandler.WithFixtures().CreateClient().GetCardAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task GetCollectionAsync_sends_75_names_per_request_and_reports_not_found()
    {
        var handler = FakeScryfallHandler.WithFixtures();
        var names = Enumerable.Range(1, 80).Select(i => $"Card {i}").ToList();

        var result = await handler.CreateClient().GetCollectionAsync(names);

        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, r => Assert.Equal(HttpMethod.Post, r.Method));
        Assert.Equal(75, IdentifierCount(handler.Requests[0].Body!));
        Assert.Equal(5, IdentifierCount(handler.Requests[1].Body!));
        // The fixture answers each batch with Sol Ring found and one name not found.
        Assert.Equal(["Sol Ring", "Sol Ring"], result.Cards.Select(c => c.Name));
        Assert.Equal(["Not A Real Card", "Not A Real Card"], result.NotFound);
    }

    [Fact]
    public async Task GetCollectionAsync_ignores_blank_and_duplicate_names()
    {
        var handler = FakeScryfallHandler.WithFixtures();

        await handler.CreateClient().GetCollectionAsync(["Sol Ring", "sol ring", "", "  "]);

        Assert.Equal(1, IdentifierCount(handler.Requests.Single().Body!));
    }

    [Fact]
    public async Task Requests_are_spaced_by_the_throttle_interval()
    {
        var client = FakeScryfallHandler.WithFixtures().CreateClient(TimeSpan.FromMilliseconds(100));
        var watch = Stopwatch.StartNew();

        await Task.WhenAll(
            client.AutocompleteAsync("lightning b"),
            client.AutocompleteAsync("lightning b"),
            client.AutocompleteAsync("lightning b"));

        // Three requests need at least two gaps of 100 ms.
        Assert.True(watch.ElapsedMilliseconds >= 190, $"took {watch.ElapsedMilliseconds} ms");
    }

    private static int IdentifierCount(string body) =>
        JsonDocument.Parse(body).RootElement.GetProperty("identifiers").GetArrayLength();
}
