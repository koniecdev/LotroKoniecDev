using System.Globalization;
using System.Net.Mime;
using System.Text;

namespace LotroKoniecDev.AuthSystem.API.Middleware;

/// <summary>
/// The page a browser gets when the rate limiter refuses a request.
/// </summary>
/// <remarks>
/// Without it a throttled sign-in ends on the bare "Status Code: 429" that <c>UseStatusCodePages</c>
/// writes — English, unstyled, and with no way back. That surface only became reachable from the main
/// forms when the account pages moved behind the limiter (#692).
/// It is written here rather than as a Razor page because re-executing into one would keep the original
/// POST, and these pages have no POST handler for it. The markup is self-contained for the same reason
/// the account pages are: each of them sets <c>Layout = null</c> and carries its own tokens.
/// </remarks>
internal static class TooManyRequestsPage
{
    /// <summary>
    /// Writes the page when the caller is a browser. An API client keeps the response it expects, so
    /// nothing is written for it and the pipeline answers as before.
    /// </summary>
    internal static async Task WriteIfBrowserRequestAsync(
        HttpContext httpContext,
        TimeSpan retryAfter,
        CancellationToken cancellationToken)
    {
        if (httpContext.Response.HasStarted || !WantsHtml(httpContext.Request))
        {
            return;
        }

        httpContext.Response.ContentType = $"{MediaTypeNames.Text.Html}; charset=utf-8";
        await httpContext.Response.WriteAsync(BuildHtml(retryAfter), Encoding.UTF8, cancellationToken);
    }

    private static bool WantsHtml(HttpRequest request)
    {
        return request.Headers.Accept.Any(value =>
            value?.Contains(MediaTypeNames.Text.Html, StringComparison.OrdinalIgnoreCase) == true);
    }

    /// <summary>
    /// Says how long the wait is only when the limiter gave a number, so the page never invents one.
    /// </summary>
    internal static string BuildWaitSentence(TimeSpan retryAfter)
    {
        if (retryAfter <= TimeSpan.Zero)
        {
            return "Odczekaj chwilę i spróbuj ponownie.";
        }

        int minutes = (int)Math.Ceiling(retryAfter.TotalMinutes);
        string unit = minutes == 1 ? "minutę" : minutes < 5 ? "minuty" : "minut";

        return "Spróbuj ponownie za około " + minutes.ToString(CultureInfo.InvariantCulture) + " " + unit + ".";
    }

    /// <summary>
    /// A raw string literal, so there is no Razor encoder behind it. Nothing a caller can influence may be
    /// interpolated here without <c>HtmlEncoder</c> — today the only hole is an <c>int</c> (#681, #682).
    /// </summary>
    private static string BuildHtml(TimeSpan retryAfter)
    {
        return $$"""
            <!DOCTYPE html>
            <html lang="pl">
            <head>
                <meta charset="utf-8" />
                <meta name="viewport" content="width=device-width, initial-scale=1.0" />
                <title>Za dużo prób — lotro-translator.pl</title>
                <link rel="stylesheet" href="/fonts.css" />
                <style>
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
                    <h1>Za dużo prób</h1>
                    <p>Wysłano zbyt wiele żądań z tego połączenia. {{BuildWaitSentence(retryAfter)}}</p>
                    <p>Limit chroni konta przed zgadywaniem haseł i skrzynki przed zalewem wiadomości.</p>
                    <p><a href="/Account/Login">Wróć do logowania</a></p>
                </main>
            </body>
            </html>
            """;
    }
}
