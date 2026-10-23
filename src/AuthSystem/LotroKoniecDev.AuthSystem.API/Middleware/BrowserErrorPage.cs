using System.Net.Mime;
using System.Text;

namespace LotroKoniecDev.AuthSystem.API.Middleware;

/// <summary>
/// The frame of the pages a browser gets when the auth origin cannot answer normally: the 429 page, the
/// 5xx page and the 4xx pages. An API client keeps the machine-readable answer, so each page is written
/// only for a browser.
/// </summary>
/// <remarks>
/// These pages are written here and not as Razor pages. Re-executing into a Razor page would keep the
/// original POST, and the account pages have no POST handler for it. Their styles live in
/// <c>wwwroot/error-page.css</c>, because the auth CSP admits no inline style (#833).
/// </remarks>
internal static class BrowserErrorPage
{
    internal const string BackToLoginLink = "<a href=\"/Account/Login\">Wróć do logowania</a>";

    /// <summary>
    /// A browser names <c>text/html</c> when it opens a page. An API client does not: the frontend asks for
    /// the HATEOAS JSON type, and <c>curl</c> or <c>fetch</c> send <c>*/*</c>. A caller that names it with
    /// <c>q=0</c> says it does not accept HTML, so it keeps the JSON.
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
    /// A raw string literal, so there is no Razor encoder behind it. Nothing a caller can influence may be
    /// passed in without <c>HtmlEncoder</c>. Today every caller passes fixed Polish text and an <c>int</c>
    /// (#681, #682).
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
