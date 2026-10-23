using LotroKoniecDev.Frontend.Settings;
using Microsoft.Extensions.Options;

namespace LotroKoniecDev.Frontend.Infrastructure.Security;

/// <summary>
/// Written through <see cref="HttpResponse.OnStarting"/>, so the headers also reach error and status-code
/// pages that run a second time. Registered only outside Development, so the local hot-reload script keeps
/// working.
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
    /// No page needs an inline script, so <c>script-src</c> stays at <c>'self'</c>. A new inline script
    /// needs a nonce here, never <c>'unsafe-inline'</c> (#670). <c>style-src</c> needs
    /// <c>'unsafe-inline'</c> for the inline <c>style</c> attributes, such as the progress-bar widths. The
    /// fonts are self-hosted (LEGAL-06), so <c>font-src</c> stays at <c>'self'</c>. The auth origin is in
    /// <c>connect-src</c> and <c>form-action</c> for the OIDC login flow.
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

    /// <summary>A CSP source accepts an origin only: no path and no trailing slash.</summary>
    internal static string AuthOrigin(string authority) =>
        new Uri(authority).GetLeftPart(UriPartial.Authority);
}
