using AngleSharp.Dom;
using LotroKoniecDev.Frontend.Components.Shared;
using LotroKoniecDev.Frontend.Infrastructure.CookieConsent;
using LotroKoniecDev.Frontend.Settings;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;
using NSubstitute;

namespace LotroKoniecDev.Frontend.Tests.Unit.Components.Shared;

/// <summary>
/// The notice and its cookie (#1339). Closing it in a real browser, with JavaScript on and off, is covered by
/// the browser E2E.
/// </summary>
public sealed class CookieConsentTests : BunitContext
{
    private readonly DefaultHttpContext _httpContext = new();

    public CookieConsentTests()
    {
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
    public void Render_WhenCookieAbsent_ShowsTheNoticeAsAnOpenDialog()
    {
        IRenderedComponent<CookieConsent> component = Render<CookieConsent>();

        IElement dialog = component.Find("dialog.cookie-bar");
        dialog.HasAttribute("open").ShouldBeTrue();
        dialog.GetAttribute("aria-label").ShouldBe("Informacja o plikach cookie");
        component.Markup.ShouldContain("niezbędnych plików cookie");
    }

    [Fact]
    public void Render_WhenCookieAbsent_SetsTheCookieInTheSameResponse()
    {
        Render<CookieConsent>();

        string setCookie = _httpContext.Response.Headers.SetCookie.ToString();
        setCookie.ShouldStartWith($"{CookieConsentCookie.Name}={CookieConsentCookie.Value};");
        setCookie.ShouldContain("httponly", Case.Insensitive);
        setCookie.ShouldContain("path=/", Case.Insensitive);
        setCookie.ShouldContain("samesite=lax", Case.Insensitive);
    }

    [Fact]
    public void Render_WhenCookieAbsent_KeepsTheCookieForAYear()
    {
        // The privacy policy says "ważne rok". A session cookie would show the notice again after a browser
        // restart.
        Render<CookieConsent>();

        SetCookieHeaderValue setCookie = SetCookieHeaderValue.Parse(_httpContext.Response.Headers.SetCookie.ToString());
        TimeSpan lifetime = setCookie.Expires.ShouldNotBeNull() - DateTimeOffset.UtcNow;
        lifetime.TotalDays.ShouldBeInRange(364, 367);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void Render_WhenCookieAbsent_MarksTheCookieSecureOnlyOverHttps(bool isHttps, bool expectedSecure)
    {
        _httpContext.Request.IsHttps = isHttps;

        Render<CookieConsent>();

        string setCookie = _httpContext.Response.Headers.SetCookie.ToString();
        setCookie.ShouldStartWith($"{CookieConsentCookie.Name}=");
        setCookie.Contains("secure", StringComparison.OrdinalIgnoreCase).ShouldBe(expectedSecure);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("anything")]
    public void Render_WhenCookiePresent_RendersNothing(string cookieValue)
    {
        _httpContext.Request.Headers.Cookie = $"{CookieConsentCookie.Name}={cookieValue}";

        IRenderedComponent<CookieConsent> component = Render<CookieConsent>();

        component.Markup.Trim().ShouldBeEmpty();
    }

    [Theory]
    [InlineData("true")]
    [InlineData("anything")]
    public void Render_WhenCookiePresent_SetsNoCookie(string cookieValue)
    {
        _httpContext.Request.Headers.Cookie = $"{CookieConsentCookie.Name}={cookieValue}";

        Render<CookieConsent>();

        _httpContext.Response.Headers.SetCookie.ShouldBeEmpty();
    }

    [Fact]
    public void Render_WhenResponseHasStarted_ShowsTheNoticeWithoutSettingTheCookie()
    {
        _httpContext.Features.Set<IHttpResponseFeature>(new StartedResponseFeature());

        IRenderedComponent<CookieConsent> component = Render<CookieConsent>();

        component.Find("dialog.cookie-bar").ShouldNotBeNull();
        _httpContext.Response.Headers.SetCookie.ShouldBeEmpty();
    }

    [Fact]
    public void Render_WhenHttpContextUnavailable_ShowsTheNotice()
    {
        IHttpContextAccessor accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns((HttpContext?)null);
        Services.AddSingleton(accessor);

        IRenderedComponent<CookieConsent> component = Render<CookieConsent>();

        component.Find("dialog.cookie-bar").ShouldNotBeNull();
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
    public void Render_CloseForm_ClosesTheDialogInTheBrowserWithNoRequest()
    {
        // Closing must keep what the visitor typed or ticked on the page, with JavaScript off too.
        IRenderedComponent<CookieConsent> component = Render<CookieConsent>();

        IElement form = component.Find("dialog.cookie-bar form.cookie-bar-form");
        form.GetAttribute("method").ShouldBe("dialog");
        form.HasAttribute("action").ShouldBeFalse();
        form.QuerySelectorAll("input").ShouldBeEmpty();
        form.QuerySelector("button[type=submit]").ShouldNotBeNull().TextContent.Trim().ShouldBe("Rozumiem");
        component.Markup.ShouldNotContain("blazor:on");
    }

    private sealed class StartedResponseFeature : IHttpResponseFeature
    {
        public int StatusCode { get; set; } = StatusCodes.Status200OK;
        public string? ReasonPhrase { get; set; }
        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();
        public Stream Body { get; set; } = Stream.Null;
        public bool HasStarted => true;

        public void OnStarting(Func<object, Task> callback, object state)
        {
        }

        public void OnCompleted(Func<object, Task> callback, object state)
        {
        }
    }
}
