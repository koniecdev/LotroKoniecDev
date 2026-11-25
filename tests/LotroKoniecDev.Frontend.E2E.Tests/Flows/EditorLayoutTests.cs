using LotroKoniecDev.Frontend.E2E.Tests.Infrastructure;
using Microsoft.Playwright;
using Shouldly;

namespace LotroKoniecDev.Frontend.E2E.Tests.Flows;

/// <summary>
/// The editor's English and Polish boxes are never further apart than the page's side padding (#1572). The
/// layout is the same for every row, so the flow opens the first seeded row (<see cref="TranslationRowsSeed"/>).
/// </summary>
public sealed class EditorLayoutTests : E2ETestBase
{
    private static readonly LocatorWaitForOptions LongWait = new() { Timeout = 30_000 };

    [Fact]
    public async Task English_and_Polish_boxes_side_by_side_are_24_px_apart()
    {
        // Arrange
        await OpenEditorAsync(1920, 1080);

        // Act
        LocatorBoundingBoxResult english = await EditorBoxes().First.RequireBoundingBoxAsync();
        LocatorBoundingBoxResult polish = await EditorBoxes().Last.RequireBoundingBoxAsync();

        // Assert
        (polish.X - (english.X + english.Width)).ShouldBe(24, 1d);
    }

    [Fact]
    public async Task English_and_Polish_boxes_stacked_on_a_phone_are_20_px_apart()
    {
        // Arrange
        await OpenEditorAsync(390, 844);

        // Act
        LocatorBoundingBoxResult english = await EditorBoxes().First.RequireBoundingBoxAsync();
        LocatorBoundingBoxResult polish = await EditorBoxes().Last.RequireBoundingBoxAsync();

        // Assert
        (polish.Y - (english.Y + english.Height)).ShouldBe(20, 1d);
    }

    public EditorLayoutTests(PlaywrightStackFixture fixture) : base(fixture)
    {
    }

    private ILocator EditorBoxes() => Page.Locator(".editor-grid > .panel");

    private async Task OpenEditorAsync(int width, int height)
    {
        await AuthActions.LoginAsync(Page, Fixture, Fixture.Admin);
        await Fixture.TranslationRows.EnsureSeededAsync(Page, Fixture.FrontendBaseUrl);

        await Page.SetViewportSizeAsync(width, height);
        await Page.GotoAsync($"{Fixture.FrontendBaseUrl}/translations");
        await Page.GetByRole(AriaRole.Link, new() { Name = "Edytuj", Exact = true }).First.ClickAsync();
        await Page.GetByLabel("Tłumaczenie polskie", new() { Exact = true }).WaitForAsync(LongWait);
        await Page.WaitForFontsAsync();
    }
}
