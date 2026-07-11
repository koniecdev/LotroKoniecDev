using AngleSharp.Dom;
using Bunit.Rendering;
using Bunit.TestDoubles;
using LotroKoniecDev.Frontend.Components.Layout;
using LotroKoniecDev.Frontend.Infrastructure.Auth;
using Microsoft.Extensions.DependencyInjection;

namespace LotroKoniecDev.Frontend.Tests.Unit.Components.Layout;

/// <summary>
/// Renders <see cref="NavMenu"/> through bUnit to lock the two authentication states down at the markup
/// level: an anonymous visitor is offered the login link, an authenticated translator is shown their
/// name plus a logout form. The link targets are the auth module's canonical paths, so a future rename
/// of those constants surfaces here instead of as a dead button in the browser.
/// </summary>
public sealed class NavMenuTests : BunitContext
{
    public NavMenuTests()
    {
        // The authenticated branch renders an <AntiforgeryToken/> inside the logout form.
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
    public void Render_Always_ShowsTheCoreNavigationLinksInOrder()
    {
        AddAuthorization().SetNotAuthorized();

        IRenderedComponent<NavMenu> component = RenderNavMenu();

        string[] navTargets = component
            .FindAll("nav.nav-links a")
            .Select(anchor => anchor.GetAttribute("href")!)
            .ToArray();

        // The topbar hosts the auth affordance in the same nav; anonymous renders the login link last.
        navTargets.ShouldBe(
        [
            "/", "/translations", "/import-export", "/game-versions", "/dashboard",
            AuthenticationDependencyInjectionExtensions.LoginPath
        ]);
    }

    /// <summary>
    /// Hosts <see cref="NavMenu"/> inside a render fragment and resolves it via
    /// <c>FindComponent</c>. The component's <see cref="Microsoft.AspNetCore.Components.Authorization.AuthorizeView"/>
    /// resolves asynchronously, which the typed <c>Render&lt;NavMenu&gt;</c> discovery does not see on its
    /// first synchronous pass for a parameterless component; the fragment seam renders it reliably.
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
