using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Maroos.Core.Scryfall;

/// <summary>
/// Typed HttpClient for the Scryfall API. The only place Maroos talks to Scryfall; components go through
/// <c>CardService</c>, which adds the database cache on top.
/// </summary>
public sealed class ScryfallClient(HttpClient http, ScryfallThrottle throttle)
{
    /// <summary>Scryfall's limit for identifiers per <c>/cards/collection</c> request.</summary>
    public const int CollectionBatchSize = 75;

    public async Task<ScryfallList<ScryfallCard>> SearchAsync(string query, int page = 1, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return ScryfallList<ScryfallCard>.Empty;

        var url = $"cards/search?q={Uri.EscapeDataString(query.Trim())}&page={page}";
        // Scryfall answers a search with no matches with 404 not_found.
        return await GetJsonAsync<ScryfallList<ScryfallCard>>(url, ct) ?? ScryfallList<ScryfallCard>.Empty;
    }

    public async Task<IReadOnlyList<string>> AutocompleteAsync(string text, CancellationToken ct = default)
    {
        // Scryfall returns nothing for fewer than 2 characters, so skip the round trip.
        if (string.IsNullOrWhiteSpace(text) || text.Trim().Length < 2)
            return [];

        var catalog = await GetJsonAsync<ScryfallCatalog>($"cards/autocomplete?q={Uri.EscapeDataString(text.Trim())}", ct);
        return catalog?.Data ?? [];
    }

    public async Task<ScryfallCard?> GetCardAsync(Guid id, CancellationToken ct = default)
    {
        var json = await GetCardJsonAsync(id, ct);
        return json is null ? null : ParseCard(json);
    }

    /// <summary>Raw card JSON, for the database cache. Null when Scryfall has no card with this id.</summary>
    public Task<string?> GetCardJsonAsync(Guid id, CancellationToken ct = default) =>
        throttle.RunAsync(async () =>
        {
            using var response = await http.GetAsync($"cards/{id}", ct);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return null;
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(ct);
        }, ct);

    /// <summary>Looks cards up by exact name, <see cref="CollectionBatchSize"/> names per request.</summary>
    public Task<ScryfallCollectionResult> GetCollectionAsync(IEnumerable<string> names, CancellationToken ct = default) =>
        GetCollectionAsync(
            names.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).Select(n => CardIdentifier.ForName(n.Trim())),
            ct);

    /// <summary>Looks cards up by id, name or printing, <see cref="CollectionBatchSize"/> identifiers per request.</summary>
    public async Task<ScryfallCollectionResult> GetCollectionAsync(IEnumerable<CardIdentifier> identifiers, CancellationToken ct = default)
    {
        var cards = new List<FetchedCard>();
        var notFound = new List<CardIdentifier>();
        var found = new Dictionary<CardIdentifier, FetchedCard>();

        foreach (var batch in identifiers.Distinct().Chunk(CollectionBatchSize))
        {
            var body = new { identifiers = batch };
            var page = await throttle.RunAsync(async () =>
            {
                using var response = await http.PostAsJsonAsync("cards/collection", body, ScryfallJson.Options, ct);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadFromJsonAsync<ScryfallCollectionPage>(ScryfallJson.Options, ct);
            }, ct);

            if (page is null)
                continue;
            // Keep each card's raw JSON so callers can cache exactly what Scryfall sent.
            var batchCards = page.Data.Select(json => new FetchedCard(ParseCard(json.GetRawText()), json.GetRawText())).ToList();
            cards.AddRange(batchCards);
            notFound.AddRange(page.NotFound);
            MatchToIdentifiers(batch, batchCards, page.NotFound, found);
        }

        return new ScryfallCollectionResult(cards, notFound, found);
    }

    /// <summary>
    /// Scryfall returns found cards in request order and echoes the identifiers it couldn't match, so walking
    /// the batch pairs each identifier with its card. If the counts don't line up, match on id/printing/name instead.
    /// </summary>
    private static void MatchToIdentifiers(
        IReadOnlyList<CardIdentifier> batch,
        IReadOnlyList<FetchedCard> batchCards,
        IReadOnlyList<CardIdentifier> batchNotFound,
        Dictionary<CardIdentifier, FetchedCard> found)
    {
        var missing = batchNotFound.ToHashSet();
        var requested = batch.Where(id => !missing.Contains(id)).ToList();

        if (requested.Count == batchCards.Count)
        {
            for (var i = 0; i < requested.Count; i++)
                found[requested[i]] = batchCards[i];
            return;
        }

        foreach (var identifier in requested)
        {
            if (batchCards.FirstOrDefault(c => identifier.Matches(c.Card)) is { } card)
                found[identifier] = card;
        }
    }

    public static ScryfallCard ParseCard(string json) =>
        JsonSerializer.Deserialize<ScryfallCard>(json, ScryfallJson.Options)
        ?? throw new JsonException("Scryfall card JSON was empty.");

    private Task<T?> GetJsonAsync<T>(string url, CancellationToken ct) where T : class =>
        throttle.RunAsync(async () =>
        {
            using var response = await http.GetAsync(url, ct);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return null;
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<T>(ScryfallJson.Options, ct);
        }, ct);

    private sealed record ScryfallCatalog(IReadOnlyList<string> Data);

    private sealed record ScryfallCollectionPage(IReadOnlyList<JsonElement> Data, IReadOnlyList<CardIdentifier> NotFound);
}
