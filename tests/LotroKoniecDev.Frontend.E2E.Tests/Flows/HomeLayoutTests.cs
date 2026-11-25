using LotroKoniecDev.Frontend.E2E.Tests.Infrastructure;
using Microsoft.Playwright;
using Shouldly;

namespace LotroKoniecDev.Frontend.E2E.Tests.Flows;

/// <summary>
/// Side by side, the player and translator boxes have the same height, and the big percentage is centred
/// against its text (#1457). Their texts wrap differently per browser and font, so only a browser shows it.
/// The gap between the boxes and between the sections is never wider than the page's side padding (#1572).
/// </summary>
public sealed class HomeLayoutTests : E2ETestBase
{
    [Theory]
    [InlineData(1920)]
    // At 960 px the sign-in button wraps, so the translator box is the taller one.
    [InlineData(960)]
    public async Task Player_and_translator_boxes_side_by_side_have_the_same_height(int width)
    {
        // Arrange
        await OpenHomeAsync(width, 1080);

        // Act
        LocatorBoundingBoxResult players = await Page.Locator("#dla-graczy").RequireBoundingBoxAsync();
        LocatorBoundingBoxResult translators = await Page.Locator("#dla-tlumaczy").RequireBoundingBoxAsync();

        // Assert
        players.Y.ShouldBe(translators.Y, 1d);
        players.Height.ShouldBe(translators.Height, 1d);
    }

    [Fact]
    public async Task Progress_percentage_is_centred_vertically_against_its_text()
    {
        // Arrange
        await OpenHomeAsync(1920, 1080);

        // Act
        LocatorBoundingBoxResult number = await Page.Locator(".progress-hero-num").RequireBoundingBoxAsync();
        LocatorBoundingBoxResult text = await Page.Locator(".progress-hero-lbl").RequireBoundingBoxAsync();

        // Assert
        (number.Y + number.Height / 2).ShouldBe(text.Y + text.Height / 2, 1d);
    }

    [Fact]
    public async Task Player_and_translator_boxes_side_by_side_are_24_px_apart()
    {
        // Arrange
        await OpenHomeAsync(1920, 1080);

        // Act
        LocatorBoundingBoxResult players = await Page.Locator("#dla-graczy").RequireBoundingBoxAsync();
        LocatorBoundingBoxResult translators = await Page.Locator("#dla-tlumaczy").RequireBoundingBoxAsync();

        // Assert
        (translators.X - (players.X + players.Width)).ShouldBe(24, 1d);
    }

    [Fact]
    public async Task Player_and_translator_boxes_stacked_on_a_phone_are_20_px_apart()
    {
        // Arrange
        await OpenHomeAsync(390, 844);

        // Act
        LocatorBoundingBoxResult players = await Page.Locator("#dla-graczy").RequireBoundingBoxAsync();
        LocatorBoundingBoxResult translators = await Page.Locator("#dla-tlumaczy").RequireBoundingBoxAsync();

        // Assert
        (translators.Y - (players.Y + players.Height)).ShouldBe(20, 1d);
    }

    [Theory]
    [InlineData(1920, 24)]
    [InlineData(390, 20)]
    public async Task Sections_are_as_far_apart_as_the_box_gap(int width, int expectedGap)
    {
        // Arrange
        await OpenHomeAsync(width, 1080);

        // Act
        LocatorBoundingBoxResult progress = await Page.Locator("#postep").RequireBoundingBoxAsync();
        LocatorBoundingBoxResult players = await Page.Locator("#dla-graczy").RequireBoundingBoxAsync();

        // Assert
        (players.Y - (progress.Y + progress.Height)).ShouldBe(expectedGap, 1d);
    }

    public HomeLayoutTests(PlaywrightStackFixture fixture) : base(fixture)
    {
    }

    private async Task OpenHomeAsync(int width, int height)
    {
        await Page.SetViewportSizeAsync(width, height);
        await Page.GotoAsync($"{Fixture.FrontendBaseUrl}/");
        await Page.WaitForFontsAsync();
    }
}
