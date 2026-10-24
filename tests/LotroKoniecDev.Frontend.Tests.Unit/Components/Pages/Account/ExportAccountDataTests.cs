using AngleSharp.Dom;
using Bunit.TestDoubles;
using LotroKoniecDev.Frontend.Components.Pages.Account;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;

namespace LotroKoniecDev.Frontend.Tests.Unit.Components.Pages.Account;

/// <summary>
/// The page only posts the password to the download route and shows why the last attempt was refused;
/// the auth API checks the password (ADR-0052).
/// </summary>
public sealed class ExportAccountDataTests : BunitContext
{
    public ExportAccountDataTests()
    {
        // The form renders an <AntiforgeryToken/>, which needs the service behind it.
        Services.AddAntiforgery();
    }

    [Fact]
    public void Render_WithoutAnErrorMarker_PostsThePasswordToTheDownloadRoute()
    {
        IRenderedComponent<ExportAccountData> component = Render<ExportAccountData>();

        IElement form = component.Find("[data-testid=export-form]");
        form.GetAttribute("method").ShouldBe("post");
        form.GetAttribute("action").ShouldBe("/account/export/download");

        IElement password = component.Find("#export-password");
        password.GetAttribute("type").ShouldBe("password");
        password.GetAttribute("name").ShouldBe("password");
        password.HasAttribute("required").ShouldBeTrue();
    }

    [Fact]
    public void Render_WithoutAnErrorMarker_CarriesAnAntiforgeryToken()
    {
        // The route demands the token, so without it every download fails with a 400. bUnit can only see
        // the component, not a real token.
        IRenderedComponent<ExportAccountData> component = Render<ExportAccountData>();

        component.FindComponents<AntiforgeryToken>().ShouldHaveSingleItem();
    }

    [Fact]
    public void Render_WithoutAnErrorMarker_ShowsNoErrorPanel()
    {
        IRenderedComponent<ExportAccountData> component = Render<ExportAccountData>();

        component.FindAll("[data-testid=export-error]").ShouldBeEmpty();
    }

    [Theory]
    [InlineData("password", "Hasło jest nieprawidłowe.")]
    [InlineData("required", "Podaj obecne hasło, aby pobrać swoje dane.")]
    [InlineData("throttled", "Serwer jest chwilowo zajęty. Odczekaj chwilę i spróbuj ponownie.")]
    [InlineData("password-throttled", "Zbyt wiele prób potwierdzenia hasła. Odczekaj kwadrans i spróbuj ponownie.")]
    public void Render_WithAnErrorMarker_ShowsTheMatchingPolishSentence(string errorCode, string expected)
    {
        Navigation().NavigateTo($"/account/export?error={errorCode}");

        IRenderedComponent<ExportAccountData> component = Render<ExportAccountData>();

        component.Find("[data-testid=export-error]").TextContent.Trim().ShouldBe(expected);
    }

    [Theory]
    [InlineData("password")]
    [InlineData("required")]
    [InlineData("throttled")]
    [InlineData("password-throttled")]
    public void Render_WithAnErrorMarker_OffersAWayBackInsteadOfTheForm(string errorCode)
    {
        // A download keeps the browser on this page, so a form next to the banner would hand the file
        // over under "wrong password".
        Navigation().NavigateTo($"/account/export?error={errorCode}");

        IRenderedComponent<ExportAccountData> component = Render<ExportAccountData>();

        component.FindAll("[data-testid=export-form]").ShouldBeEmpty();
        component.Find("[data-testid=export-retry]").GetAttribute("href").ShouldBe("/account/export");
    }

    [Fact]
    public void Render_WithoutAnErrorMarker_OffersNoRetryLink()
    {
        IRenderedComponent<ExportAccountData> component = Render<ExportAccountData>();

        component.FindAll("[data-testid=export-retry]").ShouldBeEmpty();
    }

    [Theory]
    [InlineData("nonsense")]
    [InlineData("session")]
    [InlineData("<script>alert(1)</script>")]
    public void Render_WithATamperedErrorMarker_ShowsNoErrorPanel(string errorCode)
    {
        // The marker is a switch over known codes, so a hand-edited URL cannot put its own text here.
        Navigation().NavigateTo($"/account/export?error={Uri.EscapeDataString(errorCode)}");

        IRenderedComponent<ExportAccountData> component = Render<ExportAccountData>();

        component.FindAll("[data-testid=export-error]").ShouldBeEmpty();
    }

    private BunitNavigationManager Navigation() =>
        (BunitNavigationManager)Services.GetRequiredService<NavigationManager>();
}
