using LotroKoniecDev.Frontend.Settings;
using Microsoft.Extensions.Options;

namespace LotroKoniecDev.Frontend.Infrastructure.Security;

/// <summary>
/// Adds the security headers the browser needs to every response (audit #0001, M6): a
/// Content-Security-Policy limited to <c>'self'</c> plus the auth origin,
/// <c>X-Content-Type-Options: nosniff</c>, <c>Referrer-Policy: no-referrer</c> and
/// <c>X-Frame-Options: DENY</c> next to the CSP's <c>frame-ancestors 'none'</c>.
/// The headers are built once from configuration and written through
/// <see cref="HttpResponse.OnStarting"/>, so they also reach error and status-code pages that run a
/// second time. It is only registered outside Development, like <c>UseHsts()</c>, so the local dev loop
/// and its hot-reload script keep working.
/// </summary>
internal sealed class SecurityHeadersMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IReadOnlyDictionary<string, string> _headers;

    public SecurityHeadersMiddleware(RequestDelegate next, IOptions<AuthSystemSettings> authSettings)
    {
        _next = next;
        _headers = BuildHeaders(authSettings.Value.Authority);
    }

    public async Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(() =>
        {
            IHeaderDictionary responseHeaders = context.Response.Headers;
            foreach (KeyValuePair<string, string> header in _headers)
            {
                responseHeaders[header.Key] = header.Value;
            }

            return Task.CompletedTask;
        });

        await _next(context);
    }

    internal static IReadOnlyDictionary<string, string> BuildHeaders(string authority) =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Content-Security-Policy"] = BuildContentSecurityPolicy(AuthOrigin(authority)),
            ["X-Content-Type-Options"] = "nosniff",
            ["Referrer-Policy"] = "no-referrer",
            ["X-Frame-Options"] = "DENY"
        };

    /// <summary>
    /// Builds the CSP. <c>script-src</c> stays at <c>'self'</c>: <c>blazor.web.js</c> is a normal file
    /// and streaming updates arrive as markup, so no page needs an inline script. A new inline script
    /// needs a nonce here, never <c>'unsafe-inline'</c> (#670).
    /// <c>style-src</c> needs <c>'unsafe-inline'</c> for the inline <c>style</c> width on the dashboard
    /// progress bar. The fonts are hosted by us (LEGAL-06), so <c>font-src</c> stays at <c>'self'</c>.
    /// The auth origin is allowed in <c>connect-src</c> and <c>form-action</c>, so the OIDC login flow is
    /// not blocked.
    /// </summary>
    internal static string BuildContentSecurityPolicy(string authOrigin) => string.Join("; ",
    [
        "default-src 'self'",
        "base-uri 'self'",
        "object-src 'none'",
        "frame-ancestors 'none'",
        "img-src 'self' data:",
        "script-src 'self'",
        "style-src 'self' 'unsafe-inline'",
        "font-src 'self'",
        $"connect-src 'self' {authOrigin}",
        $"form-action 'self' {authOrigin}"
    ]);

    /// <summary>
    /// Cuts a configured authority URL down to its origin, the scheme, host and non-default port. That is
    /// the form a CSP source accepts: no path and no trailing slash.
    /// </summary>
    internal static string AuthOrigin(string authority) =>
        new Uri(authority).GetLeftPart(UriPartial.Authority);
}
