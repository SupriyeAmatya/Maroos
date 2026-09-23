namespace Maroos.Web.Components.Cards;

/// <param name="Text">What the user submitted.</param>
/// <param name="IsSuggestion">True when picked from the suggestion list, so <paramref name="Text"/> is an exact card name.</param>
public readonly record struct CardAutocompleteSubmit(string Text, bool IsSuggestion);
