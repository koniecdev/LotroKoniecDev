using System.Net;
using AngleSharp.Dom;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Account;
using LotroKoniecDev.AuthSystem.Contracts.Hateoas;
using LotroKoniecDev.Frontend.Components.Pages.Account;
using LotroKoniecDev.Frontend.Components.Shared;
using LotroKoniecDev.Frontend.Infrastructure.Discovery;
using LotroKoniecDev.Frontend.Infrastructure.Errors;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients.AuthSystemHttpClients;
using LotroKoniecDev.Hateoas.Abstractions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using AccountComponent = LotroKoniecDev.Frontend.Components.Pages.Account.Account;
using AuthDiscoveryResponse = LotroKoniecDev.AuthSystem.Contracts.Discovery.DiscoveryResponse;

namespace LotroKoniecDev.Frontend.Tests.Unit.Components.Pages.Account;

/// <summary>
/// Pins what the loader tests cannot reach: the account data in the markup, and the action rows that
/// follow the API's links, never a role check.
/// </summary>
public sealed class AccountTests : BunitContext
{
    private readonly IDiscoveryCache _discoveryCache = Substitute.For<IDiscoveryCache>();
    private readonly IAuthSystemClient _client = Substitute.For<IAuthSystemClient>();

    public AccountTests()
    {
        Services.AddSingleton(_discoveryCache);
        Services.AddSingleton(_client);
        Services.AddScoped<AccountLoader>();
    }

    [Fact]
    public void Render_WhenExportLoads_ShowsUsernameEmailRolesAndConsentDates()
    {
        StubExport(AccountLoaderTests.CreateEnvelope());

        IRenderedComponent<AccountComponent> component = Render<AccountComponent>();

        component.Markup.ShouldContain("frodo");
        component.Markup.ShouldContain("frodo@shire.me");
        component.Markup.ShouldContain("Tłumacz");
        // #736: 12:00 UTC is 14:00 Poland time in summer.
        component.Markup.ShouldContain("2026-06-01 14:00 czasu polskiego");
        component.Markup.ShouldContain("Regulamin serwisu");
        component.Markup.ShouldContain("Zaakceptowany");
    }

    [Fact]
    public void Render_WhenTermsNotAccepted_ShowsTheGrandfatheredConsentState()
    {
        // Accounts older than the terms keep their old status (spec 0010), so the row must never pretend
        // they accepted.
        StubExport(AccountLoaderTests.CreateEnvelope(termsOfServiceAccepted: false));

        IRenderedComponent<AccountComponent> component = Render<AccountComponent>();

        component.Markup.ShouldContain("Regulamin serwisu");
        component.Markup.ShouldContain("Brak akceptacji");
    }

    [Fact]
    public void Render_WhenAnOlderAccountGaveTheDataProcessingConsent_ShowsNoRowForIt()
    {
        // The account runs on the contract, so this consent is never shown as given or missing (#1073).
        StubExport(AccountLoaderTests.CreateEnvelope());

        IRenderedComponent<AccountComponent> component = Render<AccountComponent>();

        component.Markup.ShouldNotContain("Przetwarzanie danych");
        component.Markup.ShouldNotContain("Wyrażona");
    }

    [Fact]
    public void Render_WhenEnvelopeAdvertisesTheDownload_LinksToThePasswordConfirmationPage()
    {
        // The export asks for the password first (#690), so the row points at the confirmation page.
        StubExport(AccountLoaderTests.CreateEnvelope(links:
        [
            new LinkDto("auth/account/data-export", Rels.DownloadAccountData, "POST")
        ]));

        IRenderedComponent<AccountComponent> component = Render<AccountComponent>();

        IElement download = component.Find("[data-testid=account-export]");
        download.GetAttribute("href").ShouldBe("/account/export");
        download.HasAttribute("download").ShouldBeFalse();
    }

    [Fact]
    public void Render_WhenEnvelopeAdvertisesChangePasswordAndDelete_ShowsBothActionRows()
    {
        StubExport(AccountLoaderTests.CreateEnvelope(links:
        [
            new LinkDto("auth/change-password", Rels.ChangePassword, "POST"),
            new LinkDto("auth/account/delete", Rels.DeleteAccount, "POST")
        ]));

        IRenderedComponent<AccountComponent> component = Render<AccountComponent>();

        component.Find("[data-testid=account-change-password]")
            .GetAttribute("href").ShouldBe("/account/change-password");
        component.Find("[data-testid=account-delete]")
            .GetAttribute("href").ShouldBe("/account/delete");
    }

    [Fact]
    public void Render_WhenEnvelopeAdvertisesChangeEmail_ShowsTheActionRowAndTheInlineLink()
    {
        StubExport(AccountLoaderTests.CreateEnvelope(links:
        [
            new LinkDto("auth/account/change-email", Rels.ChangeEmail, "POST")
        ]));

        IRenderedComponent<AccountComponent> component = Render<AccountComponent>();

        component.Find("[data-testid=account-change-email]")
            .GetAttribute("href").ShouldBe("/account/change-email");
        component.Find("[data-testid=account-change-email-inline]")
            .GetAttribute("href").ShouldBe("/account/change-email");
    }

    [Fact]
    public void Render_WhenEnvelopeAdvertisesNoActionRels_HidesTheGatedActionRows()
    {
        StubExport(AccountLoaderTests.CreateEnvelope(links: []));

        IRenderedComponent<AccountComponent> component = Render<AccountComponent>();

        component.FindAll("[data-testid=account-change-password]").ShouldBeEmpty();
        component.FindAll("[data-testid=account-change-email]").ShouldBeEmpty();
        component.FindAll("[data-testid=account-change-email-inline]").ShouldBeEmpty();
        component.FindAll("[data-testid=account-delete]").ShouldBeEmpty();
        component.FindAll("[data-testid=account-export]").ShouldBeEmpty();
    }

    [Fact]
    public void Render_WhenDeletionScheduled_ShowsTheScheduledNotice()
    {
        StubExport(AccountLoaderTests.CreateEnvelope(
            deletionScheduledAt: new DateTimeOffset(2026, 7, 11, 8, 0, 0, TimeSpan.Zero)));

        IRenderedComponent<AccountComponent> component = Render<AccountComponent>();

        component.Markup.ShouldContain("Usunięcie tego konta zostało zaplanowane");
        component.Markup.ShouldContain("2026-07-11 10:00 czasu polskiego");
    }

    [Fact]
    public void Render_WhenLoadFails_ShowsTheProblemTitle()
    {
        AuthDiscoveryResponse discovery = new("LotroKoniecDev.AuthSystem")
        {
            Links = [new LinkDto("auth/account/data-export", Rels.ExportAccountData, "GET")]
        };
        _discoveryCache.GetAuthSystemDiscoveryAsync(Arg.Any<CancellationToken>())
            .Returns(ApiResult.Success(discovery));
        _client.GetApiResultAsync<AccountDataExportResponse>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            // The shape HttpClientApiExtensions produces for a transport error: already Polish.
            .Returns(ApiResult.Failure<AccountDataExportResponse>(ApiProblemCopy.FrontendAuthored(
                "Nie udało się wczytać danych konta",
                status: (int)HttpStatusCode.BadGateway)));

        IRenderedComponent<AccountComponent> component = Render<AccountComponent>();

        component.Find(".error-message .t").TextContent.ShouldBe("Nie udało się wczytać danych konta");
    }

    [Fact]
    public void Render_WhenTheLoadIsUnauthorized_RedirectsToLoginInsteadOfTheErrorPanel()
    {
        // IsUnauthorized must win over the failure panel, or an expired session ends on an error box.
        StubUnauthorizedExport();

        IRenderedComponent<AccountComponent> component = Render<AccountComponent>();

        component.FindComponents<RedirectToLogin>().ShouldHaveSingleItem();
        component.FindAll(".error-message").ShouldBeEmpty();
    }

    private void StubUnauthorizedExport()
    {
        AuthDiscoveryResponse discovery = new("LotroKoniecDev.AuthSystem")
        {
            Links = [new LinkDto("auth/account/data-export", Rels.ExportAccountData, "GET")]
        };
        _discoveryCache.GetAuthSystemDiscoveryAsync(Arg.Any<CancellationToken>())
            .Returns(ApiResult.Success(discovery));
        _client.GetApiResultAsync<AccountDataExportResponse>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ApiResult.Failure<AccountDataExportResponse>(new ProblemDetails
            {
                Title = "Unauthorized",
                Status = 401
            }));
    }

    private void StubExport(AccountDataExportResponse envelope)
    {
        AuthDiscoveryResponse discovery = new("LotroKoniecDev.AuthSystem")
        {
            Links = [new LinkDto("auth/account/data-export", Rels.ExportAccountData, "GET")]
        };
        _discoveryCache.GetAuthSystemDiscoveryAsync(Arg.Any<CancellationToken>())
            .Returns(ApiResult.Success(discovery));
        _client.GetApiResultAsync<AccountDataExportResponse>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ApiResult.Success(envelope));
    }
}
