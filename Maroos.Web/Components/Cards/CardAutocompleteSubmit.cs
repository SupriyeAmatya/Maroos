// =====================================================================================================
// FILE: Maroos.Web/Components/Cards/CardAutocompleteSubmit.cs
// PURPOSE: What CardAutocomplete hands its parent on submit. The search page runs a Scryfall query with
// Text; the deck page adds the card named Text. IsSuggestion tells the parent Text is an exact card name.
// `readonly record struct`: a small immutable value type (no heap allocation) with value equality.
// =====================================================================================================

namespace Maroos.Web.Components.Cards;

/// <param name="Text">What the user submitted.</param>
/// <param name="IsSuggestion">True when picked from the suggestion list, so <paramref name="Text"/> is an exact card name.</param>
public readonly record struct CardAutocompleteSubmit(string Text, bool IsSuggestion);
