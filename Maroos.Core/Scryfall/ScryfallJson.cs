using System.Text.Json;

namespace Maroos.Core.Scryfall;

public static class ScryfallJson
{
    /// <summary>Scryfall uses snake_case property names.</summary>
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };
}
