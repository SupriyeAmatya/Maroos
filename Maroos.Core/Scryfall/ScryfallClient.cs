// =====================================================================================================
// FILE: Maroos.Core/Scryfall/ScryfallClient.cs
// PURPOSE: The ONLY code in Maroos that talks to the Scryfall API over the internet.
//
// WHAT IT CAN DO:
//   SearchAsync        — full-text card search ("t:dragon c:r", "lightning bolt")      GET  /cards/search
//   AutocompleteAsync  — name suggestions while typing ("lightning b" -> names)        GET  /cards/autocomplete
//   GetCardAsync /     — one card by its Scryfall id                                   GET  /cards/{id}
//   GetCardJsonAsync
//   GetCollectionAsync — up to 75 cards per request, by id / name / set+number         POST /cards/collection
//
// RULES IT FOLLOWS:
//   * Every request goes through ScryfallThrottle so calls are >= 100 ms apart.
//   * "Not found" (HTTP 404) is a normal answer, returned as null/empty — not an error.
//   * Any other failure (500, 429 too many requests, network down) throws HttpRequestException,
//     which the pages catch and turn into a friendly "Couldn't reach Scryfall" message.
//
// Blazor pages never call this class directly; they go through Maroos.Web/Services/CardService,
// which adds the database cache on top.
// =====================================================================================================

// HttpStatusCode (e.g. NotFound) lives here.
using System.Net;
// Helpers like GetFromJsonAsync / PostAsJsonAsync / ReadFromJsonAsync.
using System.Net.Http.Json;
// JsonSerializer / JsonElement / JsonException.
using System.Text.Json;

namespace Maroos.Core.Scryfall;

/// <summary>
/// Typed HttpClient for the Scryfall API. The only place Maroos talks to Scryfall; components go through
/// <c>CardService</c>, which adds the database cache on top.
/// </summary>
// Primary constructor: dependency injection hands us
//   http     — an HttpClient already pointed at https://api.scryfall.com/ with the right headers (see Program.cs);
//   throttle — the app-wide ScryfallThrottle.
public sealed class ScryfallClient(HttpClient http, ScryfallThrottle throttle)
{
    /// <summary>Scryfall's limit for identifiers per <c>/cards/collection</c> request.</summary>
    // CollectionBatchSize: Scryfall accepts at most 75 identifiers in one bulk lookup,
    // so a 100-card deck list is sent as two requests (75 + 25).
    public const int CollectionBatchSize = 75;

    // SearchAsync: runs a Scryfall search.
    //   query — anything Scryfall's search box accepts (card names or syntax like "t:dragon c:r").
    //   page  — which page of results (Scryfall pages hold up to 175 cards).
    //   ct    — cancellation token (stop if the user navigates away).
    //   returns a page of cards, or an empty list when nothing matches.
    public async Task<ScryfallList<ScryfallCard>> SearchAsync(string query, int page = 1, CancellationToken ct = default)
    {
        // Don't bother Scryfall with an empty search.
        if (string.IsNullOrWhiteSpace(query))
            return ScryfallList<ScryfallCard>.Empty;

        // Build the URL. EscapeDataString turns spaces and symbols into URL-safe text ("lightning bolt" -> "lightning%20bolt").
        var url = $"cards/search?q={Uri.EscapeDataString(query.Trim())}&page={page}";
        // Scryfall answers a search with no matches with 404 not_found.
        // GetJsonAsync returns null for 404, and `?? Empty` turns that into "no results".
        return await GetJsonAsync<ScryfallList<ScryfallCard>>(url, ct) ?? ScryfallList<ScryfallCard>.Empty;
    }

    // AutocompleteAsync: card-name suggestions for what the user has typed so far.
    //   text — the partial name, e.g. "lightning b".
    //   returns up to ~20 full card names, e.g. ["Lightning Bolt", "Lightning Blow", ...].
    public async Task<IReadOnlyList<string>> AutocompleteAsync(string text, CancellationToken ct = default)
    {
        // Scryfall returns nothing for fewer than 2 characters, so skip the round trip.
        if (string.IsNullOrWhiteSpace(text) || text.Trim().Length < 2)
            return [];

        // The autocomplete endpoint answers with a "catalog": { "data": ["name1", "name2", ...] }.
        var catalog = await GetJsonAsync<ScryfallCatalog>($"cards/autocomplete?q={Uri.EscapeDataString(text.Trim())}", ct);
        return catalog?.Data ?? [];
    }

    // GetCardAsync: one card by its Scryfall id, parsed into a ScryfallCard (or null if Scryfall doesn't know it).
    public async Task<ScryfallCard?> GetCardAsync(Guid id, CancellationToken ct = default)
    {
        var json = await GetCardJsonAsync(id, ct);
        return json is null ? null : ParseCard(json);
    }

    /// <summary>Raw card JSON, for the database cache. Null when Scryfall has no card with this id.</summary>
    // GetCardJsonAsync: same as above but returns the raw JSON text, which CardService saves in the database.
    public Task<string?> GetCardJsonAsync(Guid id, CancellationToken ct = default) =>
        // Everything inside the lambda runs only when the throttle allows it.
        throttle.RunAsync(async () =>
        {
            // `using var` disposes the HTTP response (frees the connection) when this block ends.
            using var response = await http.GetAsync($"cards/{id}", ct);
            // Unknown card id: a normal "no such card" answer, not an error.
            if (response.StatusCode == HttpStatusCode.NotFound)
                return null;
            // Any other non-2xx status (server error, rate limited...) throws HttpRequestException.
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(ct);
        }, ct);

    /// <summary>Looks cards up by exact name, <see cref="CollectionBatchSize"/> names per request.</summary>
    // GetCollectionAsync (by names): convenience overload — cleans the name list, then calls the identifier version.
    // Cleaning: drop blank names, remove duplicates ignoring case ("Sol Ring" = "sol ring"), trim spaces.
    public Task<ScryfallCollectionResult> GetCollectionAsync(IEnumerable<string> names, CancellationToken ct = default) =>
        GetCollectionAsync(
            names.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).Select(n => CardIdentifier.ForName(n.Trim())),
            ct);

    /// <summary>Looks cards up by id, name or printing, <see cref="CollectionBatchSize"/> identifiers per request.</summary>
    // GetCollectionAsync (by identifiers): the bulk lookup used by deck import and deck pages.
    //   identifiers — what to look up (see CardIdentifier).
    //   returns every card found, every identifier NOT found, and which card answered which identifier.
    // PROCESS:
    //   1. remove duplicate identifiers and split them into chunks of 75;
    //   2. for each chunk, POST { "identifiers": [...] } to /cards/collection (throttled);
    //   3. keep each card's raw JSON (for the cache) alongside the parsed card;
    //   4. pair each identifier with the card that answered it (MatchToIdentifiers).
    public async Task<ScryfallCollectionResult> GetCollectionAsync(IEnumerable<CardIdentifier> identifiers, CancellationToken ct = default)
    {
        // cards: every card returned, across all batches.
        var cards = new List<FetchedCard>();
        // notFound: identifiers Scryfall had no card for.
        var notFound = new List<CardIdentifier>();
        // found: identifier -> the card that answered it.
        var found = new Dictionary<CardIdentifier, FetchedCard>();

        // Distinct() works because records compare by value; Chunk(75) yields arrays of at most 75.
        foreach (var batch in identifiers.Distinct().Chunk(CollectionBatchSize))
        {
            // Anonymous object serialised as { "identifiers": [ {...}, {...} ] }.
            var body = new { identifiers = batch };
            // One throttled POST per batch.
            var page = await throttle.RunAsync(async () =>
            {
                using var response = await http.PostAsJsonAsync("cards/collection", body, ScryfallJson.Options, ct);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadFromJsonAsync<ScryfallCollectionPage>(ScryfallJson.Options, ct);
            }, ct);

            // Shouldn't happen, but an empty response body just means "nothing from this batch".
            if (page is null)
                continue;
            // Keep each card's raw JSON so callers can cache exactly what Scryfall sent.
            // page.Data holds JsonElements (unparsed JSON); GetRawText() gives the exact JSON of each card.
            var batchCards = page.Data.Select(json => new FetchedCard(ParseCard(json.GetRawText()), json.GetRawText())).ToList();
            cards.AddRange(batchCards);
            notFound.AddRange(page.NotFound);
            // Work out which returned card belongs to which requested identifier.
            MatchToIdentifiers(batch, batchCards, page.NotFound, found);
        }

        return new ScryfallCollectionResult(cards, notFound, found);
    }

    /// <summary>
    /// Scryfall returns found cards in request order and echoes the identifiers it couldn't match, so walking
    /// the batch pairs each identifier with its card. If the counts don't line up, match on id/printing/name instead.
    /// </summary>
    // MatchToIdentifiers: fills `found` with identifier -> card pairs for one batch.
    //   batch         — the identifiers we sent, in order.
    //   batchCards    — the cards Scryfall returned, in order.
    //   batchNotFound — the identifiers Scryfall said it couldn't find.
    //   found         — the dictionary to fill in (shared across batches).
    // Example: sent [A, B, C], Scryfall says B not found and returns [cardA, cardC].
    //   Remove B -> [A, C]; 2 identifiers and 2 cards, so pair by position: A->cardA, C->cardC.
    // This was verified against the live API. If the counts ever disagree, we fall back to
    // CardIdentifier.Matches (compare ids / set+number / names) instead of trusting positions.
    private static void MatchToIdentifiers(
        IReadOnlyList<CardIdentifier> batch,
        IReadOnlyList<FetchedCard> batchCards,
        IReadOnlyList<CardIdentifier> batchNotFound,
        Dictionary<CardIdentifier, FetchedCard> found)
    {
        // A HashSet makes "was this one missing?" checks instant.
        var missing = batchNotFound.ToHashSet();
        // The identifiers that should have a card, still in request order.
        var requested = batch.Where(id => !missing.Contains(id)).ToList();

        // Normal case: counts line up, so pair by position.
        if (requested.Count == batchCards.Count)
        {
            for (var i = 0; i < requested.Count; i++)
                found[requested[i]] = batchCards[i];
            return;
        }

        // Safety net: pair by content instead.
        foreach (var identifier in requested)
        {
            if (batchCards.FirstOrDefault(c => identifier.Matches(c.Card)) is { } card)
                found[identifier] = card;
        }
    }

    // ParseCard: turns one card's JSON text into a ScryfallCard. Public because CardService uses it
    // to rebuild cards from the JSON stored in the database cache.
    public static ScryfallCard ParseCard(string json) =>
        JsonSerializer.Deserialize<ScryfallCard>(json, ScryfallJson.Options)
        // Deserialize returns null only for the literal JSON "null" — treat that as corrupt data.
        ?? throw new JsonException("Scryfall card JSON was empty.");

    // GetJsonAsync<T>: shared helper for GET requests that return JSON.
    //   url — path relative to https://api.scryfall.com/
    //   returns the parsed object, or null for 404 Not Found.
    // `where T : class` lets us return null for "not found".
    private Task<T?> GetJsonAsync<T>(string url, CancellationToken ct) where T : class =>
        throttle.RunAsync(async () =>
        {
            using var response = await http.GetAsync(url, ct);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return null;
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<T>(ScryfallJson.Options, ct);
        }, ct);

    // ScryfallCatalog: shape of the autocomplete answer: { "data": ["Lightning Bolt", ...] }.
    private sealed record ScryfallCatalog(IReadOnlyList<string> Data);

    // ScryfallCollectionPage: shape of the /cards/collection answer:
    //   { "data": [ {card}, ... ], "not_found": [ {identifier}, ... ] }.
    // Data stays as raw JsonElements so we can keep each card's exact JSON text.
    private sealed record ScryfallCollectionPage(IReadOnlyList<JsonElement> Data, IReadOnlyList<CardIdentifier> NotFound);
}
