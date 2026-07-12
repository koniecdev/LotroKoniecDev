using LotroKoniecDev.Frontend.Infrastructure.CookieConsent;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Primitives;

namespace LotroKoniecDev.Frontend.Tests.Unit.Infrastructure.CookieConsent;

/// <summary>
/// Drives the accept route's request delegate directly (no web host): acceptance must persist the
/// year-long consent cookie and bounce the visitor back to the page the plain-HTML form was posted
/// from — with the open-redirect guard collapsing any non-local target to the home page (LEGAL-04).
/// </summary>
public sealed class CookieConsentEndpointsExtensionsTests
{
    [Fact]
    public void AcceptCookieConsent_AppendsTheConsentCookie()
    {
        DefaultHttpContext httpContext = new();

        CookieConsentEndpointsExtensions.AcceptCookieConsent(httpContext, CreateForm("/translations"));

        string setCookie = httpContext.Response.Headers.SetCookie.ToString();
        setCookie.ShouldContain(CookieConsentCookie.Name);
        setCookie.ShouldContain("httponly", Case.Insensitive);
        setCookie.ShouldContain("path=/", Case.Insensitive);
        setCookie.ShouldContain("samesite=lax", Case.Insensitive);
        // A persistent expiry is the "survives navigation" contract — a session cookie would
        // re-show the banner on the next browser start.
        setCookie.ShouldContain("expires=", Case.Insensitive);
    }

    [Fact]
    public void AcceptCookieConsent_WhenReturnPathIsLocal_RedirectsBackToIt()
    {
        DefaultHttpContext httpContext = new();

        IResult result = CookieConsentEndpointsExtensions.AcceptCookieConsent(
            httpContext, CreateForm("/translations?page=2"));

        RedirectHttpResult redirect = result.ShouldBeOfType<RedirectHttpResult>();
        redirect.Url.ShouldBe("/translations?page=2");
    }

    [Theory]
    [InlineData("https://evil.example")]
    [InlineData("//evil.example")]
    [InlineData("/\\evil.example")]
    [InlineData("/\t/evil.example")]
    [InlineData("/\r\n/evil.example")]
    [InlineData("evil")]
    [InlineData("")]
    public void AcceptCookieConsent_WhenReturnPathIsNotLocal_RedirectsHome(string returnPath)
    {
        DefaultHttpContext httpContext = new();

        IResult result = CookieConsentEndpointsExtensions.AcceptCookieConsent(
            httpContext, CreateForm(returnPath));

        RedirectHttpResult redirect = result.ShouldBeOfType<RedirectHttpResult>();
        redirect.Url.ShouldBe("/");
    }

    [Fact]
    public void AcceptCookieConsent_WhenReturnPathIsMissing_RedirectsHome()
    {
        DefaultHttpContext httpContext = new();
        FormCollection emptyForm = new(new Dictionary<string, StringValues>());

        IResult result = CookieConsentEndpointsExtensions.AcceptCookieConsent(httpContext, emptyForm);

        RedirectHttpResult redirect = result.ShouldBeOfType<RedirectHttpResult>();
        redirect.Url.ShouldBe("/");
    }

    [Fact]
    public void AcceptCookieConsent_WhenRequestIsHttps_MarksTheCookieSecure()
    {
        DefaultHttpContext httpContext = new();
        httpContext.Request.IsHttps = true;

        CookieConsentEndpointsExtensions.AcceptCookieConsent(httpContext, CreateForm("/"));

        httpContext.Response.Headers.SetCookie
            .ToString()
            .ShouldContain("secure", Case.Insensitive);
    }

    [Fact]
    public void AcceptCookieConsent_WhenRequestIsPlainHttp_LeavesTheCookieNonSecure()
    {
        DefaultHttpContext httpContext = new();

        CookieConsentEndpointsExtensions.AcceptCookieConsent(httpContext, CreateForm("/"));

        string setCookie = httpContext.Response.Headers.SetCookie.ToString();
        setCookie.ShouldContain(CookieConsentCookie.Name);
        setCookie.ShouldNotContain("secure", Case.Insensitive);
    }

    private static FormCollection CreateForm(string returnPath) =>
        new(new Dictionary<string, StringValues> { ["returnPath"] = returnPath });
}
