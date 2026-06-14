namespace LotroKoniecDev.Frontend.Infrastructure.Auth.DeadSession;

internal sealed class SessionExpiryNotice : ISessionExpiryNotice
{
    internal const string CookieName = ".lotrokoniecdev.session-expired";
    private const string CookieValue = "1";

    // Deliberately tiny: the cookie only needs to survive the redirect that follows the forced
    // sign-out until the next render reads it. If the consuming Delete cannot be written (the SSR
    // response already started streaming), the short max-age guarantees the banner self-clears
    // within seconds instead of lingering across navigation.
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

        httpContext.Response.Cookies.Append(CookieName, CookieValue, BuildOptions());
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
            httpContext.Response.Cookies.Delete(CookieName, BuildDeleteOptions());
        }

        return true;
    }

    // Path "/" mirrors the auth + antiforgery cookies so the post-redirect read on any path finds it.
    // HttpOnly because no client JS reads it. Secure is intentionally left at its default so the marker
    // round-trips over both http and https dev origins — it carries no sensitive value, only a one-shot
    // "show the banner" flag.
    private static CookieOptions BuildOptions() => new()
    {
        Path = "/",
        HttpOnly = true,
        SameSite = SameSiteMode.Lax,
        MaxAge = CookieLifetime,
        IsEssential = true
    };

    private static CookieOptions BuildDeleteOptions() => new()
    {
        Path = "/",
        SameSite = SameSiteMode.Lax
    };
}
