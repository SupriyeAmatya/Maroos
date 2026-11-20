using System.Security.Claims;

namespace Maroos.Web.Services;

public static class ClaimsPrincipalExtensions
{
    /// <summary>The Identity user id of a signed-in user, or null for anonymous visitors.</summary>
    public static string? GetUserId(this ClaimsPrincipal user) =>
        user.Identity?.IsAuthenticated == true ? user.FindFirstValue(ClaimTypes.NameIdentifier) : null;
}
