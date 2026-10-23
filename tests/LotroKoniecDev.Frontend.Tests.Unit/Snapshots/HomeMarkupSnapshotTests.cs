using Bunit.Rendering;
using Bunit.TestDoubles;
using LotroKoniecDev.Frontend.Components.Pages.Home;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients.TranslationSystemHttpClients;
using LotroKoniecDev.TranslationSystem.Contracts.Progress;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using HomeComponent = LotroKoniecDev.Frontend.Components.Pages.Home.Home;
using LotroKoniecDev.Frontend.Tests.Unit.Infrastructure.Discovery;
using LotroKoniecDev.TranslationSystem.Contracts.Hateoas;

namespace LotroKoniecDev.Frontend.Tests.Unit.Snapshots;

/// <summary>
/// Pins the landing page's whole markup (#571); <c>HomeTests</c> checks the selectors each behaviour is
/// about. The counters have five digits so the non-breaking group separator shows. The empty-catalog state
/// has its own snapshot, because it changes the counter classes and collapses the bar. Read the diff before
/// you accept a new verified file.
/// </summary>
public sealed class HomeMarkupSnapshotTests : BunitContext
{
    private readonly ITranslationSystemClient _client = Substitute.For<ITranslationSystemClient>();

    public HomeMarkupSnapshotTests()
    {
        Services.AddSingleton(_client);
        Services.AddSingleton(StubDiscoveryCache.AdvertisingGet(Rels.Progress));
        Services.AddScoped<HomeProgressLoader>();
    }

    [Fact]
    public async Task Render_AnonymousWithProgress_MatchesTheLandingPageMarkup()
    {
        AddAuthorization().SetNotAuthorized();
        StubProgress(ApiResult.Success(new PublicProgressResponse(
            Total: 12_400, Translated: 9_150, Approved: 4_800, CurrentGameVersion: "48.1")));

        IRenderedComponent<HomeComponent> component = RenderHome();

        await Verifier.Verify(component.Markup, "html");
    }

    [Fact]
    public async Task Render_WhenProgressUnavailable_MatchesTheDegradedLandingPageMarkup()
    {
        // Nobody looks at the outage state in a browser, so a regression there would reach production.
        AddAuthorization().SetNotAuthorized();
        StubProgress(ApiResult.Failure<PublicProgressResponse>(
            new ProblemDetails { Title = "API nieosiągalne", Status = 503 }));

        IRenderedComponent<HomeComponent> component = RenderHome();

        await Verifier.Verify(component.Markup, "html");
    }

    [Fact]
    public async Task Render_WithEmptyCatalog_MatchesTheZeroStateMarkup()
    {
        // Before the first import the meter must collapse without dividing by zero.
        AddAuthorization().SetNotAuthorized();
        StubProgress(ApiResult.Success(new PublicProgressResponse(
            Total: 0, Translated: 0, Approved: 0, CurrentGameVersion: null)));

        IRenderedComponent<HomeComponent> component = RenderHome();

        await Verifier.Verify(component.Markup, "html");
    }

    [Fact]
    public async Task Render_WhenAuthenticated_MatchesTheSignedInLandingPageMarkup()
    {
        AddAuthorization().SetAuthorized("Frodo");
        StubProgress(ApiResult.Success(new PublicProgressResponse(
            Total: 12_400, Translated: 9_150, Approved: 4_800, CurrentGameVersion: "48.1")));

        IRenderedComponent<HomeComponent> component = RenderHome();

        await Verifier.Verify(component.Markup, "html");
    }

    private void StubProgress(ApiResult<PublicProgressResponse> result) =>
        _client
            .GetApiResultAsync<PublicProgressResponse>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(result);

    /// <summary>
    /// Through a render fragment, because the typed <c>Render&lt;Home&gt;</c> does not see the page's
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
