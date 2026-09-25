// =====================================================================================================
// FILE: Maroos.Core/Decks/CardTypes.cs
// PURPOSE: Decides which group a card goes in on the deck page ("Creatures (12)", "Instants (8)"...),
// the way Moxfield groups decks. Every card goes into exactly ONE group, even when it has several
// types ("Artifact Creature" counts as a Creature).
// =====================================================================================================

namespace Maroos.Core.Decks;

/// <summary>Groups cards the way Moxfield's deck view does, by one primary type each.</summary>
public static class CardTypes
{
    // The group names, as constants so typos are caught by the compiler.
    public const string Creature = "Creature";
    public const string Planeswalker = "Planeswalker";
    public const string Battle = "Battle";
    public const string Instant = "Instant";
    public const string Sorcery = "Sorcery";
    public const string Artifact = "Artifact";
    public const string Enchantment = "Enchantment";
    public const string Land = "Land";
    // Other: anything that doesn't match (e.g. unusual card types) or has no type information.
    public const string Other = "Other";

    /// <summary>Display order of the groups on a deck page.</summary>
    // DisplayOrder: the order groups appear on screen (matches Moxfield).
    public static readonly IReadOnlyList<string> DisplayOrder =
        [Creature, Planeswalker, Battle, Instant, Sorcery, Artifact, Enchantment, Land, Other];

    // When a card has several types, the first match wins: an Artifact Creature is a Creature,
    // and Dryad Arbor (Land Creature) is a Land.
    // Precedence: the order in which types are checked when deciding a card's group.
    private static readonly string[] Precedence =
        [Land, Creature, Planeswalker, Battle, Instant, Sorcery, Artifact, Enchantment];

    /// <summary>The primary type of a type line; double-faced cards use their front face.</summary>
    // PrimaryType: type line -> group name.
    // Example: "Legendary Artifact Creature — Golem" -> front part "Legendary Artifact Creature"
    //          -> words [Legendary, Artifact, Creature] -> first in Precedence found: Creature.
    public static string PrimaryType(string? typeLine)
    {
        // No type information (e.g. Scryfall data missing): put it under Other.
        if (string.IsNullOrWhiteSpace(typeLine))
            return Other;

        // Double-faced cards look like "Creature — Human // Creature — Insect": use the front face only.
        var front = typeLine.Split(" // ")[0];
        // Subtypes after the dash ("Creature — Goblin") don't count.
        // types: the individual type words before the dash.
        var types = front.Split('—')[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // The first type in Precedence that appears in the card's words; Other if none do.
        return Precedence.FirstOrDefault(t => types.Contains(t, StringComparer.OrdinalIgnoreCase)) ?? Other;
    }

    // GroupHeading: singular type -> plural heading. "Sorcery" is irregular; "Other" stays as is.
    public static string GroupHeading(string type) => type switch
    {
        Sorcery => "Sorceries",
        Other => "Other",
        _ => type + "s",
    };
}
