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
    private const string AdvertisedGrantHref = "https://auth.example/advertised/accounts/42/reviewer";

    private readonly IDiscoveryCache _discoveryCache = Substitute.For<IDiscoveryCache>();
    private readonly IAuthSystemClient _client = Substitute.For<IAuthSystemClient>();

    [Fact]
    public async Task ListAccountsAsync_OnTheFirstPage_GetsTheAdvertisedHref()
    {
        // Arrange
        AccountAdministrationLoader loader = CreateLoader(advertisesAccounts: true);

        // Act
        await loader.ListAccountsAsync(page: 1);

        // Assert
        await _client.Received(1).GetApiResultAsync<AccountListResponse>(AdvertisedAccountsHref, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ListAccountsAsync_OnALaterPage_AddsThePageToTheAdvertisedHref()
    {
        // Arrange
        AccountAdministrationLoader loader = CreateLoader(advertisesAccounts: true);

        // Act
        await loader.ListAccountsAsync(page: 3);

        // Assert
        await _client.Received(1).GetApiResultAsync<AccountListResponse>(
            $"{AdvertisedAccountsHref}?page=3", Arg.Any<CancellationToken>());
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
        _client.PutApiResultAsync(AdvertisedGrantHref, Arg.Any<CancellationToken>()).Returns(ApiResult.Success());

        // Act
        ApiResult result = await loader.GrantReviewerAsync(AdvertisedGrantHref);

        // Assert
        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task RemoveReviewerAsync_DeletesTheAdvertisedHref()
    {
        // Arrange
        AccountAdministrationLoader loader = CreateLoader(advertisesAccounts: true);
        _client.DeleteApiResultAsync(AdvertisedGrantHref, Arg.Any<CancellationToken>()).Returns(ApiResult.Success());

        // Act
        ApiResult result = await loader.RemoveReviewerAsync(AdvertisedGrantHref);

        // Assert
        result.IsSuccess.ShouldBeTrue();
    }

    private AccountAdministrationLoader CreateLoader(bool advertisesAccounts)
    {
        AuthDiscoveryResponse discovery = new("LotroKoniecDev.AuthSystem")
        {
            Links = advertisesAccounts ? [new LinkDto(AdvertisedAccountsHref, Rels.Accounts, "GET")] : []
        };
        _discoveryCache.GetAuthSystemDiscoveryAsync(Arg.Any<CancellationToken>()).Returns(ApiResult.Success(discovery));
        _client.GetApiResultAsync<AccountListResponse>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ApiResult.Success(new AccountListResponse([], 1, 50, 0)));

        return new AccountAdministrationLoader(_discoveryCache, _client);
    }
}
