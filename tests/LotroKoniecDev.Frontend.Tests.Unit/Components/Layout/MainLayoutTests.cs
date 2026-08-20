using AngleSharp.Dom;
using LotroKoniecDev.Frontend.Components.Layout;
using LotroKoniecDev.Frontend.Infrastructure.Auth.DeadSession;
using LotroKoniecDev.Frontend.Settings;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace LotroKoniecDev.Frontend.Tests.Unit.Components.Layout;

/// <summary>
/// Renders <see cref="MainLayout"/> through bUnit to pin the legal links in the footer (LEGAL-05).
/// Every page must offer the terms of service and the privacy policy the auth server hosts, which is
/// the one place the cookie banner and the terms page already point at. If this breaks, the whole app
/// quietly stops meeting the Art. 13 requirement that those pages are reachable.
/// </summary>
public sealed class MainLayoutTests : BunitContext
{
    private readonly ISessionExpiryNotice _sessionExpiryNotice = Substitute.For<ISessionExpiryNotice>();

    public MainLayoutTests()
    {
        Services.AddAntiforgery();
        Services.AddSingleton(_sessionExpiryNotice);

        IHttpContextAccessor accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(new DefaultHttpContext());
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

        AddAuthorization().SetNotAuthorized();
    }

    [Fact]
    public void Render_Footer_LinksTheTermsOfService()
    {
        IRenderedComponent<MainLayout> component = RenderMainLayout();

        IElement termsLink = component.Find("footer .foot-links a[href='/regulamin']");
        termsLink.TextContent.ShouldBe("Regulamin");
    }

    [Fact]
    public void Render_Footer_LinksTheAuthServerPrivacyPolicyFromSettings()
    {
        IRenderedComponent<MainLayout> component = RenderMainLayout();

        IElement policyLink = component.Find("footer .foot-links a[target=_blank]");
        policyLink.GetAttribute("href").ShouldBe("https://localhost:5003/Account/PrivacyPolicy");
        policyLink.GetAttribute("rel").ShouldBe("noopener");
        policyLink.TextContent.ShouldBe("Polityka prywatności");
    }

    [Fact]
    public void Render_Footer_ShowsTheNonAffiliationTrademarkLine()
    {
        IRenderedComponent<MainLayout> component = RenderMainLayout();

        IElement legalLine = component.Find("footer .foot-legal");
        legalLine.TextContent.ShouldBe(
            "Nieoficjalny, niekomercyjny projekt fanowski — niepowiązany ze Standing Stone Games " +
            "ani Middle-earth Enterprises. The Lord of the Rings Online™ oraz nazwy postaci, " +
            "przedmiotów, wydarzeń i miejsc są znakami towarowymi Middle-earth Enterprises, LLC.");
    }

    [Fact]
    public void Render_Body_IsRenderedInsideMain()
    {
        IRenderedComponent<MainLayout> component = RenderMainLayout();

        component.Find("main").TextContent.ShouldContain("page-body");
    }

    private IRenderedComponent<MainLayout> RenderMainLayout() =>
        Render<MainLayout>(parameters => parameters
            .Add(layout => layout.Body, builder => builder.AddContent(0, "page-body")));
}
