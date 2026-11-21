using LotroKoniecDev.AuthSystem.Contracts.Features.Admin;
using LotroKoniecDev.AuthSystem.Contracts.Hateoas;
using LotroKoniecDev.Frontend.Components.Pages.AccountAdministration;
using LotroKoniecDev.Frontend.Infrastructure.Discovery;
using LotroKoniecDev.Frontend.Infrastructure.Errors;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients.AuthSystemHttpClients;
using LotroKoniecDev.Hateoas.Abstractions;
using LotroKoniecDev.SharedKernel.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using AccountAdministrationComponent = LotroKoniecDev.Frontend.Components.Pages.AccountAdministration.AccountAdministration;
using AuthDiscoveryResponse = LotroKoniecDev.AuthSystem.Contracts.Discovery.DiscoveryResponse;

namespace LotroKoniecDev.Frontend.Tests.Unit.Components.Pages.AccountAdministration;

/// <summary>
/// Pins what the loader tests cannot reach: each row shows its roles in Polish, and the role forms and
/// the pager follow the server's links, never a role check (#1488). The real form posts are in
/// <c>FormPostWhileTheLoadFailsTests</c> and the auth API's endpoint tests.
/// </summary>
public sealed class AccountAdministrationTests : BunitContext
{
    private readonly IDiscoveryCache _discoveryCache = Substitute.For<IDiscoveryCache>();
    private readonly IAuthSystemClient _client = Substitute.For<IAuthSystemClient>();

    [Fact]
    public void Render_WhenAccountsLoad_ListsEachAccountWithItsPolishRoleNames()
    {
        // Arrange
        StubAccounts(Page(
            Account("frodo", AuthConstants.Roles.Translator),
            Account("sam", AuthConstants.Roles.Reviewer, AuthConstants.Roles.Translator)));

        // Act
        IRenderedComponent<AccountAdministrationComponent> component = Render<AccountAdministrationComponent>();

        // Assert
        component.FindAll("[data-testid=account-row]").Count.ShouldBe(2);
        string table = component.Find("table.data-table").TextContent;
        table.ShouldContain("frodo");
        table.ShouldContain("Tłumacz");
        table.ShouldContain("Recenzent");
    }

    [Fact]
    public void Render_ShowsEachRoleFormOnlyWhereTheServerOffersIt()
    {
        // Arrange
        StubAccounts(Page(
            Account("frodo", [AuthConstants.Roles.Translator], new LinkDto("https://auth.example/g", Rels.GrantReviewer, "PUT")),
            Account("sam", [AuthConstants.Roles.Reviewer, AuthConstants.Roles.Translator], new LinkDto("https://auth.example/r", Rels.RemoveReviewer, "DELETE")),
            Account("gandalf", AuthConstants.Roles.Admin)));

        // Act
        IRenderedComponent<AccountAdministrationComponent> component = Render<AccountAdministrationComponent>();

        // Assert
        IReadOnlyList<AngleSharp.Dom.IElement> rows = component.FindAll("[data-testid=account-row]");
        rows[0].TextContent.ShouldContain("Nadaj rolę Recenzent");
        rows[0].TextContent.ShouldNotContain("Odbierz rolę Recenzent");
        rows[1].TextContent.ShouldContain("Odbierz rolę Recenzent");
        rows[1].TextContent.ShouldNotContain("Nadaj rolę Recenzent");
        rows[2].QuerySelectorAll("form").Length.ShouldBe(0);
    }

    [Fact]
    public void Render_WithANextPageLink_LinksTheSecondPage()
    {
        // Arrange
        AccountListResponse page = Page(Account("frodo", AuthConstants.Roles.Translator));
        page.Links = [new LinkDto("https://auth.example/accounts?page=2", Rels.NextPage, "GET")];
        StubAccounts(page);

        // Act
        IRenderedComponent<AccountAdministrationComponent> component = Render<AccountAdministrationComponent>();

        // Assert
        component.Find("nav.pager a[rel=next]").GetAttribute("href").ShouldBe("/admin/accounts?page=2");
    }

    [Fact]
    public void Render_WhenTheServerDoesNotOfferTheList_ShowsTheRefusal()
    {
        // Arrange: a translator who typed the address gets no accounts rel.
        _discoveryCache.GetAuthSystemDiscoveryAsync(Arg.Any<CancellationToken>())
            .Returns(ApiResult.Success(new AuthDiscoveryResponse("LotroKoniecDev.AuthSystem")));

        // Act
        IRenderedComponent<AccountAdministrationComponent> component = Render<AccountAdministrationComponent>();

        // Assert
        component.Find(".error-message").TextContent.ShouldContain("Lista kont jest niedostępna");
        component.FindAll("table.data-table").ShouldBeEmpty();
    }

    [Fact]
    public void Render_WhenTheListFailsToLoad_ShowsTheProblem()
    {
        // Arrange
        StubDiscovery();
        _client.GetApiResultAsync<AccountListResponse>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ApiResult.Failure<AccountListResponse>(ApiProblemCopy.StatusOnly(StatusCodes.Status503ServiceUnavailable)));

        // Act
        IRenderedComponent<AccountAdministrationComponent> component = Render<AccountAdministrationComponent>();

        // Assert
        component.FindAll(".error-message").Count.ShouldBe(1);
        component.FindAll("table.data-table").ShouldBeEmpty();
    }

    public AccountAdministrationTests()
    {
        Services.AddAntiforgery();
        Services.AddSingleton(_discoveryCache);
        Services.AddSingleton(_client);
        Services.AddScoped<AccountAdministrationLoader>();
    }

    private static AccountSummaryResponse Account(string username, params string[] roles) =>
        Account(username, roles, links: []);

    private static AccountSummaryResponse Account(string username, string[] roles, params LinkDto[] links) =>
        new(Guid.NewGuid(), username, $"{username}@shire.me", roles, DeletionScheduledAt: null) { Links = links };

    private static AccountListResponse Page(params AccountSummaryResponse[] accounts) =>
        new(accounts, Page: 1, PageSize: 50, TotalCount: accounts.Length);

    private void StubDiscovery()
    {
        AuthDiscoveryResponse discovery = new("LotroKoniecDev.AuthSystem")
        {
            Links = [new LinkDto("https://auth.example/accounts", Rels.Accounts, "GET")]
        };
        _discoveryCache.GetAuthSystemDiscoveryAsync(Arg.Any<CancellationToken>()).Returns(ApiResult.Success(discovery));
    }

    private void StubAccounts(AccountListResponse page)
    {
        StubDiscovery();
        _client.GetApiResultAsync<AccountListResponse>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ApiResult.Success(page));
    }
}
