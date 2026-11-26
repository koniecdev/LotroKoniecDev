using System.Reflection;
using AngleSharp.Dom;
using Bunit.Rendering;
using Bunit.TestDoubles;
using LotroKoniecDev.Frontend.Components.Pages.Home;
using LotroKoniecDev.Frontend.Components.Pages.ImportExport;
using LotroKoniecDev.Frontend.Components.Shared;
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
/// The landing page (#309): the progress band from the public counters, a calm message on an API outage,
/// and the buttons that follow whether the visitor is signed in. Players get the patcher, never the file
/// it downloads itself (#1575).
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
        // The landing page is the public face of the project.
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
    public void Render_WhenAnonymous_SendsBothJoinButtonsToTheSignUpInsteadOfThePanel()
    {
        AddAuthorization().SetNotAuthorized();
        StubProgress(ApiResult.Success(new PublicProgressResponse(0, 0, 0, null)));

        IRenderedComponent<HomeComponent> component = RenderHome();

        string[] joinButtons = component
            .FindAll($"a[href='{AuthenticationDependencyInjectionExtensions.RegisterPath}']")
            .Select(link => link.TextContent.Trim())
            .ToArray();
        joinButtons.ShouldBe(["Dołącz jako tłumacz", "Zarejestruj się"]);
        component.FindAll($"a[href='{AuthenticationDependencyInjectionExtensions.LoginPath}']").ShouldBeEmpty();
        component.FindAll("a[href='/dashboard']").ShouldBeEmpty();
    }

    [Fact]
    public void Render_WhenAuthenticated_OffersThePanelCtasInsteadOfTheSignUp()
    {
        AddAuthorization().SetAuthorized("Frodo");
        StubProgress(ApiResult.Success(new PublicProgressResponse(0, 0, 0, null)));

        IRenderedComponent<HomeComponent> component = RenderHome();

        component.FindAll("a[href='/dashboard']").ShouldNotBeEmpty();
        component.FindAll($"a[href='{AuthenticationDependencyInjectionExtensions.RegisterPath}']")
            .ShouldBeEmpty();
        component.FindAll($"a[href='{AuthenticationDependencyInjectionExtensions.LoginPath}']")
            .ShouldBeEmpty();
    }

    [Fact]
    public void Render_Always_OffersThePatcherInsteadOfTheTranslationFile()
    {
        AddAuthorization().SetNotAuthorized();
        StubProgress(ApiResult.Success(new PublicProgressResponse(0, 0, 0, null)));

        IRenderedComponent<HomeComponent> component = RenderHome();

        IElement patcher = component.Find("#dla-graczy a.btn-primary");
        patcher.GetAttribute("href").ShouldBe(ExternalLinks.PatcherReadme);
        patcher.TextContent.ShouldBe("Patcher CLI");
        component.FindAll("a[download]").ShouldBeEmpty();
        component.FindAll($"a[href='{ImportExportEndpointsExtensions.DownloadPath}']").ShouldBeEmpty();
        component.FindAll("a[href='/translations']").ShouldNotBeEmpty();
    }

    [Fact]
    public void Render_Always_ShowsTheWindowsAppAsADisabledButtonWithNoAddress()
    {
        AddAuthorization().SetNotAuthorized();
        StubProgress(ApiResult.Success(new PublicProgressResponse(0, 0, 0, null)));

        IRenderedComponent<HomeComponent> component = RenderHome();

        IElement app = component.FindAll("#dla-graczy .editor-actions > *")
            .Single(button => button.TextContent == "Aplikacja Windows (wkrótce)");
        app.TagName.ShouldBe("SPAN");
        app.HasAttribute("href").ShouldBeFalse();
        app.GetAttribute("aria-disabled").ShouldBe("true");
        app.GetAttribute("role").ShouldBe("link");
        app.ClassList.ShouldContain("is-disabled");
    }

    [Fact]
    public void Render_Always_ListsWhatThePatcherDoesInThePlayerSteps()
    {
        AddAuthorization().SetNotAuthorized();
        StubProgress(ApiResult.Success(new PublicProgressResponse(0, 0, 0, null)));

        IRenderedComponent<HomeComponent> component = RenderHome();

        string[] steps = component.FindAll("#dla-graczy .steps li strong")
            .Select(step => step.TextContent)
            .ToArray();
        steps.ShouldBe(["Pobierz patcher", "Uruchom grę przez patcher", "Graj po polsku"]);
    }

    [Fact]
    public void Render_Page_EndsTheContentWithTheTwoBoxesAndKeepsTheRiskNoteForTheFooter()
    {
        AddAuthorization().SetNotAuthorized();
        StubProgress(ApiResult.Success(new PublicProgressResponse(0, 0, 0, null)));

        IRenderedComponent<HomeComponent> component = RenderHome();

        component.Find(".content-wrap").LastElementChild.ShouldNotBeNull().ClassList.ShouldContain("home-duo");
        component.FindAll(".risk-note").ShouldBeEmpty();
    }

    [Fact]
    public void Render_Page_LinksTheDiscordBannerInANewTab()
    {
        AddAuthorization().SetNotAuthorized();
        StubProgress(ApiResult.Success(new PublicProgressResponse(0, 0, 0, null)));

        IRenderedComponent<HomeComponent> component = RenderHome();

        IElement bannerLink = component.Find("a.home-banner");
        bannerLink.GetAttribute("href").ShouldBe("https://discord.gg/sncXwa64Ex");
        bannerLink.GetAttribute("target").ShouldBe("_blank");
        bannerLink.GetAttribute("rel").ShouldBe("noopener");
        bannerLink.QuerySelector("img").ShouldNotBeNull()
            .GetAttribute("alt").ShouldBe("Tłumacz LOTRO razem z nami. Dołącz do Discorda!");
    }

    [Fact]
    public void Render_Page_PutsTheBannerAfterTheLastSectionOutsideThePanels()
    {
        AddAuthorization().SetNotAuthorized();
        StubProgress(ApiResult.Success(new PublicProgressResponse(0, 0, 0, null)));

        IRenderedComponent<HomeComponent> component = RenderHome();

        IElement bannerLink = component.Find("a.home-banner");
        bannerLink.Closest("section").ShouldBeNull();
        bannerLink.PreviousElementSibling.ShouldNotBeNull().ClassList.ShouldContain("content-wrap");
    }

    [Fact]
    public void Render_Page_ShowsTheWideStripFrom641PxAndThePhoneBannerBelow()
    {
        AddAuthorization().SetNotAuthorized();
        StubProgress(ApiResult.Success(new PublicProgressResponse(0, 0, 0, null)));

        IRenderedComponent<HomeComponent> component = RenderHome();

        IElement picture = component.Find("a.home-banner > picture");
        IElement strip = picture.QuerySelector("source").ShouldNotBeNull();
        strip.GetAttribute("media").ShouldBe("(min-width: 641px)");
        strip.GetAttribute("srcset").ShouldBe("discord-strip.webp");
        picture.QuerySelector("img").ShouldNotBeNull().GetAttribute("src").ShouldBe("discord-banner.webp");
    }

    private void StubProgress(ApiResult<PublicProgressResponse> result) =>
        _client
            .GetApiResultAsync<PublicProgressResponse>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(result);

    /// <summary>
    /// Through a render fragment, because the typed <c>Render&lt;Home&gt;</c> does not see the
    /// asynchronous <c>AuthorizeView</c> on its first pass.
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
