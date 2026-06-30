using LotroKoniecDev.Frontend.Settings;
using Microsoft.Extensions.Options;

namespace LotroKoniecDev.Frontend.Infrastructure.Security;

/// <summary>
/// Stamps the browser-facing security response headers on every response (audit #0001 / M6): a
/// Content-Security-Policy locked to <c>'self'</c> plus the auth origin and the Google Fonts hosts the
/// layout loads, <c>X-Content-Type-Options: nosniff</c>, <c>Referrer-Policy: no-referrer</c>, and
/// <c>X-Frame-Options: DENY</c> alongside the CSP <c>frame-ancestors 'none'</c>. The header set is
/// computed once from configuration and written via <see cref="HttpResponse.OnStarting"/> so it also
/// reaches re-executed error/status-code responses. Only registered outside Development, mirroring
/// <c>UseHsts()</c>, so the host-run dev loop (and its hot-reload inline script) stays unchanged.
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
    /// Builds the CSP. <c>script-src</c> stays at <c>'self'</c> (Blazor's <c>blazor.web.js</c> and
    /// streaming updates are external/markup, never inline script); <c>style-src</c> needs
    /// <c>'unsafe-inline'</c> for the dynamic inline <c>style</c> width on the dashboard progress bar
    /// and the Google Fonts stylesheet; the auth origin is admitted for <c>connect-src</c>/
    /// <c>form-action</c> so the OIDC login flow is not blocked.
    /// </summary>
    internal static string BuildContentSecurityPolicy(string authOrigin) => string.Join("; ",
    [
        "default-src 'self'",
        "base-uri 'self'",
        "object-src 'none'",
        "frame-ancestors 'none'",
        "img-src 'self' data:",
        "script-src 'self'",
        "style-src 'self' 'unsafe-inline' https://fonts.googleapis.com",
        "font-src 'self' https://fonts.gstatic.com",
        $"connect-src 'self' {authOrigin}",
        $"form-action 'self' {authOrigin}"
    ]);

    /// <summary>
    /// Reduces a configured authority URL to its bare origin (scheme + host + non-default port), which
    /// is the form a CSP source expression accepts — no path, no trailing slash.
    /// </summary>
    internal static string AuthOrigin(string authority) =>
        new Uri(authority).GetLeftPart(UriPartial.Authority);
}
