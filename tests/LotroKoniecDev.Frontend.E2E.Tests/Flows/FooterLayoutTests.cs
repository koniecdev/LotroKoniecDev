using LotroKoniecDev.Frontend.E2E.Tests.Infrastructure;
using Microsoft.Playwright;
using Shouldly;

namespace LotroKoniecDev.Frontend.E2E.Tests.Flows;

/// <summary>
/// The footer on every page: left-aligned columns that stack on a phone, the risk note at the left edge with
/// a readable line length, and the bottom row split to both sides (#1575).
/// </summary>
public sealed class FooterLayoutTests : E2ETestBase
{
    /// <summary>70 characters of the footer's 11 px monospace font, 7 px each.</summary>
    private const int ReadableLineWidth = 490;

    [Theory]
    [InlineData("/", 1920)]
    [InlineData("/translations", 1366)]
    [InlineData("/regulamin", 1920)]
    [InlineData("/", 390)]
    public async Task Risk_note_starts_at_the_left_edge_and_keeps_a_readable_line(string path, int width)
    {
        // Arrange
        await OpenAsync(path, width, 1080);
        LocatorBoundingBoxResult columns = await Page.Locator("footer .foot-grid").RequireBoundingBoxAsync();

        // Act
        LocatorBoundingBoxResult note = await Page.Locator("footer .risk-note").RequireBoundingBoxAsync();

        // Assert
        note.X.ShouldBe(columns.X, 1d);
        note.Width.ShouldBeLessThanOrEqualTo(Math.Min(ReadableLineWidth + 1, columns.Width));
    }

    [Fact]
    public async Task Columns_sit_side_by_side_on_a_wide_screen()
    {
        // Arrange
        await OpenAsync("/", 1366, 1080);

        // Act
        IReadOnlyList<LocatorBoundingBoxResult> columns = await ColumnBoxesAsync();

        // Assert
        columns.Count.ShouldBe(3);
        columns[1].Y.ShouldBe(columns[0].Y, 1d);
        columns[2].Y.ShouldBe(columns[0].Y, 1d);
        columns[1].X.ShouldBeGreaterThan(columns[0].X + columns[0].Width);
        columns[2].X.ShouldBeGreaterThan(columns[1].X + columns[1].Width);
    }

    [Fact]
    public async Task Columns_stack_at_the_left_edge_on_a_phone()
    {
        // Arrange
        await OpenAsync("/", 390, 844);

        // Act
        IReadOnlyList<LocatorBoundingBoxResult> columns = await ColumnBoxesAsync();

        // Assert
        columns.Count.ShouldBe(3);
        columns[1].X.ShouldBe(columns[0].X, 1d);
        columns[2].X.ShouldBe(columns[0].X, 1d);
        columns[1].Y.ShouldBeGreaterThanOrEqualTo(columns[0].Y + columns[0].Height);
        columns[2].Y.ShouldBeGreaterThanOrEqualTo(columns[1].Y + columns[1].Height);
    }

    [Fact]
    public async Task Bottom_row_puts_the_copyright_left_and_the_artist_right()
    {
        // Arrange
        await OpenAsync("/", 1366, 1080);
        LocatorBoundingBoxResult columns = await Page.Locator("footer .foot-grid").RequireBoundingBoxAsync();

        // Act
        LocatorBoundingBoxResult copyright = await Page.Locator("footer .foot-bottom p").First.RequireBoundingBoxAsync();
        LocatorBoundingBoxResult credit = await Page.Locator("footer .foot-credit").RequireBoundingBoxAsync();

        // Assert
        copyright.X.ShouldBe(columns.X, 1d);
        (credit.X + credit.Width).ShouldBe(columns.X + columns.Width, 1d);
        credit.Y.ShouldBe(copyright.Y, 1d);
    }

    public FooterLayoutTests(PlaywrightStackFixture fixture) : base(fixture)
    {
    }

    private async Task<IReadOnlyList<LocatorBoundingBoxResult>> ColumnBoxesAsync()
    {
        ILocator columns = Page.Locator("footer .foot-grid > *");
        int count = await columns.CountAsync();
        List<LocatorBoundingBoxResult> boxes = [];
        for (int index = 0; index < count; index++)
        {
            boxes.Add(await columns.Nth(index).RequireBoundingBoxAsync());
        }

        return boxes;
    }

    private async Task OpenAsync(string path, int width, int height)
    {
        await Page.SetViewportSizeAsync(width, height);
        await Page.GotoAsync($"{Fixture.FrontendBaseUrl}{path}");
        await Page.WaitForFontsAsync();
    }
}
