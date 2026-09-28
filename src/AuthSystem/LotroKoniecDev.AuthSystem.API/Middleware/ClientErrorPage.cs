namespace LotroKoniecDev.AuthSystem.API.Middleware;

/// <summary>
/// The pages a browser gets for a 4xx answer that has no page of its own: an address that does not
/// exist, a form that failed its antiforgery check, and every other refused request (#879). Without them
/// the browser shows the problem-details JSON that API clients get.
/// </summary>
internal static class ClientErrorPage
{
    internal static string BuildNotFoundHtml(string? nonce) =>
        BrowserErrorPage.BuildHtml(
            "Nie ma takiej strony",
            nonce,
            "Pod tym adresem nie ma żadnej strony. Sprawdź, czy adres jest poprawny.",
            BrowserErrorPage.BackToLoginLink);

    /// <summary>
    /// A fresh load of the form gives it a new token and cookie. That is why the page asks for a refresh
    /// and not only for a second try. A browser that blocks this site's cookies fails the check every
    /// time, so the page names that cause too.
    /// </summary>
    internal static string BuildFormExpiredHtml(string? nonce) =>
        BrowserErrorPage.BuildHtml(
            "Formularz wygasł",
            nonce,
            "Nie możemy przyjąć tego formularza, bo jego zabezpieczenie straciło ważność. "
            + "Tak się dzieje na przykład wtedy, gdy ciasteczka zostały usunięte, a strona była wciąż otwarta.",
            "Wróć do formularza, odśwież stronę i wyślij go jeszcze raz.",
            "Jeśli to się powtarza, sprawdź, czy przeglądarka nie blokuje ciasteczek tej strony. "
            + "Bez nich nie da się zalogować.",
            BrowserErrorPage.BackToLoginLink);

    internal static string BuildHtml(string? nonce) =>
        BrowserErrorPage.BuildHtml(
            "Nie udało się obsłużyć żądania",
            nonce,
            "Tego żądania nie da się obsłużyć. Wróć do poprzedniej strony i spróbuj ponownie.",
            BrowserErrorPage.BackToLoginLink);
}
