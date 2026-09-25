// =====================================================================================================
// FILE: Maroos.Web/Services/ClaimsPrincipalExtensions.cs
// PURPOSE: One helper, GetUserId(), used by pages to find out who is signed in.
// A ClaimsPrincipal is .NET's "the current user": a set of claims (id, name, email...) from the login cookie.
// =====================================================================================================

using System.Security.Claims;

namespace Maroos.Web.Services;

public static class ClaimsPrincipalExtensions
{
    /// <summary>The Identity user id of a signed-in user, or null for anonymous visitors.</summary>
    // Usage: `var userId = authState.User.GetUserId();` — the id (NameIdentifier claim) is what the
    // services take for ownership checks (OwnerId, SellerId, BidderId...).
    public static string? GetUserId(this ClaimsPrincipal user) =>
        user.Identity?.IsAuthenticated == true ? user.FindFirstValue(ClaimTypes.NameIdentifier) : null;
}
