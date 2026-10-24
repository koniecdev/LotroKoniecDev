using System.Globalization;
using LotroKoniecDev.AuthSystem.API.Common;
using LotroKoniecDev.AuthSystem.API.Settings;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace LotroKoniecDev.AuthSystem.API.Middleware;

/// <summary>
/// The same headers the frontend sends, on every auth response (#693). Written in
/// <see cref="HttpResponse.OnStarting(Func{Task})"/>, so they also reach error and 429 responses, and
/// <c>DENY</c> replaces the <c>SAMEORIGIN</c> antiforgery adds only to pages with a form.
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
    /// <c>script-src 'self'</c> with no nonce: page script lives in files such as <c>login.js</c> (#670).
    /// Each page keeps its styles inline, so <c>style-src</c> admits the nonce, never
    /// <c>'unsafe-inline'</c>. <c>form-action</c> lists the frontend origins, because a sign-in POST ends in
    /// a redirect to the frontend's callback and Chrome checks every redirect of a form.
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
    /// The only places a form on this origin may end up. An invalid value is skipped; the settings
    /// validator reports it. Built by hand from scheme, punycode host and port: <c>GetLeftPart</c> keeps a
    /// <c>user@</c> part, and <c>Authority</c> gives a Unicode host, which a header may not carry.
    /// This reads configuration while OpenIddict checks the seeded client row, so it assumes the two agree;
    /// if they drift apart, sign-in fails visibly.
    /// </summary>
    internal static IReadOnlyList<string> FrontendOrigins(WebClientSettings webClient) =>
        webClient.RedirectUris
            .Concat(webClient.PostLogoutRedirectUris)
            .Select(value => AbsoluteHttpUri.TryParse(value, out Uri? uri) ? uri : null)
            .OfType<Uri>()
            .Select(Origin)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string Origin(Uri uri)
    {
        // IdnHost drops the brackets of an IPv6 address, and a CSP source needs them back.
        string host = uri.HostNameType is UriHostNameType.IPv6 ? $"[{uri.IdnHost}]" : uri.IdnHost;

        return uri.IsDefaultPort
            ? $"{uri.Scheme}://{host}"
            : $"{uri.Scheme}://{host}:{uri.Port.ToString(CultureInfo.InvariantCulture)}";
    }
}
