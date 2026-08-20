namespace LotroKoniecDev.Frontend.Infrastructure.Auth.DeadSession;

internal sealed class SessionExpiryNotice : ISessionExpiryNotice
{
    internal const string CookieName = ".lotrokoniecdev.session-expired";
    private const string CookieValue = "1";

    // Very short on purpose. The cookie only has to survive the redirect after a forced sign-out until
    // the next render reads it. If the delete that clears it cannot be written, because the SSR response
    // has already started, the short max-age makes the banner disappear within seconds instead of
    // following the user from page to page.
    private static readonly TimeSpan CookieLifetime = TimeSpan.FromSeconds(30);

    private readonly IHttpContextAccessor _httpContextAccessor;

    public SessionExpiryNotice(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public void Raise()
    {
        HttpContext? httpContext = _httpContextAccessor.HttpContext;
        if (httpContext is null || httpContext.Response.HasStarted)
        {
            return;
        }

        httpContext.Response.Cookies.Append(CookieName, CookieValue, BuildOptions(httpContext.Request.IsHttps));
    }

    public bool Consume()
    {
        HttpContext? httpContext = _httpContextAccessor.HttpContext;
        if (httpContext is null)
        {
            return false;
        }

        if (!httpContext.Request.Cookies.TryGetValue(CookieName, out string? value)
            || value != CookieValue)
        {
            return false;
        }

        if (!httpContext.Response.HasStarted)
        {
            httpContext.Response.Cookies.Delete(CookieName, BuildDeleteOptions(httpContext.Request.IsHttps));
        }

        return true;
    }

    // Path "/" like the auth and antiforgery cookies, so the read after the redirect finds it on any
    // path. HttpOnly because no script reads it. Secure follows the request scheme: it is set over HTTPS,
    // in the dev https profile and in production behind the proxy through the forwarded headers, and left
    // out on the plain-http dev profile so the cookie still works there. It carries nothing sensitive,
    // only a one-time "show the banner" flag.
    private static CookieOptions BuildOptions(bool isHttps) => new()
    {
        Path = "/",
        HttpOnly = true,
        Secure = isHttps,
        SameSite = SameSiteMode.Lax,
        MaxAge = CookieLifetime,
        IsEssential = true
    };

    private static CookieOptions BuildDeleteOptions(bool isHttps) => new()
    {
        Path = "/",
        Secure = isHttps,
        SameSite = SameSiteMode.Lax
    };
}
