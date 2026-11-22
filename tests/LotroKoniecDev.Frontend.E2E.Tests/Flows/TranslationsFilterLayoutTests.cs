using LotroKoniecDev.Frontend.E2E.Tests.Infrastructure;
using Microsoft.Playwright;
using Shouldly;

namespace LotroKoniecDev.Frontend.E2E.Tests.Flows;

/// <summary>
/// The translation list's filter row holds six fields (#1477). On a narrow desktop window the last fields
/// wrap to a second line, and the search box keeps a usable width. Only a browser with the real fonts
/// shows it. Below 881 px the mobile rule stacks the fields.
/// </summary>
public sealed class TranslationsFilterLayoutTests : E2ETestBase
{
    /// <summary>
    /// Just under the search field's 12rem flex basis, so a row that squeezes the field fails.
    /// </summary>
    private const float MinimumSearchWidth = 190;

    [Theory]
    [InlineData(1920)]
    [InlineData(1366)]
    // About 1162 px is the last width where everything fits on one line.
    [InlineData(1170)]
    [InlineData(1100)]
    [InlineData(900)]
    public async Task Every_filter_control_stays_inside_the_form_and_the_search_box_keeps_its_width(int width)
    {
        // Arrange
        await OpenTranslationsAsync(width);

        // Act
        LocatorBoundingBoxResult form = await Page.Locator("form.filter").RequireBoundingBoxAsync();
        LocatorBoundingBoxResult search = await SearchBox().RequireBoundingBoxAsync();
        List<LocatorBoundingBoxResult> controls = [];
        foreach (ILocator control in Controls())
        {
            controls.Add(await control.RequireBoundingBoxAsync());
        }

        // Assert
        search.Width.ShouldBeGreaterThanOrEqualTo(MinimumSearchWidth);
        controls.ShouldAllBe(control =>
            control.X >= form.X - 1
            && control.X + control.Width <= form.X + form.Width + 1
            && control.Y >= form.Y - 1
            && control.Y + control.Height <= form.Y + form.Height + 1);
    }

    [Theory]
    [InlineData(1920)]
    [InlineData(1366)]
    public async Task On_a_wide_screen_search_match_status_and_the_filter_button_share_one_row(int width)
    {
        // Arrange
        await OpenTranslationsAsync(width);

        // Act: the row lines its items up at the bottom; the button is a little shorter than a select.
        LocatorBoundingBoxResult search = await SearchBox().RequireBoundingBoxAsync();
        LocatorBoundingBoxResult match = await MatchSelect().RequireBoundingBoxAsync();
        LocatorBoundingBoxResult status = await StatusSelect().RequireBoundingBoxAsync();
        LocatorBoundingBoxResult filter = await FilterButton().RequireBoundingBoxAsync();

        // Assert
        float searchBottom = search.Y + search.Height;
        (match.Y + match.Height).ShouldBe(searchBottom, 1d);
        (status.Y + status.Height).ShouldBe(searchBottom, 1d);
        (filter.Y + filter.Height).ShouldBe(searchBottom, 1d);
    }

    [Fact]
    public async Task The_match_and_page_size_selects_keep_their_own_width_on_every_wide_screen()
    {
        // Arrange: only search and status grow with the window.
        await OpenTranslationsAsync(1920);
        LocatorBoundingBoxResult wideMatch = await MatchSelect().RequireBoundingBoxAsync();
        LocatorBoundingBoxResult widePageSize = await PageSizeSelect().RequireBoundingBoxAsync();

        // Act
        await OpenTranslationsAsync(1366);
        LocatorBoundingBoxResult narrowMatch = await MatchSelect().RequireBoundingBoxAsync();
        LocatorBoundingBoxResult narrowPageSize = await PageSizeSelect().RequireBoundingBoxAsync();

        // Assert
        narrowMatch.Width.ShouldBe(wideMatch.Width, 1d);
        narrowPageSize.Width.ShouldBe(widePageSize.Width, 1d);
    }

    public TranslationsFilterLayoutTests(PlaywrightStackFixture fixture) : base(fixture)
    {
    }

    private ILocator SearchBox() => Page.GetByLabel("Wyszukaj", new() { Exact = true });

    private ILocator MatchSelect() => Page.GetByLabel("Dopasowanie", new() { Exact = true });

    private ILocator StatusSelect() => Page.GetByLabel("Status", new() { Exact = true });

    private ILocator PageSizeSelect() => Page.GetByLabel("Na stronie", new() { Exact = true });

    private ILocator FilterButton() => Page.GetByRole(AriaRole.Button, new() { Name = "Filtruj", Exact = true });

    /// <summary>
    /// The checkbox itself is hidden under a styled box, so its label stands for it.
    /// </summary>
    private IEnumerable<ILocator> Controls() =>
    [
        SearchBox(),
        MatchSelect(),
        StatusSelect(),
        PageSizeSelect(),
        Page.Locator("form.filter .field-conflicts label.checkbox"),
        Page.GetByRole(AriaRole.Link, new() { Name = "Wyczyść", Exact = true }),
        FilterButton()
    ];

    private async Task OpenTranslationsAsync(int width)
    {
        await Page.SetViewportSizeAsync(width, 900);
        await Page.GotoAsync($"{Fixture.FrontendBaseUrl}/translations");
        await Page.WaitForFontsAsync();
    }
}
