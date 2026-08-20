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
/// Pins the rendered markup of the landing page (#571). <c>HomeTests</c> checks the few selectors each
/// behaviour is about, and the snapshot covers everything in between: the order of the sections, the
/// buttons, the parts of the progress bar and the download link. So an accidental change to the public
/// face of the project fails loudly instead of shipping.
/// </summary>
/// <remarks>
/// The counters have five digits on purpose. <c>HomeProgressView.Format</c> prints them with a fixed
/// <c>NumberFormatInfo</c> whose group separator, a non-breaking space, only appears above 999, and that
/// separator exists so the output never depends on the machine's culture. A snapshot with small numbers
/// would never show it.
/// The empty-catalog state has its own snapshot for the same reason: it is the branch that changes the
/// counter classes and collapses the bar.
/// <para>
/// The page is static SSR, so its markup depends only on the stubbed progress result and on whether the
/// visitor is logged in. Nothing here depends on the clock, the culture or a build fingerprint.
/// Accepting a new verified file is a deliberate act: read the diff before you do it.
/// </para>
/// </remarks>
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
        // The outage fallback is the state nobody looks at in a browser, so it is exactly the one a
        // markup regression would reach production in.
        AddAuthorization().SetNotAuthorized();
        StubProgress(ApiResult.Failure<PublicProgressResponse>(
            new ProblemDetails { Title = "API nieosiągalne", Status = 503 }));

        IRenderedComponent<HomeComponent> component = RenderHome();

        await Verifier.Verify(component.Markup, "html");
    }

    [Fact]
    public async Task Render_WithEmptyCatalog_MatchesTheZeroStateMarkup()
    {
        // Before the first import every counter is zero: the tiles take their "zero" class and the
        // meter has to collapse without dividing by zero.
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
    /// Puts the page inside a render fragment and finds it with <c>FindComponent</c>. The page's
    /// <c>AuthorizeView</c> resolves asynchronously, and the typed <c>Render&lt;Home&gt;</c> does not see
    /// that on its first pass. <c>HomeTests</c> uses the same trick.
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
