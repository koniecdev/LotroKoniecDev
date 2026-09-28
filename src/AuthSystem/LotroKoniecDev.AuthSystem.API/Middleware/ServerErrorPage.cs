namespace LotroKoniecDev.AuthSystem.API.Middleware;

/// <summary>
/// The page a browser gets when a request fails in a way nobody planned for, for example when the
/// database is down during sign-in (#867). Without it the browser shows the problem-details JSON that
/// API clients get. <see cref="BrowserErrorPageWriter"/> chooses it for every 5xx answer.
/// </summary>
internal static class ServerErrorPage
{
    internal static string BuildHtml(string? nonce) =>
        BrowserErrorPage.BuildHtml(
            "Coś poszło nie tak",
            nonce,
            "Wystąpił błąd po naszej stronie. Spróbuj ponownie za chwilę.",
            BrowserErrorPage.BackToLoginLink);
}
