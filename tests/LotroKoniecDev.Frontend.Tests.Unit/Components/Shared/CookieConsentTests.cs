using AngleSharp.Dom;
using LotroKoniecDev.Frontend.Components.Shared;
using LotroKoniecDev.Frontend.Infrastructure.CookieConsent;
using LotroKoniecDev.Frontend.Settings;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace LotroKoniecDev.Frontend.Tests.Unit.Components.Shared;

/// <summary>
/// Renders the cookie banner (LEGAL-04). Whether it is shown is decided once on the server by reading
/// the request cookie, with no script and no interactivity, so these tests pin the SSR parts: the plain
/// HTML accept form with its action and hidden returnPath, the privacy-policy link with the
/// <c>#cookies</c> anchor, and the banner disappearing once the request carries the consent cookie.
/// The whole accept round trip is covered by the endpoint's unit tests and by the browser E2E tests.
/// </summary>
public sealed class CookieConsentTests : BunitContext
{
    private readonly DefaultHttpContext _httpContext = new();

    public CookieConsentTests()
    {
        Services.AddAntiforgery();
        IHttpContextAccessor accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(_httpContext);
        Services.AddSingleton(accessor);
        Services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new AuthSystemSettings
        {
            BaseUrl = "https://localhost:5003/",
            Authority = "https://localhost:5003",
            ClientId = "lotrokoniecdev-web",
            CallbackPath = "/callback",
            SignedOutCallbackPath = "/signout-callback-oidc",
            Scopes = ["openid", "profile", "api"]
        }));
    }

    [Fact]
    public void Render_WhenConsentCookieAbsent_ShowsTheBanner()
    {
        IRenderedComponent<CookieConsent> component = Render<CookieConsent>();

        component.Find(".cookie-bar").ShouldNotBeNull();
        component.Markup.ShouldContain("niezbędnych plików cookie");
    }

    [Fact]
    public void Render_WhenConsentCookiePresent_RendersNothing()
    {
        _httpContext.Request.Headers.Cookie = $"{CookieConsentCookie.Name}=true";

        IRenderedComponent<CookieConsent> component = Render<CookieConsent>();

        component.Markup.Trim().ShouldBeEmpty();
    }

    [Fact]
    public void Render_AcceptForm_PostsToTheAcceptEndpointWithTheCurrentPathAndQuery()
    {
        _httpContext.Request.Path = "/translations";
        _httpContext.Request.QueryString = new QueryString("?page=2");

        IRenderedComponent<CookieConsent> component = Render<CookieConsent>();

        IElement form = component.Find("form.cookie-bar-form");
        form.GetAttribute("method").ShouldBe("post");
        form.GetAttribute("action").ShouldBe(CookieConsentEndpointsExtensions.AcceptPath);
        component.Find("input[name=returnPath]").GetAttribute("value").ShouldBe("/translations?page=2");
    }

    [Fact]
    public void Render_PolicyLink_TargetsTheAuthServerPolicyCookieSection()
    {
        IRenderedComponent<CookieConsent> component = Render<CookieConsent>();

        component.Find(".cookie-bar-text a")
            .GetAttribute("href")
            .ShouldBe("https://localhost:5003/Account/PrivacyPolicy#cookies");
    }

    [Fact]
    public void Render_Banner_ContainsNoInteractiveHandlers()
    {
        // The SSR rule: accepting has to work with JavaScript turned off, so the button is a plain form
        // submit and never an @on* handler.
        IRenderedComponent<CookieConsent> component = Render<CookieConsent>();

        component.Find("button[type=submit]").TextContent.Trim().ShouldBe("Akceptuję");
        component.Markup.ShouldNotContain("blazor:onclick");
        component.Markup.ShouldNotContain("blazor:onsubmit");
    }

    [Fact]
    public void Render_WhenHttpContextUnavailable_FallsBackToHomeReturnPath()
    {
        IHttpContextAccessor accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns((HttpContext?)null);
        Services.AddSingleton(accessor);

        IRenderedComponent<CookieConsent> component = Render<CookieConsent>();

        component.Find("input[name=returnPath]").GetAttribute("value").ShouldBe("/");
    }
}
