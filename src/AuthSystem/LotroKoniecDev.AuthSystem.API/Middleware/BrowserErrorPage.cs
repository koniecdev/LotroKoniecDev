using System.Net.Mime;
using System.Text;

namespace LotroKoniecDev.AuthSystem.API.Middleware;

/// <summary>
/// The frame of the pages a browser gets when the auth origin cannot answer normally: the 429 page and
/// the 500 page. An API client keeps the machine-readable answer, so each page is written only for a
/// browser.
/// </summary>
/// <remarks>
/// These pages are written here and not as Razor pages. Re-executing into a Razor page would keep the
/// original POST, and the account pages have no POST handler for it. The markup is self-contained for
/// the same reason the account pages are: each of them sets <c>Layout = null</c> and carries its own
/// tokens.
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
    /// passed in without <c>HtmlEncoder</c>. Today every caller passes fixed Polish text, an <c>int</c>, and
    /// the CSP nonce, which is server-made base64url (#681, #682, #693).
    /// </summary>
    internal static string BuildHtml(string heading, string? nonce, params IReadOnlyList<string> paragraphs)
    {
        string styleTag = nonce is null ? "<style>" : $"<style nonce=\"{nonce}\">";
        string body = string.Join("\n        ", paragraphs.Select(paragraph => $"<p>{paragraph}</p>"));

        return $$"""
            <!DOCTYPE html>
            <html lang="pl">
            <head>
                <meta charset="utf-8" />
                <meta name="viewport" content="width=device-width, initial-scale=1.0" />
                <title>{{heading}} — lotro-translator.pl</title>
                <link rel="stylesheet" href="/fonts.css" />
                {{styleTag}}
                    :root {
                        --bg: oklch(0.16 0.011 80);
                        --surface: oklch(0.23 0.014 80);
                        --border: oklch(0.34 0.016 80);
                        --text: oklch(0.96 0.008 85);
                        --text-mute: oklch(0.80 0.012 85);
                        --accent: oklch(0.78 0.11 84);
                        --font-sans: "Manrope", ui-sans-serif, system-ui, -apple-system, sans-serif;
                    }
                    * { box-sizing: border-box; }
                    html, body { margin: 0; padding: 0; }
                    body {
                        background: var(--bg);
                        color: var(--text);
                        font-family: var(--font-sans);
                        font-size: 15px;
                        line-height: 1.6;
                        display: flex;
                        align-items: center;
                        justify-content: center;
                        min-height: 100vh;
                        padding: 24px;
                    }
                    main {
                        background: var(--surface);
                        border: 1px solid var(--border);
                        border-radius: 14px;
                        max-width: 34rem;
                        padding: 32px;
                    }
                    h1 { font-size: 1.35rem; margin: 0 0 12px; }
                    p { color: var(--text-mute); margin: 0 0 12px; }
                    a { color: var(--accent); }
                </style>
            </head>
            <body>
                <main>
                    <h1>{{heading}}</h1>
                    {{body}}
                </main>
            </body>
            </html>
            """;
    }
}
