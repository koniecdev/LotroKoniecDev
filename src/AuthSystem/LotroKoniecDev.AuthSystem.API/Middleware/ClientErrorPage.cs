namespace LotroKoniecDev.AuthSystem.API.Middleware;

/// <summary>
/// The pages for a 4xx answer with no page of its own (#879): a missing address, an expired form and
/// every other refusal.
/// </summary>
internal static class ClientErrorPage
{
    internal static string BuildNotFoundHtml() =>
        BrowserErrorPage.BuildHtml(
            "Nie ma takiej strony",
            "Pod tym adresem nie ma żadnej strony. Sprawdź, czy adres jest poprawny.",
            BrowserErrorPage.BackToLoginLink);

    /// <summary>
    /// Asks for a refresh, because a fresh load gives the form a new token and cookie. A browser that
    /// blocks this site's cookies fails every time, so the page names that cause too.
    /// </summary>
    internal static string BuildFormExpiredHtml() =>
        BrowserErrorPage.BuildHtml(
            "Formularz wygasł",
            "Nie możemy przyjąć tego formularza, bo jego zabezpieczenie straciło ważność. "
            + "Tak się dzieje na przykład wtedy, gdy ciasteczka zostały usunięte, a strona była wciąż otwarta.",
            "Wróć do formularza, odśwież stronę i wyślij go jeszcze raz.",
            "Jeśli to się powtarza, sprawdź, czy przeglądarka nie blokuje ciasteczek tej strony. "
            + "Bez nich nie da się zalogować.",
            BrowserErrorPage.BackToLoginLink);

    internal static string BuildHtml() =>
        BrowserErrorPage.BuildHtml(
            "Nie udało się obsłużyć żądania",
            "Tego żądania nie da się obsłużyć. Wróć do poprzedniej strony i spróbuj ponownie.",
            BrowserErrorPage.BackToLoginLink);
}
