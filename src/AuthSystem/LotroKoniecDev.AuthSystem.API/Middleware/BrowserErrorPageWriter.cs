namespace LotroKoniecDev.AuthSystem.API.Middleware;

/// <summary>
/// A Polish page for a browser instead of problem details (#867, #879). Status-code pages, exception
/// handlers and <c>Results.Problem</c> all write through <see cref="IProblemDetailsService"/>, so this one
/// writer covers them. The rate limiter's 429 page and OpenIddict's JSON errors fill the body themselves.
/// </summary>
/// <remarks>
/// It has to be registered before <c>AddProblemDetails()</c>: the service asks the writers in order, and
/// ASP.NET Core's own writer takes a browser too, because a browser accepts <c>*/*</c>.
/// <para>
/// The page never shows the problem details, which carry the stack trace in Development and Testing.
/// </para>
/// <para>
/// The write ignores <c>RequestAborted</c>: after a slow failure the user has often closed the tab, and
/// a cancelled write would throw inside the exception handler and log the failure twice more.
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
    /// A 429 here comes from an endpoint's own budget, not the limiter, so the page names no wait.
    /// </summary>
    private static string BuildHtml(HttpContext httpContext)
    {
        string? nonce = CspNonce.Get(httpContext);

        return httpContext.Response.StatusCode switch
        {
            >= StatusCodes.Status500InternalServerError => ServerErrorPage.BuildHtml(nonce),
            StatusCodes.Status404NotFound => ClientErrorPage.BuildNotFoundHtml(nonce),
            StatusCodes.Status429TooManyRequests => TooManyRequestsPage.BuildHtml(TimeSpan.Zero, nonce),
            _ when AntiforgeryFailureFilter.HasFailed(httpContext) => ClientErrorPage.BuildFormExpiredHtml(nonce),
            _ => ClientErrorPage.BuildHtml(nonce)
        };
    }
}
