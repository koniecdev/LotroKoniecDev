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

    [Fact]
    public async Task Save_row_on_a_phone_with_the_fill_button_keeps_every_label_on_one_line()
    {
        // Arrange: a row of the seed's fill group shows four buttons. The page gets a five-digit count, the size
        // of the largest group on prod (13,682 rows), which the shared seed cannot hold (#1587).
        await OpenFillGroupRowAsync();
        await SetTheFillCountAsync(14000);

        // Act
        (LocatorBoundingBoxResult form, List<(LocatorBoundingBoxResult Box, int LabelLines)> buttons) =
            await MeasureTheSaveRowAsync();

        // Assert
        buttons.Count.ShouldBe(4);
        foreach ((LocatorBoundingBoxResult box, int labelLines) in buttons)
        {
            box.X.ShouldBeGreaterThanOrEqualTo(form.X - 0.5f);
            (box.X + box.Width).ShouldBeLessThanOrEqualTo(form.X + form.Width + 0.5f);
            labelLines.ShouldBe(1);
        }
    }

    [Fact]
    public async Task Slip_stop_on_a_phone_keeps_all_five_buttons_inside_the_form_with_their_labels_on_one_line()
    {
        // Arrange: two spaces in a row are a slip, so the fill stops and adds „Zapisz we wszystkich mimo to”.
        await OpenFillGroupRowAsync();
        await Page.GetByLabel("Tłumaczenie polskie", new() { Exact = true }).FillAsync("Włócznia  wojenna");
        await Page.GetByRole(AriaRole.Button, new() { Name = $"Zapisz we wszystkich identycznych ({TranslationRowsSeed.FillGroupRows - 1})", Exact = true })
            .ClickAsync();
        await Page.GetByText("Tekst nie jest jeszcze zapisany. Popraw go albo zapisz mimo to.", new() { Exact = true })
            .WaitForAsync(LongWait);
        await Page.WaitForFontsAsync();
        await SetTheFillCountAsync(14000);

        // Act
        (LocatorBoundingBoxResult form, List<(LocatorBoundingBoxResult Box, int LabelLines)> buttons) =
            await MeasureTheSaveRowAsync();

        // Assert
        buttons.Count.ShouldBe(5);
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

    private ILocator SaveForm() => Page.Locator("form").Filter(new() { Has = Page.Locator(".editor-save-actions") });

    /// <summary>Through the list's search, so the row has its place and all its buttons, on a 390 px phone.</summary>
    private async Task OpenFillGroupRowAsync()
    {
        await AuthActions.LoginAsync(Page, Fixture, Fixture.Admin);
        await Fixture.TranslationRows.EnsureSeededAsync(Page, Fixture.FrontendBaseUrl);

        await Page.SetViewportSizeAsync(390, 844);
        await Page.GotoAsync($"{Fixture.FrontendBaseUrl}/translations");
        await Page.GetByLabel("Wyszukaj", new() { Exact = true }).FillAsync(Fixture.TranslationRows.FillLayoutToken);
        await Page.GetByRole(AriaRole.Button, new() { Name = "Filtruj", Exact = true }).ClickAsync();
        await Page.GetByRole(AriaRole.Link, new() { Name = "Edytuj", Exact = true }).First.ClickAsync();
        await Page.GetByLabel("Tłumaczenie polskie", new() { Exact = true }).WaitForAsync(LongWait);
        await Page.WaitForFontsAsync();
    }

    private Task SetTheFillCountAsync(int count) =>
        SaveForm().Locator("button[value='fill']").EvaluateAsync(
            "(button, count) => button.textContent = `Zapisz we wszystkich identycznych (${count})`", count);

    private async Task<(LocatorBoundingBoxResult Form, List<(LocatorBoundingBoxResult Box, int LabelLines)> Buttons)>
        MeasureTheSaveRowAsync()
    {
        LocatorBoundingBoxResult form = await SaveForm().RequireBoundingBoxAsync();
        List<(LocatorBoundingBoxResult Box, int LabelLines)> buttons = [];
        foreach (ILocator button in await SaveForm().Locator(".editor-save-actions button").AllAsync())
        {
            buttons.Add((await button.RequireBoundingBoxAsync(), await button.EvaluateAsync<int>(LabelLineCountScript)));
        }

        return (form, buttons);
    }

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
