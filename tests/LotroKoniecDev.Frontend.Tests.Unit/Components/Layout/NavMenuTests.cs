using AngleSharp.Dom;
using Bunit.Rendering;
using Bunit.TestDoubles;
using LotroKoniecDev.Frontend.Components.Layout;
using LotroKoniecDev.Frontend.Components.Shared;
using LotroKoniecDev.Frontend.Infrastructure.Auth;
using Microsoft.Extensions.DependencyInjection;

namespace LotroKoniecDev.Frontend.Tests.Unit.Components.Layout;

/// <summary>
/// Pins both sign-in states. The link targets are the auth paths' constants, so a rename shows up here
/// instead of as a dead button.
/// </summary>
public sealed class NavMenuTests : BunitContext
{
    public NavMenuTests()
    {
        // The logout form renders an <AntiforgeryToken/>.
        Services.AddAntiforgery();
    }

    [Fact]
    public void Render_WhenAnonymous_ShowsTheLoginLinkAndNoLogoutForm()
    {
        AddAuthorization().SetNotAuthorized();

        IRenderedComponent<NavMenu> component = RenderNavMenu();

        IElement loginLink = component.Find($"a[href='{AuthenticationDependencyInjectionExtensions.LoginPath}']");
        loginLink.ClassList.ShouldContain("nav-link");
        component.FindAll("form").ShouldBeEmpty();
    }

    [Fact]
    public void Render_WhenAuthenticated_ShowsTheUserNameAndALogoutFormPostingToTheLogoutPath()
    {
        AddAuthorization().SetAuthorized("Frodo");

        IRenderedComponent<NavMenu> component = RenderNavMenu();

        component.Find("span.nav-user").TextContent.ShouldBe("Frodo");

        IElement logoutForm = component.Find("form");
        logoutForm.GetAttribute("method").ShouldBe("post");
        logoutForm.GetAttribute("action").ShouldBe(AuthenticationDependencyInjectionExtensions.LogoutPath);
        component.FindAll($"a[href='{AuthenticationDependencyInjectionExtensions.LoginPath}']").ShouldBeEmpty();
    }

    [Fact]
    public void Render_WhenAuthenticated_ShowsTheMojeKontoLink()
    {
        // The privacy policy promises the self-service section under exactly this name (LEGAL-02).
        AddAuthorization().SetAuthorized("Frodo");

        IRenderedComponent<NavMenu> component = RenderNavMenu();

        IElement accountLink = component.Find("[data-testid=nav-account]");
        accountLink.GetAttribute("href").ShouldBe("/account");
        accountLink.TextContent.Trim().ShouldBe("Moje konto");
    }

    [Fact]
    public void Render_WhenAnonymous_DoesNotShowTheMojeKontoLink()
    {
        AddAuthorization().SetNotAuthorized();

        IRenderedComponent<NavMenu> component = RenderNavMenu();

        component.FindAll("[data-testid=nav-account]").ShouldBeEmpty();
    }

    [Fact]
    public void Render_WhenAnonymous_ShowsOnlyThePublicNavigationLinksInOrder()
    {
        AddAuthorization().SetNotAuthorized();

        IRenderedComponent<NavMenu> component = RenderNavMenu();

        string[] navTargets = component
            .FindAll("nav.nav-links a")
            .Select(anchor => anchor.GetAttribute("href")!)
            .ToArray();

        // Pages that need a login stay hidden from guests (#523); the login and sign-up links come last.
        navTargets.ShouldBe(
        [
            "/", "/translations",
            AuthenticationDependencyInjectionExtensions.LoginPath,
            AuthenticationDependencyInjectionExtensions.RegisterPath
        ]);
    }

    [Fact]
    public void Render_WhenAnonymous_ShowsTheSignUpAsTheAccentButton()
    {
        AddAuthorization().SetNotAuthorized();

        IRenderedComponent<NavMenu> component = RenderNavMenu();

        IElement signUpLink = component.Find($"a[href='{AuthenticationDependencyInjectionExtensions.RegisterPath}']");
        signUpLink.TextContent.ShouldBe("Zarejestruj");
        signUpLink.ClassList.ShouldContain("nav-cta");
    }

    [Fact]
    public void Render_WhenAuthenticated_ShowsNoSignUpButton()
    {
        AddAuthorization().SetAuthorized("Frodo");

        IRenderedComponent<NavMenu> component = RenderNavMenu();

        component.FindAll($"a[href='{AuthenticationDependencyInjectionExtensions.RegisterPath}']").ShouldBeEmpty();
        component.FindAll(".nav-cta").ShouldBeEmpty();
    }

    [Fact]
    public void Render_WhenAuthenticated_ShowsTheAuthGatedNavigationLinksInOrder()
    {
        AddAuthorization().SetAuthorized("Frodo");

        IRenderedComponent<NavMenu> component = RenderNavMenu();

        string[] navTargets = component
            .FindAll("nav.nav-links a")
            .Select(anchor => anchor.GetAttribute("href")!)
            .ToArray();

        navTargets.ShouldBe(
        [
            "/", "/translations", "/import-export", "/game-versions", "/dashboard", "/account"
        ]);
    }

    [Fact]
    public void Render_Brand_ShowsTheLogoImageInsteadOfTheCircle()
    {
        // The site name stands next to the logo, so the image has an empty alt (#1371).
        AddAuthorization().SetNotAuthorized();

        IRenderedComponent<NavMenu> component = RenderNavMenu();

        IElement logo = component.Find("a.brand img.brand-mark");
        logo.GetAttribute("src").ShouldBe("logo.png");
        logo.GetAttribute("alt").ShouldBe(string.Empty);
        component.FindAll("a.brand svg").ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Render_DiscordPill_SitsRightAfterTheLogoAndOpensTheInviteInANewTab(bool signedIn)
    {
        // Outside nav.nav-links, so on a phone it stays in the logo's row (#1590).
        if (signedIn)
        {
            AddAuthorization().SetAuthorized("Frodo");
        }
        else
        {
            AddAuthorization().SetNotAuthorized();
        }

        IRenderedComponent<NavMenu> component = RenderNavMenu();

        IElement pill = component.Find("a.brand").NextElementSibling.ShouldNotBeNull();
        pill.ClassList.ShouldContain("nav-discord");
        pill.GetAttribute("href").ShouldBe(ExternalLinks.Discord);
        pill.GetAttribute("target").ShouldBe("_blank");
        pill.GetAttribute("rel").ShouldBe("noopener");
        pill.TextContent.Trim().ShouldBe("Discord");
        pill.QuerySelector("svg").ShouldNotBeNull().GetAttribute("aria-hidden").ShouldBe("true");
    }

    /// <summary>
    /// Through a render fragment, because the typed <c>Render&lt;NavMenu&gt;</c> does not see the
    /// asynchronous <c>AuthorizeView</c> on its first pass.
    /// </summary>
    private IRenderedComponent<NavMenu> RenderNavMenu()
    {
        IRenderedComponent<ContainerFragment> fragment = Render(builder =>
        {
            builder.OpenComponent<NavMenu>(0);
            builder.CloseComponent();
        });

        return fragment.FindComponent<NavMenu>();
    }
}
