namespace Maroos.Core.Decks;

/// <summary>Groups cards the way Moxfield's deck view does, by one primary type each.</summary>
public static class CardTypes
{
    public const string Creature = "Creature";
    public const string Planeswalker = "Planeswalker";
    public const string Battle = "Battle";
    public const string Instant = "Instant";
    public const string Sorcery = "Sorcery";
    public const string Artifact = "Artifact";
    public const string Enchantment = "Enchantment";
    public const string Land = "Land";
    public const string Other = "Other";

    /// <summary>Display order of the groups on a deck page.</summary>
    public static readonly IReadOnlyList<string> DisplayOrder =
        [Creature, Planeswalker, Battle, Instant, Sorcery, Artifact, Enchantment, Land, Other];

    // When a card has several types, the first match wins: an Artifact Creature is a Creature,
    // and Dryad Arbor (Land Creature) is a Land.
    private static readonly string[] Precedence =
        [Land, Creature, Planeswalker, Battle, Instant, Sorcery, Artifact, Enchantment];

    /// <summary>The primary type of a type line; double-faced cards use their front face.</summary>
    public static string PrimaryType(string? typeLine)
    {
        if (string.IsNullOrWhiteSpace(typeLine))
            return Other;

        var front = typeLine.Split(" // ")[0];
        // Subtypes after the dash ("Creature — Goblin") don't count.
        var types = front.Split('—')[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);

        return Precedence.FirstOrDefault(t => types.Contains(t, StringComparer.OrdinalIgnoreCase)) ?? Other;
    }

    public static string GroupHeading(string type) => type switch
    {
        Sorcery => "Sorceries",
        Other => "Other",
        _ => type + "s",
    };
}
