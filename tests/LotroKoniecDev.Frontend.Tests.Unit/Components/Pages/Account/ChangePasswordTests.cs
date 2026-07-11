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
using ChangePasswordComponent = LotroKoniecDev.Frontend.Components.Pages.Account.ChangePassword;

namespace LotroKoniecDev.Frontend.Tests.Unit.Components.Pages.Account;

/// <summary>
/// Renders the <c>/account/change-password</c> page through bUnit over a stubbed auth client: the form
/// appears only when the envelope advertises the <c>change-password</c> rel, and every input is named
/// after the <c>[SupplyParameterFromForm]</c> model path so the posted fields actually bind (the M3-07
/// SSR binding lesson). The submit path is exercised by the browser E2E.
/// </summary>
public sealed class ChangePasswordTests : BunitContext
{
    private readonly IDiscoveryCache _discoveryCache = Substitute.For<IDiscoveryCache>();
    private readonly IAuthSystemClient _client = Substitute.For<IAuthSystemClient>();

    public ChangePasswordTests()
    {
        Services.AddAntiforgery();
        Services.AddSingleton(_discoveryCache);
        Services.AddSingleton(_client);
        Services.AddScoped<AccountLoader>();
    }

    [Fact]
    public void Render_WhenChangePasswordRelAdvertised_ShowsTheFormWithModelBoundInputNames()
    {
        StubExport(AccountLoaderTests.CreateEnvelope(links:
        [
            new LinkDto("auth/change-password", Rels.ChangePassword, "POST")
        ]));

        IRenderedComponent<ChangePasswordComponent> component = Render<ChangePasswordComponent>();

        component.Find("#current-password").GetAttribute("name").ShouldBe("PasswordInput.CurrentPassword");
        component.Find("#new-password").GetAttribute("name").ShouldBe("PasswordInput.NewPassword");
        component.Find("#repeat-password").GetAttribute("name").ShouldBe("PasswordInput.RepeatPassword");
    }

    [Fact]
    public void Render_WhenChangePasswordRelMissing_ShowsTheUnavailableNoticeAndNoForm()
    {
        StubExport(AccountLoaderTests.CreateEnvelope(links: []));

        IRenderedComponent<ChangePasswordComponent> component = Render<ChangePasswordComponent>();

        component.Markup.ShouldContain("niedostępna");
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
