using System.Net;

namespace Maroos.Core.Email;

/// <summary>HTML helpers for email bodies. Everything user-supplied goes through <see cref="Encode"/>.</summary>
public static class EmailHtml
{
    public static string Encode(string text) => WebUtility.HtmlEncode(text);

    public static string Document(string bodyHtml) =>
        "<!DOCTYPE html><html><body style=\"font-family:Segoe UI,Arial,sans-serif;font-size:15px;line-height:1.5;color:#212529\">"
        + bodyHtml + "</body></html>";
}
