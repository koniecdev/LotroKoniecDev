namespace LotroKoniecDev.AuthSystem.API.Middleware;

/// <summary>
/// For every 5xx answer, such as a database outage during sign-in (#867).
/// </summary>
internal static class ServerErrorPage
{
    internal static string BuildHtml() =>
        BrowserErrorPage.BuildHtml(
            "Coś poszło nie tak",
            "Wystąpił błąd po naszej stronie. Spróbuj ponownie za chwilę.",
            BrowserErrorPage.BackToLoginLink);
}
