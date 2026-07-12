namespace LotroKoniecDev.Frontend.Infrastructure.CookieConsent;

/// <summary>
/// Maps the cookie-consent acknowledgement route (LEGAL-04). The banner posts here as a plain HTML
/// form — no JS, no interactivity — so acceptance works with JavaScript disabled; the antiforgery
/// token is validated by the form-binding minimal-API pipeline.
/// </summary>
internal static class CookieConsentEndpointsExtensions
{
    internal const string AcceptPath = "/cookie-consent/accept";

    extension(IEndpointRouteBuilder endpoints)
    {
        public IEndpointRouteBuilder MapCookieConsentEndpoints()
        {
            endpoints.MapPost(AcceptPath, AcceptCookieConsent).AllowAnonymous();
            return endpoints;
        }
    }

    /// <summary>
    /// The accept route's request delegate, exposed internally so it can be unit-tested without a
    /// web host: persists the consent cookie and redirects back to the page the form was posted
    /// from, guarding against open redirects.
    /// </summary>
    internal static IResult AcceptCookieConsent(HttpContext context, IFormCollection form)
    {
        string returnPath = form["returnPath"].ToString();

        context.Response.Cookies.Append(
            CookieConsentCookie.Name,
            "true",
            CookieConsentCookie.BuildOptions(context.Request.IsHttps));

        string safeReturn = IsLocalPath(returnPath) ? returnPath : "/";
        return Results.Redirect(safeReturn);
    }

    // Control characters are rejected because the WHATWG URL parser strips ASCII tab/newline —
    // "/\t/evil.example" would reach the browser as the protocol-relative "//evil.example"
    // (mirrors ASP.NET Core's UrlHelper.IsLocalUrl hardening).
    private static bool IsLocalPath(string path) =>
        path.StartsWith('/')
        && !path.StartsWith("//", StringComparison.Ordinal)
        && !path.StartsWith("/\\", StringComparison.Ordinal)
        && !path.Any(char.IsControl);
}
