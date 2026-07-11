using AngleSharp.Dom;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Account;
using LotroKoniecDev.AuthSystem.Contracts.Hateoas;
using LotroKoniecDev.Frontend.Components.Pages.Account;
using LotroKoniecDev.Frontend.Infrastructure.Discovery;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients.AuthSystemHttpClients;
using LotroKoniecDev.Hateoas.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using AuthDiscoveryResponse = LotroKoniecDev.AuthSystem.Contracts.Discovery.DiscoveryResponse;
using DeleteAccountComponent = LotroKoniecDev.Frontend.Components.Pages.Account.DeleteAccount;

namespace LotroKoniecDev.Frontend.Tests.Unit.Components.Pages.Account;

/// <summary>
/// Renders the <c>/account/delete</c> page through bUnit over a stubbed auth client. Locks down the
/// SSR form wiring (inputs named after the <c>[SupplyParameterFromForm]</c> model path — the M3-07
/// binding lesson), the rel-gating (no <c>delete-account</c> rel → no form), and the
/// already-scheduled notice. The full submit path is exercised by the browser E2E — bUnit cannot bind
/// SSR form data.
/// </summary>
public sealed class DeleteAccountTests : BunitContext
{
    private readonly IDiscoveryCache _discoveryCache = Substitute.For<IDiscoveryCache>();
    private readonly IAuthSystemClient _client = Substitute.For<IAuthSystemClient>();

    public DeleteAccountTests()
    {
        Services.AddAntiforgery();
        Services.AddSingleton(_discoveryCache);
        Services.AddSingleton(_client);
        Services.AddScoped<AccountLoader>();
    }

    [Fact]
    public void Render_WhenDeleteRelAdvertised_ShowsTheFormWithModelBoundInputNames()
    {
        StubExport(AccountLoaderTests.CreateEnvelope(links:
        [
            new LinkDto("auth/account/delete", Rels.DeleteAccount, "POST")
        ]));

        IRenderedComponent<DeleteAccountComponent> component = Render<DeleteAccountComponent>();

        // Static SSR binds the posted fields only through the [SupplyParameterFromForm] model path.
        component.Find("input[type=password]").GetAttribute("name").ShouldBe("DeleteInput.Password");
        component.Find("input[type=text]").GetAttribute("name").ShouldBe("DeleteInput.ConfirmPhrase");
        component.Find("[data-testid=delete-submit]").TextContent.Trim().ShouldBe("Usuń konto");
    }

    [Fact]
    public void Render_WhenDeleteRelAdvertised_ExplainsTheConsequencesIncludingTheEmailOnlyCancel()
    {
        StubExport(AccountLoaderTests.CreateEnvelope(links:
        [
            new LinkDto("auth/account/delete", Rels.DeleteAccount, "POST")
        ]));

        IRenderedComponent<DeleteAccountComponent> component = Render<DeleteAccountComponent>();

        component.FindAll(".consequence-list li").Count.ShouldBe(4);
        component.Markup.ShouldContain(DeleteAccountComponent.ConfirmPhrase);
    }

    [Fact]
    public void Render_WhenDeletionAlreadyScheduled_ShowsTheNoticeAndNoForm()
    {
        // The API suppresses every rel except cancel-deletion once a deletion is scheduled (ADR-0031).
        StubExport(AccountLoaderTests.CreateEnvelope(
            deletionScheduledAt: new DateTimeOffset(2026, 7, 11, 8, 0, 0, TimeSpan.Zero),
            links: [new LinkDto("auth/account/cancel-deletion", Rels.CancelDeletion, "POST")]));

        IRenderedComponent<DeleteAccountComponent> component = Render<DeleteAccountComponent>();

        component.Markup.ShouldContain("już zaplanowane");
        component.FindAll("input[type=password]").ShouldBeEmpty();
    }

    [Fact]
    public void Render_WhenDeleteRelMissingAndNothingScheduled_ShowsTheUnavailableNotice()
    {
        StubExport(AccountLoaderTests.CreateEnvelope(links: []));

        IRenderedComponent<DeleteAccountComponent> component = Render<DeleteAccountComponent>();

        component.Markup.ShouldContain("niedostępne");
        component.FindAll("input[type=password]").ShouldBeEmpty();
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
