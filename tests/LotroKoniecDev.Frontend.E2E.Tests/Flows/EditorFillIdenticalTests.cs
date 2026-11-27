using LotroKoniecDev.Frontend.E2E.Tests.Infrastructure;
using Microsoft.Playwright;
using Shouldly;

namespace LotroKoniecDev.Frontend.E2E.Tests.Flows;

/// <summary>
/// #1587: „Zapisz we wszystkich identycznych (N)” in a real browser, which alone posts the pressed button's
/// value. The seed's fill flow group has four untranslated rows with one English (<see cref="TranslationRowsSeed"/>),
/// and only this flow writes them.
/// </summary>
public sealed class EditorFillIdenticalTests : E2ETestBase
{
    private const string Polish = "Włócznia wojenna";
    private static readonly LocatorWaitForOptions LongWait = new() { Timeout = 30_000 };

    [Fact]
    public async Task Fill_saves_the_typed_text_in_this_row_and_in_every_empty_row_with_the_same_English()
    {
        // Arrange
        await AuthActions.LoginAsync(Page, Fixture, Fixture.Admin);
        await Fixture.TranslationRows.EnsureSeededAsync(Page, Fixture.FrontendBaseUrl);
        await Page.GotoAsync($"{Fixture.FrontendBaseUrl}/translations");
        await Page.GetByLabel("Wyszukaj", new() { Exact = true }).FillAsync(Fixture.TranslationRows.FillFlowToken);
        await Page.GetByRole(AriaRole.Button, new() { Name = "Filtruj", Exact = true }).ClickAsync();
        await Page.GetByRole(AriaRole.Link, new() { Name = "Edytuj", Exact = true }).First.ClickAsync();
        await Page.GetByLabel("Tłumaczenie polskie", new() { Exact = true }).WaitForAsync(LongWait);

        // Act
        await Page.GetByLabel("Tłumaczenie polskie", new() { Exact = true }).FillAsync(Polish);
        await Page.GetByRole(AriaRole.Button, new() { Name = $"Zapisz we wszystkich identycznych ({TranslationRowsSeed.FillGroupRows - 1})", Exact = true })
            .ClickAsync();

        // Assert: the panel then shows the copies, and no empty row is left to offer.
        await Page.GetByText(
                $"Tłumaczenie zapisano. Ten sam tekst wpisano też w {TranslationRowsSeed.FillGroupRows - 1} pustych wierszach z tym samym angielskim tekstem.",
                new() { Exact = true })
            .WaitForAsync(LongWait);
        IReadOnlyList<string> panelRows = await Page.Locator(".same-source tbody tr").AllInnerTextsAsync();
        panelRows.Count.ShouldBe(TranslationRowsSeed.FillGroupRows - 1);
        panelRows.ShouldAllBe(row => row.Contains(Polish, StringComparison.Ordinal)
            && row.Contains("Wersja robocza", StringComparison.OrdinalIgnoreCase));
        (await Page.Locator("button[value='fill']").CountAsync()).ShouldBe(0);
    }

    public EditorFillIdenticalTests(PlaywrightStackFixture fixture) : base(fixture)
    {
    }
}
