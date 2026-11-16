using LotroKoniecDev.Frontend.E2E.Tests.Infrastructure;
using Microsoft.Playwright;
using Shouldly;

namespace LotroKoniecDev.Frontend.E2E.Tests.Flows;

/// <summary>
/// The home page ends with the Discord banner, right on top of the footer (#1371). On a screen taller than
/// the page the footer moves to the bottom of the screen, and only a browser shows whether the banner went
/// with it.
/// </summary>
public sealed class HomeDiscordBannerTests : E2ETestBase
{
    private const string BannerLinkName = "Tłumacz LOTRO razem z nami. Dołącz do Discorda!";

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(1920, 3000)]
    [InlineData(390, 4000)]
    public async Task Banner_sits_right_on_top_of_the_footer(int width, int height)
    {
        // Arrange
        await Page.SetViewportSizeAsync(width, height);
        await Page.GotoAsync($"{Fixture.FrontendBaseUrl}/");
        await Page.EvaluateAsync<bool>("document.fonts.ready.then(() => true)");

        // Act
        LocatorBoundingBoxResult banner = await RequireBoundingBoxAsync(
            Page.GetByRole(AriaRole.Link, new() { Name = BannerLinkName, Exact = true }));
        LocatorBoundingBoxResult footer = await RequireBoundingBoxAsync(Page.GetByRole(AriaRole.Contentinfo));

        // Assert
        (banner.Y + banner.Height).ShouldBe(footer.Y, 1d);
    }

    public HomeDiscordBannerTests(PlaywrightStackFixture fixture) : base(fixture)
    {
    }

    private static async Task<LocatorBoundingBoxResult> RequireBoundingBoxAsync(ILocator locator) =>
        await locator.BoundingBoxAsync()
        ?? throw new InvalidOperationException($"'{locator}' has no bounding box — it is not rendered.");
}
