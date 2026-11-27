using LotroKoniecDev.Frontend.E2E.Tests.Infrastructure;
using Microsoft.Playwright;
using Shouldly;

namespace LotroKoniecDev.Frontend.E2E.Tests.Flows;

/// <summary>
/// Side by side, the player and translator boxes have the same height, and the big percentage is centred
/// against its text (#1457). Their texts wrap differently per browser and font, so only a browser shows it.
/// The gap between the boxes and between the sections is never wider than the page's side padding (#1572).
/// A step is as tall as its number badge, and a visitor's top bar fits a phone (#1575). The hero jump lands
/// below the top bar on a phone, also where the Discord pill takes a row of its own (#1590).
/// </summary>
public sealed class HomeLayoutTests : E2ETestBase
{
    private const int StepBadgeSize = 28;

    [Theory]
    [InlineData(1920)]
    // At 960 px the boxes are narrow, so their texts and buttons wrap to different heights.
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

    [Fact]
    public async Task One_line_step_is_as_tall_as_its_badge_with_its_text_centred_on_it()
    {
        // Arrange: the third player step fits on one line on a wide screen
        await OpenHomeAsync(1920, 1080);
        ILocator step = Page.Locator("#dla-graczy .steps li").Nth(2);

        // Act
        LocatorBoundingBoxResult stepBox = await step.RequireBoundingBoxAsync();
        LocatorBoundingBoxResult firstWords = await step.Locator("strong").RequireBoundingBoxAsync();

        // Assert: the badge is pinned to the top of the step, 28 px tall
        stepBox.Height.ShouldBe(StepBadgeSize, 0.5d);
        (firstWords.Y + firstWords.Height / 2).ShouldBe(stepBox.Y + StepBadgeSize / 2f, 1d);
    }

    [Fact]
    public async Task Wrapped_step_keeps_its_first_line_centred_on_the_badge()
    {
        // Arrange
        await OpenHomeAsync(390, 844);
        ILocator step = Page.Locator("#dla-graczy .steps li").Nth(1);

        // Act
        LocatorBoundingBoxResult stepBox = await step.RequireBoundingBoxAsync();
        LocatorBoundingBoxResult firstWords = await step.Locator("strong").RequireBoundingBoxAsync();

        // Assert
        stepBox.Height.ShouldBeGreaterThan(StepBadgeSize);
        (firstWords.Y + firstWords.Height / 2).ShouldBe(stepBox.Y + StepBadgeSize / 2f, 1d);
    }

    [Fact]
    public async Task Visitors_top_bar_on_a_360_px_phone_wraps_instead_of_scrolling_sideways()
    {
        // Arrange
        await OpenHomeAsync(360, 800);

        // Act
        bool scrollsSideways = await Page.Locator(".nav-links")
            .EvaluateAsync<bool>("links => links.scrollWidth > links.clientWidth");
        LocatorBoundingBoxResult signUp = await Page
            .GetByRole(AriaRole.Link, new() { Name = Links.SignUp, Exact = true })
            .RequireBoundingBoxAsync();

        // Assert
        scrollsSideways.ShouldBeFalse();
        (signUp.X + signUp.Width).ShouldBeLessThanOrEqualTo(360);
    }

    [Theory]
    [InlineData(360, false)]
    // Below about 340 px the Discord pill takes a row of its own, so the top bar is taller still (#1590).
    [InlineData(320, false)]
    [InlineData(320, true)]
    public async Task Hero_jump_on_a_phone_lands_the_players_box_below_the_top_bar(int width, bool signedIn)
    {
        // Arrange
        if (signedIn)
        {
            await AuthActions.LoginAsync(Page, Fixture, Fixture.Admin);
        }

        await OpenHomeAsync(width, 800);

        // Act
        await Page.GetByRole(AriaRole.Link, new() { Name = "Pobierz spolszczenie", Exact = true }).ClickAsync();
        await Page.WaitForFunctionAsync("() => window.scrollY > 0");
        LocatorBoundingBoxResult heading = await Page.Locator("#dla-graczy h2").RequireBoundingBoxAsync();
        LocatorBoundingBoxResult navbar = await Page.Locator("header.nav").RequireBoundingBoxAsync();

        // Assert
        heading.Y.ShouldBeGreaterThanOrEqualTo(navbar.Y + navbar.Height - 1);
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
