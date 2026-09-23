using LotroKoniecDev.AuthSystem.API.Settings;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace LotroKoniecDev.AuthSystem.API.Middleware;

/// <summary>
/// Adds the browser security headers to every response of the auth origin (#693), the same set the
/// frontend sends: a Content-Security-Policy, <c>X-Content-Type-Options: nosniff</c>,
/// <c>Referrer-Policy: no-referrer</c> and <c>X-Frame-Options: DENY</c> next to the CSP's
/// <c>frame-ancestors 'none'</c>.
/// The headers are written in <see cref="HttpResponse.OnStarting(Func{Task})"/>. That way they also
/// reach error and 429 responses, and <c>DENY</c> replaces the <c>SAMEORIGIN</c> that antiforgery adds
/// only to pages that happen to render a form.
/// </summary>
internal sealed class SecurityHeadersMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IReadOnlyList<string> _frontendOrigins;

    public SecurityHeadersMiddleware(RequestDelegate next, IOptions<OpenIddictSettings> openIddictSettings)
    {
        _next = next;
        _frontendOrigins = FrontendOrigins(openIddictSettings.Value.WebClient);
    }

    public Task InvokeAsync(HttpContext context)
    {
        // Issued before the page renders, because the page reads it. The headers wait for OnStarting:
        // the exception handler clears the response before it runs again, and headers set now would be lost.
        string nonce = CspNonce.Issue(context);

        context.Response.OnStarting(() =>
        {
            IHeaderDictionary responseHeaders = context.Response.Headers;
            foreach (KeyValuePair<string, string> header in BuildHeaders(nonce, _frontendOrigins))
            {
                responseHeaders[header.Key] = header.Value;
            }

            return Task.CompletedTask;
        });

        return _next(context);
    }

    internal static IReadOnlyDictionary<string, string> BuildHeaders(
        string nonce,
        IReadOnlyList<string> frontendOrigins) =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [HeaderNames.ContentSecurityPolicy] = BuildContentSecurityPolicy(nonce, frontendOrigins),
            [HeaderNames.XContentTypeOptions] = "nosniff",
            ["Referrer-Policy"] = "no-referrer",
            [HeaderNames.XFrameOptions] = "DENY"
        };

    /// <summary>
    /// Builds the CSP. <c>script-src</c> is <c>'self'</c> with no nonce: the one script the pages need
    /// is a file (<c>login.js</c>), and a new inline script must be moved to a file too (#670).
    /// Each page keeps its styles in an inline <c>&lt;style&gt;</c> block, so <c>style-src</c> admits
    /// the per-request nonce and never <c>'unsafe-inline'</c>.
    /// <c>form-action</c> lists the frontend origins. A sign-in POST ends in a redirect to the
    /// frontend's callback, and Chrome checks every redirect of a form submission against it.
    /// </summary>
    internal static string BuildContentSecurityPolicy(string nonce, IReadOnlyList<string> frontendOrigins) =>
        string.Join("; ",
        [
            "default-src 'self'",
            "base-uri 'self'",
            "object-src 'none'",
            "frame-ancestors 'none'",
            "script-src 'self'",
            $"style-src 'self' 'nonce-{nonce}'",
            "font-src 'self'",
            string.Join(' ', ["form-action", "'self'", .. frontendOrigins])
        ]);

    /// <summary>
    /// The origins of the web client's redirect and post-logout URIs: the only places a form on this
    /// origin may end up. A value that is not an absolute http(s) URL is skipped; the settings
    /// validator reports it at startup. The origin is built from the scheme and the authority, never
    /// <c>GetLeftPart</c>, which would keep a <c>user@</c> part that no CSP source may carry.
    /// OpenIddict checks redirects against the client row in the database, and the seeder writes that
    /// row only when it is missing. This reads the configuration, so it assumes the two agree. On the
    /// boxes both come from the same domain setting; if they drift apart, sign-in fails visibly.
    /// </summary>
    internal static IReadOnlyList<string> FrontendOrigins(WebClientSettings webClient) =>
        webClient.RedirectUris
            .Concat(webClient.PostLogoutRedirectUris)
            .Select(uri => Uri.TryCreate(uri, UriKind.Absolute, out Uri? parsed) ? parsed : null)
            .OfType<Uri>()
            .Where(uri => uri.Scheme is "https" or "http")
            .Select(uri => $"{uri.Scheme}://{uri.Authority}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
