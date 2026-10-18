using System.Globalization;
using LotroKoniecDev.AuthSystem.API.Common;

namespace LotroKoniecDev.AuthSystem.API.Middleware;

/// <summary>
/// Without it a throttled sign-in ends on English problem-details JSON with no way back (#692).
/// </summary>
internal static class TooManyRequestsPage
{
    /// <summary>
    /// Writes nothing for an API client, which keeps the response it expects.
    /// </summary>
    internal static async Task WriteIfBrowserRequestAsync(
        HttpContext httpContext,
        TimeSpan retryAfter,
        CancellationToken cancellationToken)
    {
        if (httpContext.Response.HasStarted || !BrowserErrorPage.WantsHtml(httpContext.Request))
        {
            return;
        }

        await BrowserErrorPage.WriteAsync(httpContext, BuildHtml(retryAfter), cancellationToken);
    }

    /// <summary>
    /// Names a wait only when the limiter gave one. It is an upper bound (<c>OnRejected</c> in
    /// <c>Program.cs</c>, #892), so the sentence says "najpóźniej" and rounds up.
    /// </summary>
    internal static string BuildWaitSentence(TimeSpan retryAfter)
    {
        if (retryAfter <= TimeSpan.Zero)
        {
            return "Odczekaj chwilę i spróbuj ponownie.";
        }

        int minutes = (int)Math.Ceiling(retryAfter.TotalMinutes);

        return "Spróbuj ponownie później. Limit odnowi się najpóźniej za "
            + minutes.ToString(CultureInfo.InvariantCulture) + " "
            + PolishPlural.Pick(minutes, "minutę", "minuty", "minut") + ".";
    }

    internal static string BuildHtml(TimeSpan retryAfter) =>
        BrowserErrorPage.BuildHtml(
            "Za dużo prób",
            "Wysłano zbyt wiele żądań z tego połączenia. " + BuildWaitSentence(retryAfter),
            "Limit chroni konta przed zgadywaniem haseł i skrzynki przed zalewem wiadomości.",
            BrowserErrorPage.BackToLoginLink);
}
