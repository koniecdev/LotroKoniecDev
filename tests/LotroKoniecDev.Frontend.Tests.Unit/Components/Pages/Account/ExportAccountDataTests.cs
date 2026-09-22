using AngleSharp.Dom;
using Bunit.TestDoubles;
using LotroKoniecDev.Frontend.Components.Pages.Account;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;

namespace LotroKoniecDev.Frontend.Tests.Unit.Components.Pages.Account;

/// <summary>
/// The page that asks for the password before the GDPR export is handed over (#690, ADR-0052). It only
/// has to post the password to the download route and show why the last attempt was refused; the check
/// itself happens at the auth API.
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
        // An empty submit never leaves the browser. The server-side refusal is only the safety net.
        password.HasAttribute("required").ShouldBeTrue();
    }

    [Fact]
    public void Render_WithoutAnErrorMarker_CarriesAnAntiforgeryToken()
    {
        // The form target binds a form field, so the framework demands the token. Without it every
        // download would fail with a 400. bUnit has no real token to render, so the component itself is
        // what the test can see.
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
    [InlineData("throttled", "Zbyt wiele prób. Odczekaj chwilę i spróbuj ponownie.")]
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
    public void Render_WithAnErrorMarker_OffersAWayBackInsteadOfTheForm(string errorCode)
    {
        // A successful download answers with a file, so the browser stays on the page it posted from.
        // A form next to the banner would therefore hand the file over under "wrong password". The
        // refused state has no form at all, only a link back to a clean one.
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
        // The marker is a switch over known codes, never text to print, so a hand-edited URL cannot put
        // anything of its own on the page.
        Navigation().NavigateTo($"/account/export?error={Uri.EscapeDataString(errorCode)}");

        IRenderedComponent<ExportAccountData> component = Render<ExportAccountData>();

        component.FindAll("[data-testid=export-error]").ShouldBeEmpty();
    }

    private BunitNavigationManager Navigation() =>
        (BunitNavigationManager)Services.GetRequiredService<NavigationManager>();
}
