using AngleSharp.Dom;
using LotroKoniecDev.Frontend.Components.Shared;
using LotroKoniecDev.Frontend.Infrastructure.CookieConsent;
using LotroKoniecDev.Frontend.Settings;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace LotroKoniecDev.Frontend.Tests.Unit.Components.Shared;

/// <summary>
/// The banner's SSR parts (LEGAL-04). The accept round trip is covered by the endpoint's tests and the
/// browser E2E.
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
        // Accepting must work with JavaScript off.
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
