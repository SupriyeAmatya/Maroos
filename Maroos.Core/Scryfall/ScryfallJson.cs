// =====================================================================================================
// FILE: Maroos.Core/Scryfall/ScryfallJson.cs
// PURPOSE: The JSON settings used for every conversion to/from Scryfall's JSON.
//
// Scryfall names fields in "snake_case" (mana_cost, collector_number). C# uses "PascalCase"
// (ManaCost, CollectorNumber). These options tell System.Text.Json how to translate between them.
// =====================================================================================================

// System.Text.Json is .NET's built-in JSON library.
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Maroos.Core.Scryfall;

// ScryfallJson: a static holder for one shared JsonSerializerOptions object
// (creating options is relatively expensive, so the same instance is reused everywhere).
public static class ScryfallJson
{
    /// <summary>Scryfall uses snake_case property names.</summary>
    // Options: start from the "Web" defaults (case-insensitive matching, etc.), then:
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        // Map C# "CollectorNumber" <-> JSON "collector_number".
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        // Collection identifiers must only send the fields that are set.
        // When WRITING JSON, leave out properties whose value is null. Scryfall's /cards/collection
        // endpoint rejects identifiers like { "id": null, "name": "Sol Ring" }; it needs { "name": "Sol Ring" }.
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
