using LotroKoniecDev.Frontend.Infrastructure.Security;

namespace LotroKoniecDev.Frontend.Infrastructure.CookieConsent;

/// <summary>
/// Maps the route that records cookie consent (LEGAL-04). The banner posts here as a plain HTML form,
/// with no script and no interactivity, so accepting works even with JavaScript turned off. The
/// minimal-API form binding checks the antiforgery token.
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
    /// The accept route's handler, internal so a unit test can call it without a web host. It writes the
    /// consent cookie and redirects back to the page the form came from, refusing a target outside this
    /// site.
    /// </summary>
    internal static IResult AcceptCookieConsent(HttpContext context, IFormCollection form)
    {
        string returnPath = form["returnPath"].ToString();

        context.Response.Cookies.Append(
            CookieConsentCookie.Name,
            "true",
            CookieConsentCookie.BuildOptions(context.Request.IsHttps));

        string safeReturn = LocalReturnUrl.Sanitize(returnPath) ?? "/";
        return Results.Redirect(safeReturn);
    }
}
