using System.Reflection;
using AngleSharp.Dom;
using Bunit.Rendering;
using Bunit.TestDoubles;
using LotroKoniecDev.Frontend.Components.Pages.Home;
using LotroKoniecDev.Frontend.Components.Pages.ImportExport;
using LotroKoniecDev.Frontend.Infrastructure.Auth;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients.TranslationSystemHttpClients;
using LotroKoniecDev.TranslationSystem.Contracts.Progress;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using HomeComponent = LotroKoniecDev.Frontend.Components.Pages.Home.Home;
using LotroKoniecDev.Frontend.Tests.Unit.Infrastructure.Discovery;
using LotroKoniecDev.TranslationSystem.Contracts.Hateoas;

namespace LotroKoniecDev.Frontend.Tests.Unit.Components.Pages.Home;

/// <summary>
/// Renders the landing page (#309) through bUnit over a stubbed TMS client: the live progress band
/// derives from the public progress counters, the page must survive an API outage with a calm notice
/// (never a broken landing), and the hero/join CTAs follow the visitor's authentication state.
/// </summary>
public sealed class HomeTests : BunitContext
{
    private readonly ITranslationSystemClient _client = Substitute.For<ITranslationSystemClient>();

    public HomeTests()
    {
        Services.AddSingleton(_client);
        Services.AddSingleton(StubDiscoveryCache.AdvertisingGet(Rels.Progress));
        Services.AddScoped<HomeProgressLoader>();
    }

    [Fact]
    public void Home_IsAnonymousByContract()
    {
        // The landing page is the public face of the project — a regression to [Authorize] would
        // lock recruiters and players out at the door.
        typeof(HomeComponent).GetCustomAttribute<AllowAnonymousAttribute>().ShouldNotBeNull();
    }

    [Fact]
    public void Render_WithProgress_ShowsTheHeroPercentTilesAndVersion()
    {
        AddAuthorization().SetNotAuthorized();
        StubProgress(ApiResult.Success(new PublicProgressResponse(
            Total: 200, Translated: 150, Approved: 80, CurrentGameVersion: "48.1")));

        IRenderedComponent<HomeComponent> component = RenderHome();

        component.Find(".progress-hero-num").TextContent.ShouldBe("40%");
        component.FindAll(".stat-tile").Count.ShouldBe(3);
        component.Find(".panel-aside").TextContent.ShouldContain("48.1");
        component.Find(".legend-chip .dot-awaiting").ShouldNotBeNull();
    }

    [Fact]
    public void Render_WithProgress_SizesTheTwoMeterSegmentsFromTheView()
    {
        AddAuthorization().SetNotAuthorized();
        StubProgress(ApiResult.Success(new PublicProgressResponse(
            Total: 100, Translated: 60, Approved: 25, CurrentGameVersion: null)));

        IRenderedComponent<HomeComponent> component = RenderHome();

        IElement approvedSegment = component.Find(".progress-duo .progress-bar:first-child");
        approvedSegment.GetAttribute("style").ShouldNotBeNull().ShouldContain("width: 25%");
        IElement awaitingSegment = component.Find(".progress-bar-awaiting");
        awaitingSegment.GetAttribute("style").ShouldNotBeNull().ShouldContain("width: 35%");
    }

    [Fact]
    public void Render_WhenProgressUnavailable_StillRendersThePageWithACalmNotice()
    {
        // The landing page must never break with the API: hero, steps and downloads stay useful.
        AddAuthorization().SetNotAuthorized();
        StubProgress(ApiResult.Failure<PublicProgressResponse>(
            new ProblemDetails { Title = "API nieosiągalne", Status = 503 }));

        IRenderedComponent<HomeComponent> component = RenderHome();

        component.Find("h1").TextContent.ShouldContain("po polsku");
        component.Find(".status-line.status-warning").TextContent.ShouldContain("chwilowo niedostępne");
        component.FindAll(".progress-hero").ShouldBeEmpty();
        component.FindAll(".stat-tile").ShouldBeEmpty();
    }

    [Fact]
    public void Render_WhenAnonymous_OffersTheJoinCtasInsteadOfThePanel()
    {
        AddAuthorization().SetNotAuthorized();
        StubProgress(ApiResult.Success(new PublicProgressResponse(0, 0, 0, null)));

        IRenderedComponent<HomeComponent> component = RenderHome();

        component.FindAll($"a[href='{AuthenticationDependencyInjectionExtensions.LoginPath}']")
            .ShouldNotBeEmpty();
        component.FindAll("a[href='/dashboard']").ShouldBeEmpty();
    }

    [Fact]
    public void Render_WhenAuthenticated_OffersThePanelCtasInsteadOfLogin()
    {
        AddAuthorization().SetAuthorized("Frodo");
        StubProgress(ApiResult.Success(new PublicProgressResponse(0, 0, 0, null)));

        IRenderedComponent<HomeComponent> component = RenderHome();

        component.FindAll("a[href='/dashboard']").ShouldNotBeEmpty();
        component.FindAll($"a[href='{AuthenticationDependencyInjectionExtensions.LoginPath}']")
            .ShouldBeEmpty();
    }

    [Fact]
    public void Render_Always_OffersThePublicDownloadAndTheTranslationsList()
    {
        AddAuthorization().SetNotAuthorized();
        StubProgress(ApiResult.Success(new PublicProgressResponse(0, 0, 0, null)));

        IRenderedComponent<HomeComponent> component = RenderHome();

        IElement download = component.Find("a[download]");
        download.GetAttribute("href").ShouldBe(ImportExportEndpointsExtensions.DownloadPath);
        download.GetAttribute("download").ShouldBe(ImportExportLoader.DownloadFileName);
        component.FindAll("a[href='/translations']").ShouldNotBeEmpty();
    }

    private void StubProgress(ApiResult<PublicProgressResponse> result) =>
        _client
            .GetApiResultAsync<PublicProgressResponse>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(result);

    /// <summary>
    /// Hosts the page inside a render fragment and resolves it via <c>FindComponent</c> — the page's
    /// <see cref="Microsoft.AspNetCore.Components.Authorization.AuthorizeView"/> resolves
    /// asynchronously, which the typed <c>Render&lt;Home&gt;</c> discovery does not see on its first
    /// synchronous pass (same seam as <c>NavMenuTests</c>).
    /// </summary>
    private IRenderedComponent<HomeComponent> RenderHome()
    {
        IRenderedComponent<ContainerFragment> fragment = Render(builder =>
        {
            builder.OpenComponent<HomeComponent>(0);
            builder.CloseComponent();
        });

        return fragment.FindComponent<HomeComponent>();
    }
}
