// =====================================================================================================
// FILE: Maroos.Core/Email/EmailHtml.cs
// PURPOSE: The HTML side of every email. Card names and display names come from users and Scryfall,
// so they are always encoded: a card named "<script>" shows as text, never as markup.
// =====================================================================================================

using System.Net;

namespace Maroos.Core.Email;

/// <summary>HTML helpers for email bodies. Everything user-supplied goes through <see cref="Encode"/>.</summary>
public static class EmailHtml
{
    // Encode: < > & " ' become &lt; &gt; &amp; &quot; &#39;.
    public static string Encode(string text) => WebUtility.HtmlEncode(text);

    // Document: wraps a body in a minimal HTML page with inline styles (email programs ignore style sheets).
    public static string Document(string bodyHtml) =>
        "<!DOCTYPE html><html><body style=\"font-family:Segoe UI,Arial,sans-serif;font-size:15px;line-height:1.5;color:#212529\">"
        + bodyHtml + "</body></html>";
}
