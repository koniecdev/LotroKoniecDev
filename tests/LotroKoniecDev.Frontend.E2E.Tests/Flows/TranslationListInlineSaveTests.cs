using System.Collections.Specialized;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Web;
using LotroKoniecDev.Frontend.E2E.Tests.Infrastructure;
using Microsoft.Playwright;
using Shouldly;

namespace LotroKoniecDev.Frontend.E2E.Tests.Flows;

/// <summary>
/// #1493: a translator saves a short text from the list's own row form and stays on the same list. The page
/// has no script of its own, so only the browser shows where it lands: the address's #row fragment scrolls
/// to the row, clear of the sticky navbar. Each flow works on a row below the fold of a 25-row page, so a
/// page that did not scroll fails. The rows come from the shared seed (<see cref="TranslationRowsSeed"/>);
/// the flows touch different rows, so their order does not matter.
/// </summary>
public sealed class TranslationListInlineSaveTests : E2ETestBase
{
    private const string PageSize = "25";
    private const string Untranslated = "Untranslated";
    private static readonly LocatorWaitForOptions LongWait = new() { Timeout = 30_000 };

    [Fact]
    public async Task Save_in_place_brings_the_same_list_back_at_the_saved_row()
    {
        // Arrange
        await OpenListAsync(status: null);

        // Act
        await SaveInPlaceAsync(RowWithGossipId(20), gossipId: 20, "Zapisane w miejscu.");

        // Assert
        string savedRow = await WaitForTheSaveAsync();
        NameValueCollection query = QueryOf(Page.Url);
        query["search"].ShouldBe(Fixture.TranslationRows.InlineSaveToken);
        query["pageSize"].ShouldBe(PageSize);
        query["page"].ShouldBe("1");
        new Uri(Page.Url).Fragment.ShouldBe($"#row-{savedRow}");
        ILocator row = Page.Locator($"tr#row-{savedRow}");
        string rowText = await row.InnerTextAsync();
        rowText.ShouldContain("Row 20 of");
        rowText.ShouldContain("Zapisane w miejscu.");
        (await row.Locator("xpath=following-sibling::tr[1]").InnerTextAsync()).ShouldContain("Tłumaczenie zapisano.");
        ScrollPlace place = await ScrollPlaceOfAsync(row);
        place.ScrollY.ShouldBeGreaterThan(0);
        place.TargetTop.ShouldBeGreaterThanOrEqualTo(place.NavbarBottom - 1);
        place.TargetTop.ShouldBeLessThan(place.ViewportHeight);
    }

    [Fact]
    public async Task A_refused_save_keeps_the_typed_text_and_the_error_in_the_open_row()
    {
        // Arrange: this row's English has a placeholder, and the typed Polish has none (#1437).
        int gossipId = TranslationRowsSeed.InlineSavePlaceholderGossipId;
        await OpenListAsync(status: null);

        // Act
        await SaveInPlaceAsync(RowWithGossipId(gossipId), gossipId, "Bez znacznika.");

        // Assert
        await Page.GetByText("Tłumaczenie musi mieć tyle samo znaczników", new() { Exact = false }).WaitForAsync(LongWait);
        ILocator row = RowWithGossipId(gossipId);
        (await row.Locator("details.inline-edit").GetAttributeAsync("open")).ShouldNotBeNull();
        (await TextBoxOf(row, gossipId).InputValueAsync()).ShouldBe("Bez znacznika.");
        new Uri(Page.Url).Fragment.ShouldBe($"#{await row.GetAttributeAsync("id")}");
        ScrollPlace place = await ScrollPlaceOfAsync(row);
        place.ScrollY.ShouldBeGreaterThan(0);
        place.TargetTop.ShouldBeGreaterThanOrEqualTo(place.NavbarBottom - 1);
        place.TargetTop.ShouldBeLessThan(place.ViewportHeight);
    }

    [Fact]
    public async Task A_row_the_save_takes_out_of_the_filter_leaves_a_note_in_its_place()
    {
        // Arrange: a save makes the row a draft, so the "Nieprzetłumaczone" filter drops it. A narrow window,
        // where the navbar's links sit on their own row under the logo.
        await Page.SetViewportSizeAsync(860, 900);
        await OpenListAsync(status: Untranslated);

        // Act
        await SaveInPlaceAsync(RowWithGossipId(18), gossipId: 18, "Już nie czeka.");

        // Assert
        string savedRow = await WaitForTheSaveAsync();
        QueryOf(Page.Url)["status"].ShouldBe(Untranslated);
        (await RowWithGossipId(18).CountAsync()).ShouldBe(0);
        ILocator note = Page.Locator($"tr.row-note#row-{savedRow}");
        (await note.InnerTextAsync()).ShouldContain("Tego wiersza nie ma już na tej stronie listy.");
        (await note.Locator("xpath=preceding-sibling::tr[1]").InnerTextAsync()).ShouldContain("Row 17 of");
        ScrollPlace place = await ScrollPlaceOfAsync(note);
        place.ScrollY.ShouldBeGreaterThan(0);
        place.TargetTop.ShouldBeGreaterThanOrEqualTo(place.NavbarBottom - 1);
        place.TargetTop.ShouldBeLessThan(place.ViewportHeight);
    }

    [Fact]
    public async Task Bulk_approve_still_approves_a_ticked_row_now_that_the_table_is_outside_its_form()
    {
        // Arrange: a saved row is a draft, so the admin can tick it.
        await OpenListAsync(status: null);
        await SaveInPlaceAsync(RowWithGossipId(25), gossipId: 25, "Do zatwierdzenia.");
        string savedRow = await WaitForTheSaveAsync();

        // Act
        await Page.Locator($"tr#row-{savedRow}").GetByRole(AriaRole.Checkbox).CheckAsync();
        await Page.GetByRole(AriaRole.Button, new() { Name = "Zatwierdź zaznaczone", Exact = true }).ClickAsync();

        // Assert
        await Page.GetByText("Zatwierdzono 1 tłumaczenie.", new() { Exact = false }).WaitForAsync(LongWait);
        (await Page.Locator($"tr#row-{savedRow} td.col-status").InnerTextAsync()).ShouldContain("Zatwierdzone");
    }

    public TranslationListInlineSaveTests(PlaywrightStackFixture fixture) : base(fixture)
    {
    }

    private static NameValueCollection QueryOf(string url) =>
        HttpUtility.ParseQueryString(new Uri(url).Query);

    private async Task OpenListAsync(string? status)
    {
        await AuthActions.LoginAsync(Page, Fixture, Fixture.Admin);
        await Fixture.TranslationRows.EnsureSeededAsync(Page, Fixture.FrontendBaseUrl);

        await Page.GotoAsync($"{Fixture.FrontendBaseUrl}/translations");
        await Page.GetByLabel("Wyszukaj", new() { Exact = true }).FillAsync(Fixture.TranslationRows.InlineSaveToken);
        if (status is not null)
        {
            await Page.GetByLabel("Status", new() { Exact = true }).SelectOptionAsync(status);
        }

        await Page.GetByLabel("Na stronie", new() { Exact = true }).SelectOptionAsync(PageSize);
        await Page.GetByRole(AriaRole.Button, new() { Name = "Filtruj", Exact = true }).ClickAsync();
        await Page.GetByText("Strona 1 z 2", new() { Exact = true }).WaitForAsync(LongWait);
    }

    /// <summary>The row by its GossipId cell, so a row another flow moved cannot be taken for it.</summary>
    private ILocator RowWithGossipId(int gossipId) =>
        Page.Locator("table.data-table tbody tr:not(.row-note)").Filter(new LocatorFilterOptions
        {
            Has = Page.Locator("td.col-num", new PageLocatorOptions
            {
                HasTextRegex = new Regex(string.Create(CultureInfo.InvariantCulture, $"^{gossipId}$"))
            })
        });

    private static ILocator TextBoxOf(ILocator row, int gossipId) =>
        row.GetByLabel(
            string.Create(
                CultureInfo.InvariantCulture,
                $"Tłumaczenie polskie, FileId {TranslationRowsSeed.InlineSaveFileId}, GossipId {gossipId}"),
            new() { Exact = true });

    private static async Task SaveInPlaceAsync(ILocator row, int gossipId, string text)
    {
        await row.GetByText("Edytuj tutaj", new() { Exact = true }).ClickAsync();
        await TextBoxOf(row, gossipId).FillAsync(text);
        await row.GetByRole(AriaRole.Button, new() { Name = "Zapisz", Exact = true }).ClickAsync();
    }

    /// <summary>The redirect after a good save names the saved row; the confirmation shows once the list is in.</summary>
    private async Task<string> WaitForTheSaveAsync()
    {
        await Page.WaitForURLAsync(url => url.Contains("savedRow=", StringComparison.Ordinal), new() { Timeout = 30_000 });
        await Page.GetByText("Tłumaczenie zapisano.", new() { Exact = false }).First.WaitForAsync(LongWait);
        return QueryOf(Page.Url)["savedRow"].ShouldNotBeNull();
    }

    /// <summary>Where the page stands once it has loaded, so each test can check that it scrolled to the target.</summary>
    private async Task<ScrollPlace> ScrollPlaceOfAsync(ILocator target)
    {
        await Page.WaitForLoadStateAsync(LoadState.Load);
        double scrollY = await Page.EvaluateAsync<double>("() => window.scrollY");
        LocatorBoundingBoxResult box = await target.RequireBoundingBoxAsync();
        LocatorBoundingBoxResult navbar = await Page.Locator("header.nav").RequireBoundingBoxAsync();
        int viewportHeight = Page.ViewportSize.ShouldNotBeNull().Height;
        return new ScrollPlace(scrollY, box.Y, navbar.Y + navbar.Height, viewportHeight);
    }

    /// <summary>All in CSS pixels; the two tops are relative to the window.</summary>
    private sealed record ScrollPlace(double ScrollY, double TargetTop, double NavbarBottom, double ViewportHeight);
}
