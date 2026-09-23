using Maroos.Core.Decks;

namespace Maroos.Tests.Decks;

public class CardTypesTests
{
    [Theory]
    [InlineData("Instant", CardTypes.Instant)]
    [InlineData("Creature — Goblin", CardTypes.Creature)]
    [InlineData("Artifact Creature — Golem", CardTypes.Creature)]
    [InlineData("Legendary Enchantment Creature — God", CardTypes.Creature)]
    [InlineData("Land Creature — Forest Dryad", CardTypes.Land)]
    [InlineData("Basic Land — Mountain", CardTypes.Land)]
    [InlineData("Legendary Planeswalker — Jace", CardTypes.Planeswalker)]
    [InlineData("Kindred Sorcery — Goblin", CardTypes.Sorcery)]
    [InlineData("Artifact — Equipment", CardTypes.Artifact)]
    [InlineData("Enchantment — Aura", CardTypes.Enchantment)]
    [InlineData("Battle — Siege", CardTypes.Battle)]
    [InlineData("Creature — Human Wizard // Creature — Human Insect", CardTypes.Creature)]
    [InlineData("Sorcery // Land", CardTypes.Sorcery)]
    [InlineData("Scheme", CardTypes.Other)]
    [InlineData("", CardTypes.Other)]
    [InlineData(null, CardTypes.Other)]
    public void PrimaryType_picks_one_group_per_card(string? typeLine, string expected) =>
        Assert.Equal(expected, CardTypes.PrimaryType(typeLine));

    [Theory]
    [InlineData(CardTypes.Creature, "Creatures")]
    [InlineData(CardTypes.Sorcery, "Sorceries")]
    [InlineData(CardTypes.Other, "Other")]
    public void GroupHeading_pluralizes(string type, string heading) => Assert.Equal(heading, CardTypes.GroupHeading(type));
}
