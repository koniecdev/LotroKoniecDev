using System.Net;
using AngleSharp.Dom;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Account;
using LotroKoniecDev.AuthSystem.Contracts.Hateoas;
using LotroKoniecDev.Frontend.Components.Pages.Account;
using LotroKoniecDev.Frontend.Components.Shared;
using LotroKoniecDev.Frontend.Infrastructure.Discovery;
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
/// Renders the <c>/account</c> page through bUnit over a stubbed auth client, locking down the render
/// wiring the loader tests cannot reach: the identity/consent data lands in the markup, the export
/// download always targets the server download route, and the change-password / delete action rows are
/// gated on the envelope's HATEOAS rels — never on a locally recomputed role.
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
        component.Markup.ShouldContain("2026-06-01 12:00 UTC");
    }

    [Fact]
    public void Render_WhenExportLoads_AlwaysOffersTheExportDownloadLink()
    {
        StubExport(AccountLoaderTests.CreateEnvelope());

        IRenderedComponent<AccountComponent> component = Render<AccountComponent>();

        IElement download = component.Find("[data-testid=account-export]");
        download.GetAttribute("href").ShouldBe("/account/export");
        download.HasAttribute("download").ShouldBeTrue();
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
    public void Render_WhenEnvelopeAdvertisesNoActionRels_HidesTheGatedActionRows()
    {
        StubExport(AccountLoaderTests.CreateEnvelope(links: []));

        IRenderedComponent<AccountComponent> component = Render<AccountComponent>();

        component.FindAll("[data-testid=account-change-password]").ShouldBeEmpty();
        component.FindAll("[data-testid=account-delete]").ShouldBeEmpty();
        component.FindAll("[data-testid=account-export]").ShouldHaveSingleItem();
    }

    [Fact]
    public void Render_WhenDeletionScheduled_ShowsTheScheduledNotice()
    {
        StubExport(AccountLoaderTests.CreateEnvelope(
            deletionScheduledAt: new DateTimeOffset(2026, 7, 11, 8, 0, 0, TimeSpan.Zero)));

        IRenderedComponent<AccountComponent> component = Render<AccountComponent>();

        component.Markup.ShouldContain("Usunięcie tego konta zostało zaplanowane");
        component.Markup.ShouldContain("2026-07-11 08:00 UTC");
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
            .Returns(ApiResult.Failure<AccountDataExportResponse>(new ProblemDetails
            {
                Title = "Nie udało się wczytać danych konta",
                Status = (int)HttpStatusCode.BadGateway
            }));

        IRenderedComponent<AccountComponent> component = Render<AccountComponent>();

        component.Find(".error-message .t").TextContent.ShouldBe("Nie udało się wczytać danych konta");
    }

    [Fact]
    public void Render_WhenTheLoadIsUnauthorized_RedirectsToLoginInsteadOfTheErrorPanel()
    {
        // The branch order is load-bearing: IsUnauthorized must win over the generic failure panel,
        // or an expired session dead-ends on an error box instead of bouncing through login.
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
