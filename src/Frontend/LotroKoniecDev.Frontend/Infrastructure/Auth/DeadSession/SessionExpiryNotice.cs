namespace LotroKoniecDev.Frontend.Infrastructure.Auth.DeadSession;

internal sealed class SessionExpiryNotice : ISessionExpiryNotice
{
    internal const string CookieName = ".lotrokoniecdev.session-expired";
    private const string CookieValue = "1";

    // It only has to survive the redirect after a forced sign-out. If the delete cannot be written because
    // the SSR response has started, the banner still disappears within seconds.
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

    // Secure follows the request scheme, so the cookie still works on the plain-http dev profile. It
    // carries nothing sensitive.
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
