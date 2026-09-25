// =====================================================================================================
// FILE: Maroos.Tests/Scryfall/ScryfallClientTests.cs
// PURPOSE: ScryfallClient: parsing real Scryfall JSON (normal and double-faced cards, prices in cents),
//   search/autocomplete URLs, 404 handling, /cards/collection batching in 75s and identifier pairing, and
//   the 100 ms throttle.
// Each test's name says what it proves; the '// TEST:' line above each one repeats it in plain words.
// =====================================================================================================

using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Maroos.Core.Scryfall;

namespace Maroos.Tests.Scryfall;

public class ScryfallClientTests
{
    // TEST: SearchAsync parses results and escapes the query.
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

    // TEST: SearchAsync returns empty when scryfall finds nothing.
    [Fact]
    public async Task SearchAsync_returns_empty_when_scryfall_finds_nothing()
    {
        var results = await FakeScryfallHandler.WithFixtures().CreateClient().SearchAsync("zzzznotacardzzzz");

        Assert.Empty(results.Data);
        Assert.Equal(0, results.TotalCards);
    }

    // TEST: SearchAsync throws on bad query syntax.
    [Fact]
    public async Task SearchAsync_throws_on_bad_query_syntax()
    {
        var handler = new FakeScryfallHandler
        {
            Respond = _ => FakeScryfallHandler.Json(HttpStatusCode.BadRequest, """{"object":"error","status":400}"""),
        };

        await Assert.ThrowsAsync<HttpRequestException>(() => handler.CreateClient().SearchAsync("t:"));
    }

    // TEST: SearchAsync skips blank queries.
    [Fact]
    public async Task SearchAsync_skips_blank_queries()
    {
        var handler = FakeScryfallHandler.WithFixtures();

        var results = await handler.CreateClient().SearchAsync("   ");

        Assert.Empty(results.Data);
        Assert.Empty(handler.Requests);
    }

    // TEST: AutocompleteAsync returns names.
    [Fact]
    public async Task AutocompleteAsync_returns_names()
    {
        var names = await FakeScryfallHandler.WithFixtures().CreateClient().AutocompleteAsync("lightning b");

        Assert.Equal(5, names.Count);
        Assert.Contains("Lightning Bolt", names);
    }

    // TEST: AutocompleteAsync skips text shorter than two characters.
    [Fact]
    public async Task AutocompleteAsync_skips_text_shorter_than_two_characters()
    {
        var handler = FakeScryfallHandler.WithFixtures();

        Assert.Empty(await handler.CreateClient().AutocompleteAsync("l"));
        Assert.Empty(handler.Requests);
    }

    // TEST: GetCardAsync parses card fields prices and legalities.
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

    // TEST: GetCardAsync takes the front image from the first face of a double faced card.
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

    // TEST: GetCardAsync returns null for unknown id.
    [Fact]
    public async Task GetCardAsync_returns_null_for_unknown_id()
    {
        Assert.Null(await FakeScryfallHandler.WithFixtures().CreateClient().GetCardAsync(Guid.NewGuid()));
    }

    // TEST: GetCollectionAsync sends 75 names per request and pairs each with its card.
    [Fact]
    public async Task GetCollectionAsync_sends_75_names_per_request_and_pairs_each_with_its_card()
    {
        var handler = FakeScryfallHandler.WithFixtures();
        var names = Enumerable.Range(1, 78).Select(i => $"Unknown Card {i}").Append("Sol Ring").Append("lightning bolt").ToList();

        var result = await handler.CreateClient().GetCollectionAsync(names);

        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, r => Assert.Equal(HttpMethod.Post, r.Method));
        Assert.Equal(75, IdentifierCount(handler.Requests[0].Body!));
        Assert.Equal(5, IdentifierCount(handler.Requests[1].Body!));

        Assert.Equal(78, result.NotFound.Count);
        Assert.Equal("Sol Ring", result.Found[CardIdentifier.ForName("Sol Ring")].Card.Name);
        Assert.Equal(Fixtures.LightningBoltId, result.Found[CardIdentifier.ForName("lightning bolt")].Card.Id);
        Assert.Contains("\"oracle_text\"", result.Found[CardIdentifier.ForName("Sol Ring")].Json);
    }

    // TEST: GetCollectionAsync finds printings by set and collector number and cards by id.
    [Fact]
    public async Task GetCollectionAsync_finds_printings_by_set_and_collector_number_and_cards_by_id()
    {
        var result = await FakeScryfallHandler.WithFixtures().CreateClient().GetCollectionAsync(
            [CardIdentifier.ForPrinting("c21", "263"), CardIdentifier.ForId(Fixtures.MountainId), CardIdentifier.ForPrinting("c21", "9999")]);

        Assert.Equal(Fixtures.SolRingC21Id, result.Found[CardIdentifier.ForPrinting("c21", "263")].Card.Id);
        Assert.Equal("Mountain", result.Found[CardIdentifier.ForId(Fixtures.MountainId)].Card.Name);
        Assert.Equal([CardIdentifier.ForPrinting("c21", "9999")], result.NotFound);
    }

    // TEST: GetCollectionAsync sends only the identifier fields that are set.
    [Fact]
    public async Task GetCollectionAsync_sends_only_the_identifier_fields_that_are_set()
    {
        var handler = FakeScryfallHandler.WithFixtures();

        await handler.CreateClient().GetCollectionAsync([CardIdentifier.ForPrinting("c21", "263")]);

        Assert.Equal("""{"identifiers":[{"set":"c21","collector_number":"263"}]}""", handler.Requests.Single().Body);
    }

    // TEST: GetCollectionAsync matches by name when scryfall results do not line up with the request.
    [Fact]
    public async Task GetCollectionAsync_matches_by_name_when_scryfall_results_do_not_line_up_with_the_request()
    {
        // Out of order, and the unknown name missing from not_found: the counts don't match, so pair by name.
        var handler = new FakeScryfallHandler
        {
            Respond = _ => FakeScryfallHandler.Json(HttpStatusCode.OK,
                $$"""{"not_found":[],"data":[{{Fixtures.Read("card-lightning-bolt.json")}},{{Fixtures.Read("card-sol-ring-c21.json")}}]}"""),
        };

        var result = await handler.CreateClient().GetCollectionAsync(["Sol Ring", "Lightning Bolt", "Nope"]);

        Assert.Equal(Fixtures.SolRingC21Id, result.Found[CardIdentifier.ForName("Sol Ring")].Card.Id);
        Assert.Equal(Fixtures.LightningBoltId, result.Found[CardIdentifier.ForName("Lightning Bolt")].Card.Id);
        Assert.False(result.Found.ContainsKey(CardIdentifier.ForName("Nope")));
    }

    // TEST: GetCollectionAsync ignores blank and duplicate names.
    [Fact]
    public async Task GetCollectionAsync_ignores_blank_and_duplicate_names()
    {
        var handler = FakeScryfallHandler.WithFixtures();

        await handler.CreateClient().GetCollectionAsync(["Sol Ring", "sol ring", "", "  "]);

        Assert.Equal(1, IdentifierCount(handler.Requests.Single().Body!));
    }

    // TEST: Requests are spaced by the throttle interval.
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

    // Helper: Identifier count.
    private static int IdentifierCount(string body) =>
        JsonDocument.Parse(body).RootElement.GetProperty("identifiers").GetArrayLength();
}
