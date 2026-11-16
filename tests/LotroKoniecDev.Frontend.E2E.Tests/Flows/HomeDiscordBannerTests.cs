using LotroKoniecDev.Frontend.E2E.Tests.Infrastructure;
using Microsoft.Playwright;
using Shouldly;

namespace LotroKoniecDev.Frontend.E2E.Tests.Flows;

/// <summary>
/// The home page ends with the Discord banner, right on top of the footer, across the whole screen (#1371).
/// On a screen taller than the page the footer moves to the bottom of the screen, and only a browser shows
/// whether the banner went with it.
/// </summary>
public sealed class HomeDiscordBannerTests : E2ETestBase
{
    private const string BannerName = "Tłumacz LOTRO razem z nami. Dołącz do Discorda!";
    private const int StripWidth = 2172;

    /// <summary>How much taller than the page the screen is, so the case holds however the page grows.</summary>
    private const int ScreenOverPageHeight = 500;

    [Fact]
    public async Task Banner_on_a_screen_shorter_than_the_page_sits_right_on_top_of_the_footer()
    {
        // Arrange
        await OpenHomeAsync(1920, 1080);

        // Act
        LocatorBoundingBoxResult banner = await BannerLink().RequireBoundingBoxAsync();
        LocatorBoundingBoxResult footer = await Page.GetByRole(AriaRole.Contentinfo).RequireBoundingBoxAsync();

        // Assert
        (banner.Y + banner.Height).ShouldBe(footer.Y, 1d);
    }

    [Theory]
    [InlineData(1920)]
    [InlineData(390)]
    public async Task Banner_on_a_screen_taller_than_the_page_sits_right_on_top_of_the_footer(int width)
    {
        // Arrange
        await OpenHomeAsync(width, 900);
        int pageHeight = await Page.EvaluateAsync<int>("document.documentElement.scrollHeight");
        await Page.SetViewportSizeAsync(width, pageHeight + ScreenOverPageHeight);

        // Act
        LocatorBoundingBoxResult banner = await BannerLink().RequireBoundingBoxAsync();
        LocatorBoundingBoxResult footer = await Page.GetByRole(AriaRole.Contentinfo).RequireBoundingBoxAsync();

        // Assert
        (banner.Y + banner.Height).ShouldBe(footer.Y, 1d);
    }

    [Fact]
    public async Task Strip_on_a_screen_wider_than_itself_keeps_its_own_width_centred_on_the_full_width_band()
    {
        // Arrange
        const int screenWidth = 2560;
        await OpenHomeAsync(screenWidth, 1440);

        // Act
        LocatorBoundingBoxResult band = await BannerLink().RequireBoundingBoxAsync();
        LocatorBoundingBoxResult strip = await Page
            .GetByRole(AriaRole.Img, new() { Name = BannerName, Exact = true })
            .RequireBoundingBoxAsync();

        // Assert
        band.X.ShouldBe(0, 1d);
        band.Width.ShouldBe(screenWidth, 1d);
        strip.Width.ShouldBe(StripWidth, 1d);
        strip.X.ShouldBe((screenWidth - StripWidth) / 2, 1d);
    }

    public HomeDiscordBannerTests(PlaywrightStackFixture fixture) : base(fixture)
    {
    }

    private ILocator BannerLink() => Page.GetByRole(AriaRole.Link, new() { Name = BannerName, Exact = true });

    private async Task OpenHomeAsync(int width, int height)
    {
        await Page.SetViewportSizeAsync(width, height);
        await Page.GotoAsync($"{Fixture.FrontendBaseUrl}/");
        await Page.WaitForFontsAsync();
    }
}
