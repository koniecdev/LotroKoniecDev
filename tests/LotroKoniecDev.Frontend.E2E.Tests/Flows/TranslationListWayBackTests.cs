using System.Collections.Specialized;
using System.Globalization;
using System.Text;
using System.Web;
using LotroKoniecDev.Frontend.E2E.Tests.Infrastructure;
using Microsoft.Playwright;
using Shouldly;

namespace LotroKoniecDev.Frontend.E2E.Tests.Flows;

/// <summary>
/// #1452: a translator works through one search row by row, so the editor's way back must show the same
/// list. The flow seeds its own rows through the site as the admin: a game version and an import of
/// <c>exported.txt</c>, whose English carries a token no other flow uses.
/// </summary>
public sealed class TranslationListWayBackTests : E2ETestBase
{
    private const string GameVersion = "48";
    private const int FileId = 620_756_992;

    /// <summary>
    /// Two pages of 25, and the second still has rows after the save moves one row out of the status filter.
    /// </summary>
    private const int RowCount = 30;

    private const string Untranslated = "Untranslated";
    private const string PageSize = "25";
    private static readonly LocatorWaitForOptions LongWait = new() { Timeout = 30_000 };

    [Fact]
    public async Task Search_status_and_page_survive_edit_save_and_the_way_back()
    {
        // Arrange: the admin seeds the rows, then filters the list and opens its second page.
        string token = $"waybk{Guid.NewGuid():N}"[..13];
        await AuthActions.LoginAsync(Page, Fixture, Fixture.Admin);
        await RegisterGameVersionAsync();
        await ImportRowsAsync(token);

        await Page.GotoAsync($"{Fixture.FrontendBaseUrl}/translations");
        await Page.GetByLabel("Wyszukaj", new() { Exact = true }).FillAsync(token);
        await Page.GetByLabel("Status", new() { Exact = true }).SelectOptionAsync(Untranslated);
        await Page.GetByLabel("Na stronie", new() { Exact = true }).SelectOptionAsync(PageSize);
        await Page.GetByRole(AriaRole.Button, new() { Name = "Filtruj", Exact = true }).ClickAsync();
        await Page.GetByText("Strona 1 z 2", new() { Exact = true }).WaitForAsync(LongWait);
        await Page.Locator("nav.pager a[rel=next]").ClickAsync();
        await Page.GetByText("Strona 2 z 2", new() { Exact = true }).WaitForAsync(LongWait);

        // Act: edit a row of that page, save, and go back.
        await Page.GetByRole(AriaRole.Link, new() { Name = "Edytuj", Exact = true }).First.ClickAsync();
        ILocator translation = Page.GetByLabel("Tłumaczenie polskie", new() { Exact = true });
        await translation.WaitForAsync(LongWait);
        await translation.FillAsync("Wróciłem do tej samej listy.");
        await Page.GetByRole(AriaRole.Button, new() { Name = "Zapisz", Exact = true }).ClickAsync();
        await Page.GetByText("Tłumaczenie zapisano.", new() { Exact = true }).WaitForAsync(LongWait);
        NameValueCollection savedQuery = QueryOf(Page.Url);
        await Page.GetByRole(AriaRole.Link, new() { Name = "Wróć do listy" }).ClickAsync();
        await Page.WaitForURLAsync(url => new Uri(url).AbsolutePath == "/translations");
        await Page.GetByText("Strona 2 z 2", new() { Exact = true }).WaitForAsync(LongWait);

        // Assert: the redirect after the save kept the state, and the list shows the same filters and page.
        savedQuery["search"].ShouldBe(token);
        savedQuery["saved"].ShouldBe("true");

        NameValueCollection listQuery = QueryOf(Page.Url);
        listQuery["search"].ShouldBe(token);
        listQuery["status"].ShouldBe(Untranslated);
        listQuery["page"].ShouldBe("2");
        listQuery["pageSize"].ShouldBe(PageSize);
        listQuery["saved"].ShouldBeNull();
        (await Page.GetByLabel("Wyszukaj", new() { Exact = true }).InputValueAsync()).ShouldBe(token);
        (await Page.GetByLabel("Status", new() { Exact = true }).InputValueAsync()).ShouldBe(Untranslated);
        (await Page.GetByLabel("Na stronie", new() { Exact = true }).InputValueAsync()).ShouldBe(PageSize);
    }

    public TranslationListWayBackTests(PlaywrightStackFixture fixture) : base(fixture)
    {
    }

    private static NameValueCollection QueryOf(string url) =>
        HttpUtility.ParseQueryString(new Uri(url).Query);

    private async Task RegisterGameVersionAsync()
    {
        await Page.GotoAsync($"{Fixture.FrontendBaseUrl}/game-versions");
        await Page.GetByLabel("Wersja", new() { Exact = true }).FillAsync(GameVersion);
        await Page.GetByRole(AriaRole.Button, new() { Name = "Zarejestruj", Exact = true }).ClickAsync();
        await Page.GetByText($"Zarejestrowano wersję {GameVersion}.", new() { Exact = true }).WaitForAsync(LongWait);
    }

    private async Task ImportRowsAsync(string token)
    {
        string exported = string.Join('\n', Enumerable.Range(1, RowCount).Select(gossipId =>
            string.Create(CultureInfo.InvariantCulture, $"{FileId}||{gossipId}||Row {gossipId:00} of {token}||NULL||NULL||1")));

        await Page.GotoAsync($"{Fixture.FrontendBaseUrl}/import-export");
        await Page.GetByLabel("Plik exported.txt", new() { Exact = true }).SetInputFilesAsync(new FilePayload
        {
            Name = "exported.txt",
            MimeType = "text/plain",
            Buffer = Encoding.UTF8.GetBytes(exported)
        });
        await Page.GetByRole(AriaRole.Button, new() { Name = "Importuj", Exact = true }).ClickAsync();
        await Page.GetByText("Import zakończony powodzeniem.", new() { Exact = true }).WaitForAsync(LongWait);
    }
}
