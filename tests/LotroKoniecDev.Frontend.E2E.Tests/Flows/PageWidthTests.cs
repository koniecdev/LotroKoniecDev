using LotroKoniecDev.Frontend.E2E.Tests.Infrastructure;
using Microsoft.Playwright;
using Shouldly;

namespace LotroKoniecDev.Frontend.E2E.Tests.Flows;

/// <summary>
/// Every page of the website is as wide as the window up to 1920 px, and centred on a wider one (#1572).
/// The terms document and the home page's risk note stop at their width on a 1400 px page, so a wide
/// screen does not turn them into very long lines.
/// </summary>
public sealed class PageWidthTests : E2ETestBase
{
    private const int MaxPageWidth = 1920;
    private const int SidePadding = 32;
    private const int ContentWidthOn1400PxPage = 1400 - 2 * SidePadding;
    private const int TermsColumnGap = 20;
    private const int TermsContentsListWidth = 260;
    private const int TermsDocumentMaxWidth = ContentWidthOn1400PxPage - TermsColumnGap - TermsContentsListWidth;

    [Theory]
    [InlineData("/", 1366)]
    [InlineData("/", 1920)]
    [InlineData("/", 2560)]
    [InlineData("/translations", 1366)]
    [InlineData("/translations", 1920)]
    [InlineData("/translations", 2560)]
    public async Task Page_is_as_wide_as_the_window_up_to_1920_px_and_centred(string path, int width)
    {
        // Arrange
        await OpenAsync(path, width);
        int windowWidth = await WindowWidthAsync();
        float expectedWidth = Math.Min(windowWidth, MaxPageWidth);

        // Act
        LocatorBoundingBoxResult content = await ContentSection().RequireBoundingBoxAsync();

        // Assert
        content.Width.ShouldBe(expectedWidth, 1d);
        content.X.ShouldBe((windowWidth - expectedWidth) / 2, 1d);
    }

    [Theory]
    [InlineData(1366)]
    [InlineData(1920)]
    [InlineData(2560)]
    public async Task Home_progress_panel_fills_the_page_minus_the_side_padding(int width)
    {
        // Arrange
        await OpenAsync("/", width);
        LocatorBoundingBoxResult content = await ContentSection().RequireBoundingBoxAsync();

        // Act
        LocatorBoundingBoxResult panel = await Page.Locator("#postep").RequireBoundingBoxAsync();

        // Assert
        panel.X.ShouldBe(content.X + SidePadding, 1d);
        panel.Width.ShouldBe(content.Width - 2 * SidePadding, 1d);
    }

    [Theory]
    [InlineData(1366)]
    [InlineData(1920)]
    [InlineData(2560)]
    public async Task Home_risk_note_stops_at_the_content_width_of_a_1400_px_page_and_stays_centred(int width)
    {
        // Arrange
        await OpenAsync("/", width);
        LocatorBoundingBoxResult content = await ContentSection().RequireBoundingBoxAsync();
        float contentWidth = content.Width - 2 * SidePadding;

        // Act
        LocatorBoundingBoxResult note = await Page.Locator(".risk-note").RequireBoundingBoxAsync();

        // Assert
        note.Width.ShouldBe(Math.Min(contentWidth, ContentWidthOn1400PxPage), 1d);
        (note.X + note.Width / 2).ShouldBe(content.X + content.Width / 2, 1d);
    }

    [Theory]
    [InlineData(1366)]
    [InlineData(1400)]
    public async Task Terms_document_fills_the_space_next_to_the_contents_list_up_to_a_1400_px_page(int width)
    {
        // Arrange
        await OpenAsync("/regulamin", width);
        LocatorBoundingBoxResult content = await ContentSection().RequireBoundingBoxAsync();

        // Act
        LocatorBoundingBoxResult document = await Page.Locator(".legal-doc").RequireBoundingBoxAsync();
        LocatorBoundingBoxResult tableOfContents = await Page.Locator(".legal-side").RequireBoundingBoxAsync();

        // Assert
        (tableOfContents.X - (document.X + document.Width)).ShouldBe(TermsColumnGap, 1d);
        (tableOfContents.X + tableOfContents.Width).ShouldBe(content.X + content.Width - SidePadding, 1d);
    }

    [Fact]
    public async Task Terms_document_on_a_wide_screen_stays_as_wide_as_on_a_1400_px_page_and_the_contents_list_moves_to_the_right_edge()
    {
        // Arrange
        await OpenAsync("/regulamin", 1920);

        // Act
        LocatorBoundingBoxResult content = await ContentSection().RequireBoundingBoxAsync();
        LocatorBoundingBoxResult document = await Page.Locator(".legal-doc").RequireBoundingBoxAsync();
        LocatorBoundingBoxResult tableOfContents = await Page.Locator(".legal-side").RequireBoundingBoxAsync();

        // Assert
        document.Width.ShouldBe(TermsDocumentMaxWidth, 1d);
        (tableOfContents.X + tableOfContents.Width).ShouldBe(content.X + content.Width - SidePadding, 1d);
    }

    public PageWidthTests(PlaywrightStackFixture fixture) : base(fixture)
    {
    }

    /// <summary>The nav, the page head and the footer are <c>.container</c> too; this is the page's one content section.</summary>
    private ILocator ContentSection() => Page.Locator("section.container.content-wrap");

    private async Task<int> WindowWidthAsync() =>
        await Page.EvaluateAsync<int>("document.documentElement.clientWidth");

    private async Task OpenAsync(string path, int width)
    {
        await Page.SetViewportSizeAsync(width, 1080);
        await Page.GotoAsync($"{Fixture.FrontendBaseUrl}{path}");
        await Page.WaitForFontsAsync();
    }
}
