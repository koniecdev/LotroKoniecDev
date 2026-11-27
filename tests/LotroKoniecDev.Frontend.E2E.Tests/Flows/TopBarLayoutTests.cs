using LotroKoniecDev.Frontend.E2E.Tests.Infrastructure;
using Microsoft.Playwright;
using Shouldly;

namespace LotroKoniecDev.Frontend.E2E.Tests.Flows;

/// <summary>
/// The top bar wraps by its content: the Discord pill stays next to the logo, and the links take a second
/// row only when they do not fit. On a phone the pill sits at the right end of the logo's row, and on a
/// phone too narrow for that it takes a row of its own (#1590).
/// </summary>
public sealed class TopBarLayoutTests : E2ETestBase
{
    private const int PhoneSidePadding = 20;

    private const int DesktopSidePadding = 32;

    private const int MinimumWordWidth = 40;

    private ILocator Brand => Page.Locator("header.nav a.brand");

    private ILocator Pill => Page.Locator("header.nav a.nav-discord");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Discord_pill_on_a_360_px_phone_sits_at_the_right_end_of_the_logo_row(bool signedIn)
    {
        // Arrange
        await OpenHomeAsync(360, 800, signedIn);
        LocatorBoundingBoxResult brand = await Brand.RequireBoundingBoxAsync();

        // Act
        LocatorBoundingBoxResult pill = await Pill.RequireBoundingBoxAsync();
        bool scrollsSideways = await TopBarScrollsSidewaysAsync();

        // Assert
        (pill.Y + pill.Height / 2).ShouldBe(brand.Y + brand.Height / 2, 1d);
        (pill.X + pill.Width).ShouldBe(360 - PhoneSidePadding, 1d);
        scrollsSideways.ShouldBeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Discord_pill_on_a_320_px_phone_takes_its_own_row_with_its_word(bool signedIn)
    {
        // Arrange
        await OpenHomeAsync(320, 700, signedIn);
        LocatorBoundingBoxResult brand = await Brand.RequireBoundingBoxAsync();

        // Act
        LocatorBoundingBoxResult pill = await Pill.RequireBoundingBoxAsync();
        LocatorBoundingBoxResult word = await Pill.Locator("span").RequireBoundingBoxAsync();
        LocatorBoundingBoxResult links = await Page.Locator("header.nav .nav-links").RequireBoundingBoxAsync();
        bool scrollsSideways = await TopBarScrollsSidewaysAsync();

        // Assert
        pill.Y.ShouldBeGreaterThanOrEqualTo(brand.Y + brand.Height);
        pill.X.ShouldBe(brand.X, 1d);
        // The word is about 51 px wide; a word hidden for screen readers only would be 1 px.
        word.Width.ShouldBeGreaterThan(MinimumWordWidth);
        (word.X + word.Width).ShouldBeLessThanOrEqualTo(pill.X + pill.Width);
        links.Y.ShouldBeGreaterThanOrEqualTo(pill.Y + pill.Height);
        scrollsSideways.ShouldBeFalse();
    }

    [Theory]
    [InlineData(900, false)]
    [InlineData(1920, true)]
    public async Task Links_that_fit_stay_in_the_logo_row_right_of_the_pill(int width, bool signedIn)
    {
        // Arrange
        await OpenHomeAsync(width, 900, signedIn);
        LocatorBoundingBoxResult brand = await Brand.RequireBoundingBoxAsync();
        LocatorBoundingBoxResult pill = await Pill.RequireBoundingBoxAsync();

        // Act
        IReadOnlyList<LocatorBoundingBoxResult> links = await LinkBoxesAsync();
        bool scrollsSideways = await TopBarScrollsSidewaysAsync();

        // Assert: every link, "Wyloguj" included, sits on one line with one height
        links.Count.ShouldBeGreaterThan(1);
        links.ShouldAllBe(link => Math.Abs(link.Y - links[0].Y) <= 0.5 && Math.Abs(link.Height - links[0].Height) <= 0.5);
        (pill.Y + pill.Height / 2).ShouldBe(brand.Y + brand.Height / 2, 1d);
        (links[0].Y + links[0].Height / 2).ShouldBe(brand.Y + brand.Height / 2, 1d);
        links[0].X.ShouldBeGreaterThan(pill.X + pill.Width);
        (links[^1].X + links[^1].Width).ShouldBe(width - DesktopSidePadding, 1d);
        scrollsSideways.ShouldBeFalse();
    }

    [Fact]
    public async Task Signed_in_links_that_do_not_fit_take_a_second_row_at_its_start()
    {
        // Arrange: with the test admin's name the links need about 1177 px next to the logo and the pill
        await OpenHomeAsync(1024, 900, signedIn: true);
        LocatorBoundingBoxResult brand = await Brand.RequireBoundingBoxAsync();
        LocatorBoundingBoxResult pill = await Pill.RequireBoundingBoxAsync();

        // Act
        IReadOnlyList<LocatorBoundingBoxResult> links = await LinkBoxesAsync();
        bool scrollsSideways = await TopBarScrollsSidewaysAsync();

        // Assert: every link, "Wyloguj" included, sits on one line with one height
        links.Count.ShouldBeGreaterThan(1);
        links.ShouldAllBe(link => Math.Abs(link.Y - links[0].Y) <= 0.5 && Math.Abs(link.Height - links[0].Height) <= 0.5);
        (pill.Y + pill.Height / 2).ShouldBe(brand.Y + brand.Height / 2, 1d);
        links[0].Y.ShouldBeGreaterThanOrEqualTo(brand.Y + brand.Height);
        links[0].X.ShouldBe(brand.X, 1d);
        scrollsSideways.ShouldBeFalse();
    }

    public TopBarLayoutTests(PlaywrightStackFixture fixture) : base(fixture)
    {
    }

    private async Task<IReadOnlyList<LocatorBoundingBoxResult>> LinkBoxesAsync()
    {
        ILocator links = Page.Locator("header.nav .nav-link");
        int count = await links.CountAsync();
        List<LocatorBoundingBoxResult> boxes = [];
        for (int index = 0; index < count; index++)
        {
            boxes.Add(await links.Nth(index).RequireBoundingBoxAsync());
        }

        return boxes;
    }

    /// <summary>The signed-in links row on a phone scrolls inside itself (#1575), which does not count here.</summary>
    private Task<bool> TopBarScrollsSidewaysAsync() =>
        Page.Locator("header.nav").EvaluateAsync<bool>("nav => nav.scrollWidth > nav.clientWidth");

    private async Task OpenHomeAsync(int width, int height, bool signedIn)
    {
        if (signedIn)
        {
            await AuthActions.LoginAsync(Page, Fixture, Fixture.Admin);
        }

        await Page.SetViewportSizeAsync(width, height);
        await Page.GotoAsync($"{Fixture.FrontendBaseUrl}/");
        await Page.WaitForFontsAsync();
    }
}
