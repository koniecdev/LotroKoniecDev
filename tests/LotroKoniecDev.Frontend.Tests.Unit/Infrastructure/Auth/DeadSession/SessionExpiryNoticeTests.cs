using LotroKoniecDev.Frontend.Infrastructure.Auth.DeadSession;
using Microsoft.AspNetCore.Http;
using NSubstitute;

namespace LotroKoniecDev.Frontend.Tests.Unit.Infrastructure.Auth.DeadSession;

public sealed class SessionExpiryNoticeTests
{
    [Fact]
    public void Raise_WhenResponseNotStarted_WritesTheMarkerCookie()
    {
        DefaultHttpContext httpContext = new();
        SessionExpiryNotice notice = CreateNotice(httpContext);

        notice.Raise();

        httpContext.Response.Headers.SetCookie
            .ToString()
            .ShouldContain(SessionExpiryNotice.CookieName);
    }

    [Fact]
    public void Consume_WhenMarkerCookiePresent_ReturnsTrue()
    {
        DefaultHttpContext httpContext = new();
        httpContext.Request.Headers.Cookie = $"{SessionExpiryNotice.CookieName}=1";
        SessionExpiryNotice notice = CreateNotice(httpContext);

        bool raised = notice.Consume();

        raised.ShouldBeTrue();
    }

    [Fact]
    public void Consume_WhenMarkerCookieAbsent_ReturnsFalse()
    {
        DefaultHttpContext httpContext = new();
        SessionExpiryNotice notice = CreateNotice(httpContext);

        bool raised = notice.Consume();

        raised.ShouldBeFalse();
    }

    [Fact]
    public void Consume_WhenMarkerCookieHasUnexpectedValue_ReturnsFalse()
    {
        DefaultHttpContext httpContext = new();
        httpContext.Request.Headers.Cookie = $"{SessionExpiryNotice.CookieName}=tampered";
        SessionExpiryNotice notice = CreateNotice(httpContext);

        bool raised = notice.Consume();

        raised.ShouldBeFalse();
    }

    [Fact]
    public void Consume_WhenMarkerPresent_EmitsCookieDeletionSoTheNoticeIsOneShot()
    {
        DefaultHttpContext httpContext = new();
        httpContext.Request.Headers.Cookie = $"{SessionExpiryNotice.CookieName}=1";
        SessionExpiryNotice notice = CreateNotice(httpContext);

        notice.Consume();

        // A delete, rather than another write, appears as a Set-Cookie for the same name with an expiry
        // in 1970. Checking for that tells a real delete apart from any other Set-Cookie.
        string setCookie = httpContext.Response.Headers.SetCookie.ToString();
        setCookie.ShouldContain(SessionExpiryNotice.CookieName);
        setCookie.ShouldContain("expires=Thu, 01 Jan 1970", Case.Insensitive);
    }

    [Fact]
    public void Raise_WhenRequestIsHttps_MarksTheCookieSecure()
    {
        DefaultHttpContext httpContext = new();
        httpContext.Request.IsHttps = true;
        SessionExpiryNotice notice = CreateNotice(httpContext);

        notice.Raise();

        httpContext.Response.Headers.SetCookie
            .ToString()
            .ShouldContain("secure", Case.Insensitive);
    }

    [Fact]
    public void Raise_WhenRequestIsPlainHttp_LeavesTheCookieNonSecure()
    {
        DefaultHttpContext httpContext = new();
        SessionExpiryNotice notice = CreateNotice(httpContext);

        notice.Raise();

        string setCookie = httpContext.Response.Headers.SetCookie.ToString();
        setCookie.ShouldContain(SessionExpiryNotice.CookieName);
        setCookie.ShouldNotContain("secure", Case.Insensitive);
    }

    [Fact]
    public void Consume_WhenRequestIsHttps_EmitsSecureCookieDeletion()
    {
        DefaultHttpContext httpContext = new();
        httpContext.Request.IsHttps = true;
        httpContext.Request.Headers.Cookie = $"{SessionExpiryNotice.CookieName}=1";
        SessionExpiryNotice notice = CreateNotice(httpContext);

        notice.Consume();

        httpContext.Response.Headers.SetCookie
            .ToString()
            .ShouldContain("secure", Case.Insensitive);
    }

    [Fact]
    public void Raise_WhenHttpContextIsNull_DoesNotThrow()
    {
        IHttpContextAccessor accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns((HttpContext?)null);
        SessionExpiryNotice notice = new(accessor);

        Should.NotThrow(() => notice.Raise());
    }

    [Fact]
    public void Consume_WhenHttpContextIsNull_ReturnsFalse()
    {
        IHttpContextAccessor accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns((HttpContext?)null);
        SessionExpiryNotice notice = new(accessor);

        notice.Consume().ShouldBeFalse();
    }

    private static SessionExpiryNotice CreateNotice(HttpContext httpContext)
    {
        IHttpContextAccessor accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(httpContext);
        return new SessionExpiryNotice(accessor);
    }
}
