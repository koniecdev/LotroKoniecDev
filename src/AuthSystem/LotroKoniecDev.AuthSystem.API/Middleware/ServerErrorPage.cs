namespace LotroKoniecDev.AuthSystem.API.Middleware;

/// <summary>
/// The page a browser gets when a request fails in a way nobody planned for, for example when the
/// database is down during sign-in (#867). Without it the browser shows the problem-details JSON that
/// API clients get.
/// </summary>
/// <remarks>
/// It never names the exception, not even in Development or Testing: the log has the details, and a
/// page is the one place a stranger can read them.
/// </remarks>
internal static class ServerErrorPage
{
    /// <summary>
    /// Writes the page when the caller is a browser and tells the caller whether it did, so an API client
    /// can still get problem details.
    /// </summary>
    internal static async Task<bool> WriteIfBrowserRequestAsync(
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (!BrowserErrorPage.WantsHtml(httpContext.Request))
        {
            return false;
        }

        await BrowserErrorPage.WriteAsync(httpContext, BuildHtml(CspNonce.Get(httpContext)), cancellationToken);
        return true;
    }

    internal static string BuildHtml(string? nonce) =>
        BrowserErrorPage.BuildHtml(
            "Coś poszło nie tak",
            nonce,
            "Wystąpił błąd po naszej stronie. Spróbuj ponownie za chwilę.",
            "<a href=\"/Account/Login\">Wróć do logowania</a>");
}
