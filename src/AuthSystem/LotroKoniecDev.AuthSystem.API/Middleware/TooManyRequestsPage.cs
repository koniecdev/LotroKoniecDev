using System.Globalization;
using LotroKoniecDev.AuthSystem.API.Common;

namespace LotroKoniecDev.AuthSystem.API.Middleware;

/// <summary>
/// The page a browser gets when the rate limiter refuses a request.
/// </summary>
/// <remarks>
/// Without it a throttled sign-in ends on the problem-details JSON that <c>UseStatusCodePages</c>
/// writes: English, unstyled, and with no way back. That surface only became reachable from the main
/// forms when the account pages moved behind the limiter (#692).
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
        if (httpContext.Response.HasStarted || !BrowserErrorPage.WantsHtml(httpContext.Request))
        {
            return;
        }

        await BrowserErrorPage.WriteAsync(
            httpContext, BuildHtml(retryAfter, CspNonce.Get(httpContext)), cancellationToken);
    }

    /// <summary>
    /// Names the longest wait, and names a wait only when the limiter gave a number, so the page never
    /// invents one.
    /// </summary>
    /// <remarks>
    /// The limiter's number is an upper bound (see <c>OnRejected</c> in <c>Program.cs</c>, #892), so the
    /// sentence says "najpóźniej". Rounding up keeps it an upper bound.
    /// </remarks>
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

    internal static string BuildHtml(TimeSpan retryAfter, string? nonce) =>
        BrowserErrorPage.BuildHtml(
            "Za dużo prób",
            nonce,
            "Wysłano zbyt wiele żądań z tego połączenia. " + BuildWaitSentence(retryAfter),
            "Limit chroni konta przed zgadywaniem haseł i skrzynki przed zalewem wiadomości.",
            BrowserErrorPage.BackToLoginLink);
}
