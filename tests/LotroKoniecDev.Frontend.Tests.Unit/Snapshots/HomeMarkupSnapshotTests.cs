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
/// Pins the rendered markup of the landing page (#571). <c>HomeTests</c> asserts the handful of
/// selectors each behavior is about; the snapshot covers everything between them — the section order,
/// the CTA set, the meter segments, the download affordance — so an unintended markup regression on
/// the public face of the project fails loudly instead of shipping.
/// </summary>
/// <remarks>
/// The counters are deliberately five-figure: <c>HomeProgressView.Format</c> renders them through a
/// fixed <c>NumberFormatInfo</c> whose NBSP group separator only appears above 999, and that separator
/// exists precisely so the rendering never becomes culture-dependent — a snapshot seeded with small
/// numbers would never see it. The zero-catalog state gets its own snapshot for the same reason: it is
/// the branch that swaps the counter classes and collapses the meter.
/// <para>
/// The page is Static SSR, so its markup is a pure function of the stubbed progress result and the
/// authentication state; nothing here depends on a clock, an ambient culture or a build fingerprint.
/// Re-accepting a verified file is a deliberate, reviewed act — read the diff before you do it.
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
    /// Hosts the page inside a render fragment and resolves it via <c>FindComponent</c> — the page's
    /// <c>AuthorizeView</c> resolves asynchronously, which the typed <c>Render&lt;Home&gt;</c>
    /// discovery does not see on its first synchronous pass (same seam as <c>HomeTests</c>).
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
