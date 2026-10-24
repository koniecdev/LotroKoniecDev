using LotroKoniecDev.Frontend.Infrastructure.Security;

namespace LotroKoniecDev.Frontend.Infrastructure.CookieConsent;

/// <summary>
/// The banner posts here as a plain HTML form, so accepting works with JavaScript off (LEGAL-04). The
/// form parameter makes the framework check the antiforgery token.
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

    /// <summary>Internal so a unit test can call it without a web host.</summary>
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
