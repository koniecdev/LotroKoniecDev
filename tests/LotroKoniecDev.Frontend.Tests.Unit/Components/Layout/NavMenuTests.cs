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
        this.AddAuthorization().SetNotAuthorized();

        IRenderedComponent<NavMenu> component = RenderNavMenu();

        IElement loginLink = component.Find("a.btn-primary");
        loginLink.GetAttribute("href").ShouldBe(AuthenticationDependencyInjectionExtensions.LoginPath);
        component.FindAll("form").ShouldBeEmpty();
    }

    [Fact]
    public void Render_WhenAuthenticated_ShowsTheUserNameAndALogoutFormPostingToTheLogoutPath()
    {
        this.AddAuthorization().SetAuthorized("Frodo");

        IRenderedComponent<NavMenu> component = RenderNavMenu();

        component.Find("span.auth-user").TextContent.ShouldBe("Frodo");

        IElement logoutForm = component.Find("form");
        logoutForm.GetAttribute("method").ShouldBe("post");
        logoutForm.GetAttribute("action").ShouldBe(AuthenticationDependencyInjectionExtensions.LogoutPath);
        component.FindAll("a.btn-primary").ShouldBeEmpty();
    }

    [Fact]
    public void Render_Always_ShowsTheCoreNavigationLinksInOrder()
    {
        this.AddAuthorization().SetNotAuthorized();

        IRenderedComponent<NavMenu> component = RenderNavMenu();

        string[] navTargets = component
            .FindAll("nav.sidebar-nav a")
            .Select(anchor => anchor.GetAttribute("href")!)
            .ToArray();

        navTargets.ShouldBe(["/", "/translations", "/import-export", "/dashboard"]);
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
