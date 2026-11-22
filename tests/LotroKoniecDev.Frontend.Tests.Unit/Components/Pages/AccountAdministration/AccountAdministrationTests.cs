using LotroKoniecDev.AuthSystem.Contracts.Features.Admin;
using LotroKoniecDev.AuthSystem.Contracts.Hateoas;
using LotroKoniecDev.Frontend.Components.Pages.AccountAdministration;
using LotroKoniecDev.Frontend.Infrastructure.Discovery;
using LotroKoniecDev.Frontend.Infrastructure.Errors;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients.AuthSystemHttpClients;
using LotroKoniecDev.Hateoas.Abstractions;
using LotroKoniecDev.SharedKernel.Authorization;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using AccountAdministrationComponent = LotroKoniecDev.Frontend.Components.Pages.AccountAdministration.AccountAdministration;
using AuthDiscoveryResponse = LotroKoniecDev.AuthSystem.Contracts.Discovery.DiscoveryResponse;

namespace LotroKoniecDev.Frontend.Tests.Unit.Components.Pages.AccountAdministration;

/// <summary>
/// Pins what the loader tests cannot reach: each row shows its roles in Polish, the role forms and the
/// pager follow the server's links, never a role check, and a change redirects (#1488). The real form
/// posts are in <c>AccountAdministrationFormPostTests</c> and <c>FormPostWhileTheLoadFailsTests</c>.
/// </summary>
public sealed class AccountAdministrationTests : BunitContext
{
    private const string AccountsHref = "https://auth.example/accounts";
    private const string GrantHref = "https://auth.example/accounts/1/reviewer";
    private const string RemoveHref = "https://auth.example/accounts/2/reviewer";

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
    public void Render_OnTheSecondPageWithAPreviousPageLink_LinksTheFirstPage()
    {
        // Arrange
        AccountListResponse page = new([Account("frodo", AuthConstants.Roles.Translator)], Page: 2, PageSize: 50, TotalCount: 51)
        {
            Links = [new LinkDto("https://auth.example/accounts?page=1", Rels.PreviousPage, "GET")]
        };
        StubAccounts(page, $"{AccountsHref}?page=2");
        Navigation().NavigateTo("/admin/accounts?page=2");

        // Act
        IRenderedComponent<AccountAdministrationComponent> component = Render<AccountAdministrationComponent>();

        // Assert
        component.Find("nav.pager a[rel=prev]").GetAttribute("href").ShouldBe("/admin/accounts");
    }

    [Fact]
    public void Render_ForAPagePastTheEnd_ShowsNoAccountsAndTheWayBack()
    {
        // Arrange
        AccountListResponse page = new([], Page: 5, PageSize: 50, TotalCount: 3)
        {
            Links = [new LinkDto("https://auth.example/accounts?page=4", Rels.PreviousPage, "GET")]
        };
        StubAccounts(page, $"{AccountsHref}?page=5");
        Navigation().NavigateTo("/admin/accounts?page=5");

        // Act
        IRenderedComponent<AccountAdministrationComponent> component = Render<AccountAdministrationComponent>();

        // Assert
        component.Find(".empty h2").TextContent.ShouldBe("Brak kont");
        component.FindAll("table.data-table").ShouldBeEmpty();
        component.Find("nav.pager a[rel=prev]").GetAttribute("href").ShouldBe("/admin/accounts?page=4");
    }

    [Fact]
    public void Render_ForAnAccountWithADeletionScheduled_ShowsTheBadge()
    {
        // Arrange
        AccountSummaryResponse leaving = Account("bilbo", AuthConstants.Roles.Translator) with
        {
            DeletionScheduledAt = new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero)
        };
        StubAccounts(Page(leaving, Account("frodo", AuthConstants.Roles.Translator)));

        // Act
        IRenderedComponent<AccountAdministrationComponent> component = Render<AccountAdministrationComponent>();

        // Assert
        IReadOnlyList<AngleSharp.Dom.IElement> rows = component.FindAll("[data-testid=account-row]");
        rows[0].TextContent.ShouldContain("Usunięcie zaplanowane");
        rows[1].TextContent.ShouldNotContain("Usunięcie zaplanowane");
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

    [Fact]
    public async Task Grant_WhenTheApiAccepts_RedirectsWithTheGrantedFlagFollowingTheRowsHref()
    {
        // Arrange: only a PUT to the row's own href is stubbed to succeed, so the redirect proves it.
        StubAccounts(Page(Account("frodo", [AuthConstants.Roles.Translator], new LinkDto(GrantHref, Rels.GrantReviewer, "PUT"))));
        _client.PutApiResultAsync(GrantHref, Arg.Any<CancellationToken>()).Returns(ApiResult.Success());
        IRenderedComponent<AccountAdministrationComponent> component = Render<AccountAdministrationComponent>();

        // Act
        await component.Find("table.data-table form").SubmitAsync();

        // Assert
        Navigation().Uri.ShouldEndWith("/admin/accounts?changed=granted");
    }

    [Fact]
    public async Task Remove_WhenTheApiAccepts_RedirectsWithTheRemovedFlagFollowingTheRowsHref()
    {
        // Arrange
        StubAccounts(Page(Account(
            "sam",
            [AuthConstants.Roles.Reviewer, AuthConstants.Roles.Translator],
            new LinkDto(RemoveHref, Rels.RemoveReviewer, "DELETE"))));
        _client.DeleteApiResultAsync(RemoveHref, Arg.Any<CancellationToken>()).Returns(ApiResult.Success());
        IRenderedComponent<AccountAdministrationComponent> component = Render<AccountAdministrationComponent>();

        // Act
        await component.Find("table.data-table form").SubmitAsync();

        // Assert
        Navigation().Uri.ShouldEndWith("/admin/accounts?changed=removed");
    }

    [Fact]
    public async Task Grant_OnALaterPage_RedirectsBackToThatPageWithTheGrantedFlag()
    {
        // Arrange
        AccountListResponse secondPage = Page(Account("frodo", [AuthConstants.Roles.Translator], new LinkDto(GrantHref, Rels.GrantReviewer, "PUT")));
        StubDiscovery();
        _client.GetApiResultAsync<AccountListResponse>($"{AccountsHref}?page=2", Arg.Any<CancellationToken>())
            .Returns(ApiResult.Success(secondPage));
        _client.PutApiResultAsync(GrantHref, Arg.Any<CancellationToken>()).Returns(ApiResult.Success());
        Navigation().NavigateTo("/admin/accounts?page=2");
        IRenderedComponent<AccountAdministrationComponent> component = Render<AccountAdministrationComponent>();

        // Act
        await component.Find("table.data-table form").SubmitAsync();

        // Assert
        Navigation().Uri.ShouldEndWith("/admin/accounts?page=2&changed=granted");
    }

    [Fact]
    public async Task Grant_WhenTheApiRefuses_ShowsTheProblemAndKeepsTheListAndTheAddress()
    {
        // Arrange
        StubAccounts(Page(Account("frodo", [AuthConstants.Roles.Translator], new LinkDto(GrantHref, Rels.GrantReviewer, "PUT"))));
        _client.PutApiResultAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ApiResult.Failure(new() { Title = "Nie można nadać tej roli.", Status = 422 }));
        IRenderedComponent<AccountAdministrationComponent> component = Render<AccountAdministrationComponent>();
        string addressBefore = Navigation().Uri;

        // Act
        await component.Find("table.data-table form").SubmitAsync();

        // Assert
        component.Find(".status-message.status-error").TextContent.ShouldContain("Nie można nadać");
        component.FindAll("table.data-table").ShouldNotBeEmpty();
        Navigation().Uri.ShouldBe(addressBefore);
    }

    [Fact]
    public async Task Grant_WhenTheApiRefusesAndTheListReloadFails_ShowsBothProblems()
    {
        // Arrange: one change spends three permits of the auth API's per-address limit, so a refusal and
        // a failed reload can come together.
        StubDiscovery();
        AccountListResponse page = Page(Account("frodo", [AuthConstants.Roles.Translator], new LinkDto(GrantHref, Rels.GrantReviewer, "PUT")));
        _client.GetApiResultAsync<AccountListResponse>(AccountsHref, Arg.Any<CancellationToken>())
            .Returns(
                ApiResult.Success(page),
                ApiResult.Failure<AccountListResponse>(ApiProblemCopy.StatusOnly(StatusCodes.Status429TooManyRequests)));
        _client.PutApiResultAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ApiResult.Failure(ApiProblemCopy.StatusOnly(StatusCodes.Status429TooManyRequests)));
        IRenderedComponent<AccountAdministrationComponent> component = Render<AccountAdministrationComponent>();

        // Act
        await component.Find("table.data-table form").SubmitAsync();

        // Assert
        component.FindAll(".status-message.status-error").Count.ShouldBe(1);
        component.FindAll(".error-message").Count.ShouldBe(1);
        component.FindAll("table.data-table").ShouldBeEmpty();
    }

    [Theory]
    [InlineData("granted", "Nadano rolę Recenzent.")]
    [InlineData("removed", "Odebrano rolę Recenzent.")]
    public void Render_AfterTheRedirect_ShowsTheChangesMessage(string changed, string expected)
    {
        // Arrange
        StubAccounts(Page(Account("frodo", AuthConstants.Roles.Translator)));
        Navigation().NavigateTo($"/admin/accounts?changed={changed}");

        // Act
        IRenderedComponent<AccountAdministrationComponent> component = Render<AccountAdministrationComponent>();

        // Assert
        component.Find(".status-message.status-success").TextContent.ShouldContain(expected);
    }

    [Fact]
    public void Render_WithAChangedValueThePageDoesNotKnow_ShowsNoMessage()
    {
        // Arrange
        StubAccounts(Page(Account("frodo", AuthConstants.Roles.Translator)));
        Navigation().NavigateTo("/admin/accounts?changed=zzz");

        // Act
        IRenderedComponent<AccountAdministrationComponent> component = Render<AccountAdministrationComponent>();

        // Assert
        component.FindAll(".status-message.status-success").ShouldBeEmpty();
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("99999999999")]
    public void Render_WithAPageThatIsNotAPageNumber_LoadsTheFirstPage(string page)
    {
        // Arrange: a value the page cannot read shows the first page instead of failing (#1356).
        StubAccounts(Page(Account("frodo", AuthConstants.Roles.Translator)));
        Navigation().NavigateTo($"/admin/accounts?page={page}");

        // Act
        IRenderedComponent<AccountAdministrationComponent> component = Render<AccountAdministrationComponent>();

        // Assert: the stub answers only the first page's href, so a row on screen proves it.
        component.FindAll("[data-testid=account-row]").Count.ShouldBe(1);
    }

    public AccountAdministrationTests()
    {
        Services.AddAntiforgery();
        Services.AddSingleton(_discoveryCache);
        Services.AddSingleton(_client);
        Services.AddScoped<AccountAdministrationLoader>();
    }

    private BunitNavigationManager Navigation() =>
        (BunitNavigationManager)Services.GetRequiredService<NavigationManager>();

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
            Links = [new LinkDto(AccountsHref, Rels.Accounts, "GET")]
        };
        _discoveryCache.GetAuthSystemDiscoveryAsync(Arg.Any<CancellationToken>()).Returns(ApiResult.Success(discovery));
    }

    private void StubAccounts(AccountListResponse page, string href = AccountsHref)
    {
        StubDiscovery();
        _client.GetApiResultAsync<AccountListResponse>(href, Arg.Any<CancellationToken>())
            .Returns(ApiResult.Success(page));
    }
}
