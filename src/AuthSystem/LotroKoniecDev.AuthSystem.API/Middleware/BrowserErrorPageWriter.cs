namespace LotroKoniecDev.AuthSystem.API.Middleware;

/// <summary>
/// Answers a browser with a Polish page instead of problem details (#867, #879). Every error answer of
/// the auth origin goes through <see cref="IProblemDetailsService"/>: the status-code pages (a 404, a
/// failed form check) and every exception handler. So this one writer covers all of them, and an API
/// client still gets the JSON.
/// </summary>
/// <remarks>
/// It has to be registered before <c>AddProblemDetails()</c>. The service asks the writers in order, and
/// ASP.NET Core's own writer takes a browser too, because a browser also accepts <c>*/*</c>.
/// <para>
/// The page never shows the problem details, not even in Development or Testing, where they carry the
/// exception's message and stack trace. The log has the details, and a page is the one place a stranger
/// can read them.
/// </para>
/// <para>
/// The write does not listen to <c>RequestAborted</c>. A failure that takes long, like a database
/// timeout, is often the one after which the user has closed the tab. A cancelled write would then throw
/// inside the exception handler and log the same failure two more times. Kestrel drops a write to a
/// closed connection anyway.
/// </para>
/// </remarks>
internal sealed class BrowserErrorPageWriter : IProblemDetailsWriter
{
    public bool CanWrite(ProblemDetailsContext context) =>
        context.HttpContext.Response.StatusCode >= StatusCodes.Status400BadRequest
        && BrowserErrorPage.WantsHtml(context.HttpContext.Request);

    public ValueTask WriteAsync(ProblemDetailsContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        HttpContext httpContext = context.HttpContext;
        return new ValueTask(BrowserErrorPage.WriteAsync(httpContext, BuildHtml(httpContext), CancellationToken.None));
    }

    /// <summary>
    /// The 429 page is not chosen here: the rate limiter writes it itself, because only the limiter knows
    /// how long the wait is.
    /// </summary>
    private static string BuildHtml(HttpContext httpContext)
    {
        string? nonce = CspNonce.Get(httpContext);

        return httpContext.Response.StatusCode switch
        {
            >= StatusCodes.Status500InternalServerError => ServerErrorPage.BuildHtml(nonce),
            StatusCodes.Status404NotFound => ClientErrorPage.BuildNotFoundHtml(nonce),
            _ when AntiforgeryFailureFilter.HasFailed(httpContext) => ClientErrorPage.BuildFormExpiredHtml(nonce),
            _ => ClientErrorPage.BuildHtml(nonce)
        };
    }
}
