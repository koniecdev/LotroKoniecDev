using LotroKoniecDev.AuthSystem.Contracts.Features.Admin;
using LotroKoniecDev.AuthSystem.Contracts.Hateoas;
using LotroKoniecDev.Frontend.Components.Pages.AccountAdministration;
using LotroKoniecDev.Frontend.Infrastructure.Discovery;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients.AuthSystemHttpClients;
using LotroKoniecDev.Hateoas.Abstractions;
using Microsoft.AspNetCore.Http;
using NSubstitute;
using AuthDiscoveryResponse = LotroKoniecDev.AuthSystem.Contracts.Discovery.DiscoveryResponse;

namespace LotroKoniecDev.Frontend.Tests.Unit.Components.Pages.AccountAdministration;

/// <summary>
/// Pins that the loader follows the advertised hrefs only (#610) and turns a missing <c>accounts</c> rel
/// into a refusal, never a composed path (#1488).
/// </summary>
public sealed class AccountAdministrationLoaderTests
{
    private const string AdvertisedAccountsHref = "https://auth.example/advertised/accounts";
    private const string AdvertisedReviewerHref = "https://auth.example/advertised/accounts/42/reviewer";

    private readonly IDiscoveryCache _discoveryCache = Substitute.For<IDiscoveryCache>();
    private readonly IAuthSystemClient _client = Substitute.For<IAuthSystemClient>();

    [Fact]
    public async Task ListAccountsAsync_OnTheFirstPage_GetsTheAdvertisedHref()
    {
        // Arrange: only the advertised href is stubbed, so a success proves it was followed.
        AccountAdministrationLoader loader = CreateLoader(advertisesAccounts: true);
        StubListAt(AdvertisedAccountsHref);

        // Act
        ApiResult<AccountListResponse> result = await loader.ListAccountsAsync(page: 1);

        // Assert
        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task ListAccountsAsync_OnALaterPage_AddsThePageToTheAdvertisedHref()
    {
        // Arrange
        AccountAdministrationLoader loader = CreateLoader(advertisesAccounts: true);
        StubListAt($"{AdvertisedAccountsHref}?page=3");

        // Act
        ApiResult<AccountListResponse> result = await loader.ListAccountsAsync(page: 3);

        // Assert
        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task ListAccountsAsync_WhenTheAccountsRelIsMissing_RefusesWith403WithoutCallingTheApi()
    {
        // Arrange
        AccountAdministrationLoader loader = CreateLoader(advertisesAccounts: false);

        // Act
        ApiResult<AccountListResponse> result = await loader.ListAccountsAsync(page: 1);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.ProblemDetails.ShouldNotBeNull().Status.ShouldBe(StatusCodes.Status403Forbidden);
        await _client.DidNotReceiveWithAnyArgs().GetApiResultAsync<AccountListResponse>(default!, default);
    }

    [Fact]
    public async Task GrantReviewerAsync_PutsToTheAdvertisedHref()
    {
        // Arrange
        AccountAdministrationLoader loader = CreateLoader(advertisesAccounts: true);
        _client.PutApiResultAsync(AdvertisedReviewerHref, Arg.Any<CancellationToken>()).Returns(ApiResult.Success());

        // Act
        ApiResult result = await loader.GrantReviewerAsync(AdvertisedReviewerHref);

        // Assert
        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task RemoveReviewerAsync_DeletesTheAdvertisedHref()
    {
        // Arrange
        AccountAdministrationLoader loader = CreateLoader(advertisesAccounts: true);
        _client.DeleteApiResultAsync(AdvertisedReviewerHref, Arg.Any<CancellationToken>()).Returns(ApiResult.Success());

        // Act
        ApiResult result = await loader.RemoveReviewerAsync(AdvertisedReviewerHref);

        // Assert
        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task ListAccountsAsync_WhenTheDiscoveryCallFails_PassesTheFailureOn()
    {
        // Arrange: the sign-in server is down.
        _discoveryCache.GetAuthSystemDiscoveryAsync(Arg.Any<CancellationToken>())
            .Returns(ApiResult.Failure<AuthDiscoveryResponse>(new() { Status = StatusCodes.Status503ServiceUnavailable }));
        AccountAdministrationLoader loader = new(_discoveryCache, _client);

        // Act
        ApiResult<AccountListResponse> result = await loader.ListAccountsAsync(page: 1);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.ProblemDetails.ShouldNotBeNull().Status.ShouldBe(StatusCodes.Status503ServiceUnavailable);
    }

    private void StubListAt(string href) =>
        _client.GetApiResultAsync<AccountListResponse>(href, Arg.Any<CancellationToken>())
            .Returns(ApiResult.Success(new AccountListResponse([], 1, 50, 0)));

    private AccountAdministrationLoader CreateLoader(bool advertisesAccounts)
    {
        AuthDiscoveryResponse discovery = new("LotroKoniecDev.AuthSystem")
        {
            Links = advertisesAccounts ? [new LinkDto(AdvertisedAccountsHref, Rels.Accounts, "GET")] : []
        };
        _discoveryCache.GetAuthSystemDiscoveryAsync(Arg.Any<CancellationToken>()).Returns(ApiResult.Success(discovery));

        return new AccountAdministrationLoader(_discoveryCache, _client);
    }
}
