using System.Text.Json;
using System.Text.Json.Serialization;

namespace Maroos.Core.Scryfall;

public static class ScryfallJson
{
    /// <summary>Scryfall uses snake_case property names.</summary>
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        // Collection identifiers must only send the fields that are set.
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
