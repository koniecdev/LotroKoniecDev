using AngleSharp.Dom;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Account;
using LotroKoniecDev.AuthSystem.Contracts.Hateoas;
using LotroKoniecDev.Frontend.Components.Pages.Account;
using LotroKoniecDev.Frontend.Components.Shared;
using LotroKoniecDev.Frontend.Infrastructure.Discovery;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients.AuthSystemHttpClients;
using LotroKoniecDev.Hateoas.Abstractions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using AuthDiscoveryResponse = LotroKoniecDev.AuthSystem.Contracts.Discovery.DiscoveryResponse;

namespace LotroKoniecDev.Frontend.Tests.Unit.Components.Pages.Account;

/// <summary>
/// Renders the <c>/account/export</c> page through bUnit over a stubbed auth client. The page asks for
/// the password and posts it to the download route (#690). It shows the form only when the account
/// advertises the <c>export-account-data</c> rel, and it turns the route's error code into Polish
/// without ever printing the raw query value.
/// </summary>
public sealed class ExportDataTests : BunitContext
{
    private readonly IDiscoveryCache _discoveryCache = Substitute.For<IDiscoveryCache>();
    private readonly IAuthSystemClient _client = Substitute.For<IAuthSystemClient>();

    public ExportDataTests()
    {
        Services.AddAntiforgery();
        Services.AddSingleton(_discoveryCache);
        Services.AddSingleton(_client);
        Services.AddScoped<AccountLoader>();
    }

    [Fact]
    public void Render_WhenTheExportRelIsAdvertised_ShowsAPasswordFormThatPostsToTheDownloadRoute()
    {
        StubAccount(WithExportRel());

        IRenderedComponent<ExportData> component = Render<ExportData>();

        IElement form = component.Find("[data-testid=export-form]");
        form.GetAttribute("method").ShouldBe("post");
        form.GetAttribute("action").ShouldBe("/account/export/download");
        IElement password = component.Find("#export-password");
        password.GetAttribute("name").ShouldBe("password");
        password.GetAttribute("type").ShouldBe("password");
        component.FindAll(".error-message").ShouldBeEmpty();
    }

    [Fact]
    public void Render_WhenTheExportRelIsNotAdvertised_ShowsNoForm()
    {
        StubAccount(AccountLoaderTests.CreateEnvelope(links: []));

        IRenderedComponent<ExportData> component = Render<ExportData>();

        component.Markup.ShouldContain("niedostępne");
        component.FindAll("input[type=password]").ShouldBeEmpty();
    }

    [Theory]
    [InlineData("password-required", "Hasło jest wymagane")]
    [InlineData("invalid-password", "Nieprawidłowe hasło")]
    [InlineData("too-many-requests", "Zbyt wiele prób")]
    [InlineData("unavailable", "Nie udało się pobrać danych konta")]
    [InlineData("failed", "Nie udało się pobrać danych konta")]
    public void Render_WhenTheDownloadRouteSentAnErrorCode_ShowsItsPolishMessageAboveTheForm(
        string error, string expectedHeadline)
    {
        StubAccount(WithExportRel());
        Navigation().NavigateTo($"/account/export?error={error}");

        IRenderedComponent<ExportData> component = Render<ExportData>();

        component.Find(".error-message").TextContent.ShouldContain(expectedHeadline);
        component.FindAll("[data-testid=export-form]").ShouldHaveSingleItem();
    }

    [Fact]
    public void Render_WhenTheErrorCodeIsNotOneOfOurs_ShowsTheGeneralMessageAndNeverTheTypedValue()
    {
        StubAccount(WithExportRel());
        Navigation().NavigateTo("/account/export?error=Twoje-konto-zostalo-przejete");

        IRenderedComponent<ExportData> component = Render<ExportData>();

        component.Find(".error-message").TextContent.ShouldContain("Nie udało się pobrać danych konta");
        component.Markup.ShouldNotContain("Twoje-konto-zostalo-przejete");
    }

    [Fact]
    public void Render_WhenTheLoadIsUnauthorized_RedirectsToLoginInsteadOfTheErrorPanel()
    {
        _discoveryCache.GetAuthSystemDiscoveryAsync(Arg.Any<CancellationToken>())
            .Returns(ApiResult.Success(new AuthDiscoveryResponse("LotroKoniecDev.AuthSystem")
            {
                Links = [new LinkDto("auth/account", Rels.Account, "GET")]
            }));
        _client.GetApiResultAsync<AccountResponse>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ApiResult.Failure<AccountResponse>(
                new Microsoft.AspNetCore.Mvc.ProblemDetails { Title = "Unauthorized", Status = 401 }));

        IRenderedComponent<ExportData> component = Render<ExportData>();

        component.FindComponents<RedirectToLogin>().ShouldHaveSingleItem();
        component.FindAll("input[type=password]").ShouldBeEmpty();
    }

    private static AccountResponse WithExportRel() => AccountLoaderTests.CreateEnvelope(links:
    [
        new LinkDto("auth/account/data-export", Rels.ExportAccountData, "POST")
    ]);

    private NavigationManager Navigation() => Services.GetRequiredService<NavigationManager>();

    private void StubAccount(AccountResponse envelope)
    {
        AuthDiscoveryResponse discovery = new("LotroKoniecDev.AuthSystem")
        {
            Links = [new LinkDto("auth/account", Rels.Account, "GET")]
        };
        _discoveryCache.GetAuthSystemDiscoveryAsync(Arg.Any<CancellationToken>())
            .Returns(ApiResult.Success(discovery));
        _client.GetApiResultAsync<AccountResponse>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ApiResult.Success(envelope));
    }
}
