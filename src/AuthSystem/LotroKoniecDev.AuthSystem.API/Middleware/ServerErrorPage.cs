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
    /// Writes the page for a browser and returns true when it did, so an API client can still get
    /// problem details.
    /// </summary>
    /// <remarks>
    /// The write does not listen to <c>RequestAborted</c>. A failure that takes long, like a database
    /// timeout, is often the one after which the user has closed the tab. A cancelled write would then
    /// throw inside the exception handler and log the same failure two more times. Kestrel drops a write
    /// to a closed connection anyway.
    /// </remarks>
    internal static async Task<bool> WriteIfBrowserRequestAsync(HttpContext httpContext)
    {
        if (!BrowserErrorPage.WantsHtml(httpContext.Request))
        {
            return false;
        }

        await BrowserErrorPage.WriteAsync(httpContext, BuildHtml(CspNonce.Get(httpContext)), CancellationToken.None);
        return true;
    }

    internal static string BuildHtml(string? nonce) =>
        BrowserErrorPage.BuildHtml(
            "Coś poszło nie tak",
            nonce,
            "Wystąpił błąd po naszej stronie. Spróbuj ponownie za chwilę.",
            BrowserErrorPage.BackToLoginLink);
}
