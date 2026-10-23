using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Account;
using LotroKoniecDev.AuthSystem.Contracts.Hateoas;
using LotroKoniecDev.Frontend.Components.Pages.Account;
using LotroKoniecDev.Frontend.Components.Shared;
using LotroKoniecDev.Frontend.Infrastructure.Discovery;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients.AuthSystemHttpClients;
using LotroKoniecDev.Hateoas.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using AuthDiscoveryResponse = LotroKoniecDev.AuthSystem.Contracts.Discovery.DiscoveryResponse;
using ChangeEmailComponent = LotroKoniecDev.Frontend.Components.Pages.Account.ChangeEmail;

namespace LotroKoniecDev.Frontend.Tests.Unit.Components.Pages.Account;

/// <summary>
/// The form follows the <c>change-email</c> rel, and the inputs carry the names static SSR binds. The
/// submit is not covered: bUnit's SubmitAsync does no SSR form-data binding.
/// </summary>
public sealed class ChangeEmailTests : BunitContext
{
    private readonly IDiscoveryCache _discoveryCache = Substitute.For<IDiscoveryCache>();
    private readonly IAuthSystemClient _client = Substitute.For<IAuthSystemClient>();

    public ChangeEmailTests()
    {
        Services.AddAntiforgery();
        Services.AddSingleton(_discoveryCache);
        Services.AddSingleton(_client);
        Services.AddScoped<AccountLoader>();
    }

    [Fact]
    public void Render_WhenChangeEmailRelAdvertised_ShowsTheFormWithModelBoundInputNames()
    {
        StubExport(AccountLoaderTests.CreateEnvelope(links:
        [
            new LinkDto("auth/account/change-email", Rels.ChangeEmail, "POST")
        ]));

        IRenderedComponent<ChangeEmailComponent> component = Render<ChangeEmailComponent>();

        component.Find("#new-email").GetAttribute("name").ShouldBe("EmailInput.NewEmail");
        component.Find("#repeat-email").GetAttribute("name").ShouldBe("EmailInput.RepeatEmail");
        component.Find("#current-password").GetAttribute("name").ShouldBe("EmailInput.CurrentPassword");
    }

    [Fact]
    public void Render_WhenChangeEmailRelAdvertised_SaysTheAddressIsAlsoTheLogin()
    {
        // Sign-in looks the user up by this address, so the page must say so.
        StubExport(AccountLoaderTests.CreateEnvelope(links:
        [
            new LinkDto("auth/account/change-email", Rels.ChangeEmail, "POST")
        ]));

        IRenderedComponent<ChangeEmailComponent> component = Render<ChangeEmailComponent>();

        component.Markup.ShouldContain("loginem");
    }

    [Fact]
    public void Render_WhenChangeEmailRelMissing_ShowsTheUnavailableNoticeAndNoForm()
    {
        StubExport(AccountLoaderTests.CreateEnvelope(links: []));

        IRenderedComponent<ChangeEmailComponent> component = Render<ChangeEmailComponent>();

        component.Markup.ShouldContain("niedostępna");
        component.FindAll("input[type=email]").ShouldBeEmpty();
    }

    [Fact]
    public void Render_WhenTheLoadIsUnauthorized_RedirectsToLoginInsteadOfTheErrorPanel()
    {
        _discoveryCache.GetAuthSystemDiscoveryAsync(Arg.Any<CancellationToken>())
            .Returns(ApiResult.Success(new AuthDiscoveryResponse("LotroKoniecDev.AuthSystem")
            {
                Links = [new LinkDto("auth/account/data-export", Rels.ExportAccountData, "GET")]
            }));
        _client.GetApiResultAsync<AccountDataExportResponse>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ApiResult.Failure<AccountDataExportResponse>(
                new Microsoft.AspNetCore.Mvc.ProblemDetails { Title = "Unauthorized", Status = 401 }));

        IRenderedComponent<ChangeEmailComponent> component = Render<ChangeEmailComponent>();

        component.FindComponents<RedirectToLogin>().ShouldHaveSingleItem();
        component.FindAll(".error-message").ShouldBeEmpty();
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
