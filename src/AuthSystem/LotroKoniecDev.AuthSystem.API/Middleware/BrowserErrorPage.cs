using System.Net.Mime;
using System.Text;

namespace LotroKoniecDev.AuthSystem.API.Middleware;

/// <summary>
/// The frame of the 429, 5xx and 4xx pages a browser gets; an API client keeps the machine-readable
/// answer.
/// </summary>
/// <remarks>
/// Not Razor pages: re-executing into one would keep the original POST, which the account pages have no
/// handler for. Their styles live in <c>wwwroot/error-page.css</c>, because the auth CSP admits no inline
/// style (#833).
/// </remarks>
internal static class BrowserErrorPage
{
    internal const string BackToLoginLink = "<a href=\"/Account/Login\">Wróć do logowania</a>";

    /// <summary>
    /// A browser names <c>text/html</c>; the frontend asks for the HATEOAS JSON type and <c>curl</c> or
    /// <c>fetch</c> send <c>*/*</c>. <c>q=0</c> means "no HTML".
    /// </summary>
    internal static bool WantsHtml(HttpRequest request)
    {
        return request.GetTypedHeaders().Accept.Any(value =>
            value.MediaType.Equals(MediaTypeNames.Text.Html, StringComparison.OrdinalIgnoreCase)
            && value.Quality is not 0.0);
    }

    internal static Task WriteAsync(HttpContext httpContext, string html, CancellationToken cancellationToken)
    {
        httpContext.Response.ContentType = $"{MediaTypeNames.Text.Html}; charset=utf-8";
        return httpContext.Response.WriteAsync(html, Encoding.UTF8, cancellationToken);
    }

    /// <summary>
    /// A raw string literal with no Razor encoder behind it, so nothing a caller can influence may come in
    /// without <c>HtmlEncoder</c>. Today callers pass fixed text and an <c>int</c> (#681, #682).
    /// </summary>
    internal static string BuildHtml(string heading, params IReadOnlyList<string> paragraphs)
    {
        string body = string.Join("\n        ", paragraphs.Select(paragraph => $"<p>{paragraph}</p>"));

        return $"""
            <!DOCTYPE html>
            <html lang="pl">
            <head>
                <meta charset="utf-8" />
                <meta name="viewport" content="width=device-width, initial-scale=1.0" />
                <title>{heading} — lotro-translator.pl</title>
                <link rel="stylesheet" href="/fonts.css" />
                <link rel="stylesheet" href="/error-page.css" />
            </head>
            <body>
                <main>
                    <h1>{heading}</h1>
                    {body}
                </main>
            </body>
            </html>
            """;
    }
}
