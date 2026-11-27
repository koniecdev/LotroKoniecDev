using LotroKoniecDev.Frontend.E2E.Tests.Infrastructure;
using Microsoft.Playwright;
using Shouldly;

namespace LotroKoniecDev.Frontend.E2E.Tests.Flows;

/// <summary>
/// The editor's English and Polish boxes are never further apart than the page's side padding (#1572), and
/// its save row fits a phone (#1585). The layout is the same for every row, so the flow opens the first seeded
/// row (<see cref="TranslationRowsSeed"/>).
/// </summary>
public sealed class EditorLayoutTests : E2ETestBase
{
    /// <summary>
    /// The number of lines the button's label takes. In one flex line every button is as tall as the tallest,
    /// so equal heights would not show a label that broke onto two lines.
    /// </summary>
    private const string LabelLineCountScript = """
        button => {
            const range = document.createRange();
            range.selectNodeContents(button);
            return new Set(Array.from(range.getClientRects(), rect => Math.round(rect.top))).size;
        }
        """;

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

    [Fact]
    public async Task Save_row_on_a_phone_keeps_every_button_inside_the_form_with_its_label_on_one_line()
    {
        // Arrange: "Edytuj" gives the row its place, so all three buttons show.
        await OpenEditorAsync(390, 844);
        ILocator saveForm = Page.Locator("form").Filter(new() { Has = Page.Locator(".editor-save-actions") });

        // Act
        LocatorBoundingBoxResult form = await saveForm.RequireBoundingBoxAsync();
        List<(LocatorBoundingBoxResult Box, int LabelLines)> buttons = [];
        foreach (ILocator button in await saveForm.Locator(".editor-save-actions button").AllAsync())
        {
            buttons.Add((await button.RequireBoundingBoxAsync(), await button.EvaluateAsync<int>(LabelLineCountScript)));
        }

        // Assert
        buttons.Count.ShouldBe(3);
        foreach ((LocatorBoundingBoxResult box, int labelLines) in buttons)
        {
            box.X.ShouldBeGreaterThanOrEqualTo(form.X - 0.5f);
            (box.X + box.Width).ShouldBeLessThanOrEqualTo(form.X + form.Width + 0.5f);
            labelLines.ShouldBe(1);
        }
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
