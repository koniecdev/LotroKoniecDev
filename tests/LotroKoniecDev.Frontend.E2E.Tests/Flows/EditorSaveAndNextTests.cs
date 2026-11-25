using System.Collections.Specialized;
using System.Web;
using LotroKoniecDev.Frontend.E2E.Tests.Infrastructure;
using Microsoft.Playwright;
using Shouldly;

namespace LotroKoniecDev.Frontend.E2E.Tests.Flows;

/// <summary>
/// #1490: "Zapisz i następny" in a real browser, which alone posts the pressed button's value and the
/// textarea's text the way the page tests assume. The rows come from the shared seed
/// (<see cref="TranslationRowsSeed"/>); the flows touch different rows, so their order does not matter.
/// The seeded English ends with no full stop, so the typed Polish has none either: a slip would keep the
/// translator on the row (#1489).
/// </summary>
public sealed class EditorSaveAndNextTests : E2ETestBase
{
    private const string PageSize = "25";
    private const string SaveAndNext = "Zapisz i następny";
    private static readonly LocatorWaitForOptions LongWait = new() { Timeout = 30_000 };

    [Fact]
    public async Task Save_and_next_walks_an_untranslated_search_row_by_row()
    {
        // Arrange: each save makes the row a draft, so it leaves "Nieprzetłumaczone" (spec 0018).
        await OpenListAsync(Fixture.TranslationRows.SaveAndNextToken, status: "Untranslated");
        await Page.GetByRole(AriaRole.Link, new() { Name = "Edytuj", Exact = true }).First.ClickAsync();
        await WaitForTheEnglishOfRowAsync(1);

        // Act & Assert: three presses, each opens the row that came next in this list.
        foreach (int nextRow in new[] { 2, 3, 4 })
        {
            await Page.GetByLabel("Tłumaczenie polskie", new() { Exact = true }).FillAsync($"Wiersz {nextRow - 1}");
            await Page.GetByRole(AriaRole.Button, new() { Name = SaveAndNext, Exact = true }).ClickAsync();
            await WaitForTheEnglishOfRowAsync(nextRow);
            (await Page.GetByText("Poprzednie tłumaczenie zapisano.", new() { Exact = true }).IsVisibleAsync()).ShouldBeTrue();
            NameValueCollection query = QueryOf(Page.Url);
            query["search"].ShouldBe(Fixture.TranslationRows.SaveAndNextToken);
            query["status"].ShouldBe("Untranslated");
            query["place"].ShouldNotBeNullOrEmpty();
        }
    }

    [Fact]
    public async Task Save_and_next_on_the_last_row_of_a_page_opens_the_first_row_of_the_next_page()
    {
        // Arrange: no status filter, so the saved row stays where it is; row 25 is the last of page 1.
        await OpenListAsync(Fixture.TranslationRows.SaveAndNextToken, status: null);
        await Page.GetByRole(AriaRole.Link, new() { Name = "Edytuj", Exact = true }).Nth(24).ClickAsync();
        await WaitForTheEnglishOfRowAsync(25);

        // Act
        await Page.GetByLabel("Tłumaczenie polskie", new() { Exact = true }).FillAsync("Ostatni na stronie");
        await Page.GetByRole(AriaRole.Button, new() { Name = SaveAndNext, Exact = true }).ClickAsync();
        await WaitForTheEnglishOfRowAsync(26);
        (await Page.GetByText("Poprzednie tłumaczenie zapisano.", new() { Exact = true }).IsVisibleAsync()).ShouldBeTrue();
        await Page.GetByRole(AriaRole.Link, new() { Name = "Wróć do listy" }).ClickAsync();
        await Page.WaitForURLAsync(url => new Uri(url).AbsolutePath == "/translations");

        // Assert
        await Page.GetByText("Strona 2 z 2", new() { Exact = true }).WaitForAsync(LongWait);
        QueryOf(Page.Url)["page"].ShouldBe("2");
    }

    [Fact]
    public async Task An_approved_row_passed_with_no_edit_stays_approved()
    {
        // Arrange: a text that starts with a line break, which a textarea drops unless the page writes one
        // before it (spec 0018). The search finds only this row, so the press ends on the list.
        await OpenListAsync($"Row 30 of {Fixture.TranslationRows.SaveAndNextToken}", status: null);
        await Page.GetByRole(AriaRole.Link, new() { Name = "Edytuj", Exact = true }).ClickAsync();
        await WaitForTheEnglishOfRowAsync(30);
        await Page.GetByLabel("Tłumaczenie polskie", new() { Exact = true }).FillAsync("\nLinia pierwsza\nLinia druga");
        await Page.GetByRole(AriaRole.Button, new() { Name = "Zapisz", Exact = true }).ClickAsync();
        await Page.GetByText("Tłumaczenie zapisano.", new() { Exact = true }).WaitForAsync(LongWait);
        await Page.GetByRole(AriaRole.Button, new() { Name = "Zatwierdź", Exact = true }).ClickAsync();
        await Page.GetByText("Tłumaczenie zatwierdzono i dołączono do dystrybucji.", new() { Exact = true })
            .WaitForAsync(LongWait);

        // Act
        await Page.GetByRole(AriaRole.Button, new() { Name = SaveAndNext, Exact = true }).ClickAsync();
        await Page.WaitForURLAsync(url => new Uri(url).AbsolutePath == "/translations");

        // Assert: nothing was saved, so the row is still approved and the note says only that the list ended.
        await Page.GetByText("To był ostatni wiersz tej listy.", new() { Exact = true }).WaitForAsync(LongWait);
        QueryOf(Page.Url)["lastRow"].ShouldBe("unchanged");
        (await Page.Locator("table.data-table tbody tr").First.InnerTextAsync()).ShouldContain("Zatwierdzone");
    }

    public EditorSaveAndNextTests(PlaywrightStackFixture fixture) : base(fixture)
    {
    }

    private static NameValueCollection QueryOf(string url) =>
        HttpUtility.ParseQueryString(new Uri(url).Query);

    private async Task OpenListAsync(string search, string? status)
    {
        await AuthActions.LoginAsync(Page, Fixture, Fixture.Admin);
        await Fixture.TranslationRows.EnsureSeededAsync(Page, Fixture.FrontendBaseUrl);

        await Page.GotoAsync($"{Fixture.FrontendBaseUrl}/translations");
        await Page.GetByLabel("Wyszukaj", new() { Exact = true }).FillAsync(search);
        if (status is not null)
        {
            await Page.GetByLabel("Status", new() { Exact = true }).SelectOptionAsync(status);
        }

        await Page.GetByLabel("Na stronie", new() { Exact = true }).SelectOptionAsync(PageSize);
        await Page.GetByRole(AriaRole.Button, new() { Name = "Filtruj", Exact = true }).ClickAsync();
        await Page.WaitForURLAsync(url => QueryOf(url)["search"] == search);
        await Page.GetByRole(AriaRole.Link, new() { Name = "Edytuj", Exact = true }).First.WaitForAsync(LongWait);
    }

    /// <summary>
    /// Waits for the new page, not for a line the old page may still show: every row's English is its own.
    /// </summary>
    private Task WaitForTheEnglishOfRowAsync(int row) =>
        Page.Locator(".editor-source", new() { HasTextString = $"Row {row:00} of {Fixture.TranslationRows.SaveAndNextToken}" })
            .First
            .WaitForAsync(LongWait);
}
