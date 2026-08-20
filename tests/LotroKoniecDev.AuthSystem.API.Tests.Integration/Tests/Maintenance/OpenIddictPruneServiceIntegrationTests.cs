using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenIddict.Abstractions;
using LotroKoniecDev.AuthSystem.API.Services.Maintenance;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.Maintenance;

/// <summary>
/// Prune behavior against the real OpenIddict Entity Framework stores and PostgreSQL: rows past the
/// 14-day retention that are expired or revoked disappear, while recent or still-valid rows survive.
/// The 13/15-day-old rows bracket the retention threshold from both sides without clock faking —
/// the descriptors carry explicit creation dates.
/// </summary>
[Collection("AuthApi")]
public sealed class OpenIddictPruneServiceIntegrationTests
{
    private readonly AuthSystemApiFactory _factory;

    public OpenIddictPruneServiceIntegrationTests(AuthSystemApiFactory appFactory)
    {
        _factory = appFactory;
    }

    [Fact]
    public void AddAuthApi_RegistersPruneHostedService()
    {
        // Assert
        _factory.Services.GetServices<IHostedService>()
            .OfType<OpenIddictPruneService>()
            .ShouldHaveSingleItem();
    }

    [Fact]
    public async Task PruneOnceAsync_ExpiredTokenPastRetention_IsPrunedWhileRecentAndValidTokensSurvive()
    {
        // Arrange
        string prunedTokenId;
        string recentExpiredTokenId;
        string oldValidTokenId;
        await using (AsyncServiceScope arrangeScope = _factory.Services.CreateAsyncScope())
        {
            IOpenIddictTokenManager tokenManager =
                arrangeScope.ServiceProvider.GetRequiredService<IOpenIddictTokenManager>();

            prunedTokenId = await CreateTokenAsync(tokenManager, createdDaysAgo: 15, expired: true);
            recentExpiredTokenId = await CreateTokenAsync(tokenManager, createdDaysAgo: 13, expired: true);
            oldValidTokenId = await CreateTokenAsync(tokenManager, createdDaysAgo: 15, expired: false);
        }

        // Act
        await ResolvePruneService().PruneOnceAsync(CancellationToken.None);

        // Assert
        await using AsyncServiceScope assertScope = _factory.Services.CreateAsyncScope();
        IOpenIddictTokenManager assertTokenManager =
            assertScope.ServiceProvider.GetRequiredService<IOpenIddictTokenManager>();

        (await assertTokenManager.FindByIdAsync(prunedTokenId)).ShouldBeNull();
        (await assertTokenManager.FindByIdAsync(recentExpiredTokenId)).ShouldNotBeNull();
        (await assertTokenManager.FindByIdAsync(oldValidTokenId)).ShouldNotBeNull();
    }

    [Fact]
    public async Task PruneOnceAsync_RevokedAuthorizationPastRetention_IsPrunedWhileRecentAndPermanentSurvive()
    {
        // Arrange
        string prunedAuthorizationId;
        string recentRevokedAuthorizationId;
        string oldPermanentAuthorizationId;
        await using (AsyncServiceScope arrangeScope = _factory.Services.CreateAsyncScope())
        {
            IOpenIddictAuthorizationManager authorizationManager =
                arrangeScope.ServiceProvider.GetRequiredService<IOpenIddictAuthorizationManager>();

            prunedAuthorizationId = await CreateAuthorizationAsync(
                authorizationManager,
                createdDaysAgo: 15,
                status: OpenIddictConstants.Statuses.Revoked,
                type: OpenIddictConstants.AuthorizationTypes.AdHoc);
            recentRevokedAuthorizationId = await CreateAuthorizationAsync(
                authorizationManager,
                createdDaysAgo: 13,
                status: OpenIddictConstants.Statuses.Revoked,
                type: OpenIddictConstants.AuthorizationTypes.AdHoc);
            oldPermanentAuthorizationId = await CreateAuthorizationAsync(
                authorizationManager,
                createdDaysAgo: 15,
                status: OpenIddictConstants.Statuses.Valid,
                type: OpenIddictConstants.AuthorizationTypes.Permanent);
        }

        // Act
        await ResolvePruneService().PruneOnceAsync(CancellationToken.None);

        // Assert: the permanent valid grant is a user's live consent and must never be pruned
        await using AsyncServiceScope assertScope = _factory.Services.CreateAsyncScope();
        IOpenIddictAuthorizationManager assertAuthorizationManager =
            assertScope.ServiceProvider.GetRequiredService<IOpenIddictAuthorizationManager>();

        (await assertAuthorizationManager.FindByIdAsync(prunedAuthorizationId)).ShouldBeNull();
        (await assertAuthorizationManager.FindByIdAsync(recentRevokedAuthorizationId)).ShouldNotBeNull();
        (await assertAuthorizationManager.FindByIdAsync(oldPermanentAuthorizationId)).ShouldNotBeNull();
    }

    private OpenIddictPruneService ResolvePruneService()
        => _factory.Services.GetServices<IHostedService>()
            .OfType<OpenIddictPruneService>()
            .Single();

    private static async Task<string> CreateTokenAsync(
        IOpenIddictTokenManager tokenManager,
        int createdDaysAgo,
        bool expired)
    {
        DateTimeOffset utcNow = DateTimeOffset.UtcNow;
        OpenIddictTokenDescriptor descriptor = new()
        {
            Subject = Guid.NewGuid().ToString(),
            Type = OpenIddictConstants.TokenTypeHints.RefreshToken,
            Status = OpenIddictConstants.Statuses.Valid,
            CreationDate = utcNow.AddDays(-createdDaysAgo),
            ExpirationDate = expired ? utcNow.AddDays(-1) : utcNow.AddDays(30)
        };

        object token = await tokenManager.CreateAsync(descriptor);
        return (await tokenManager.GetIdAsync(token))!;
    }

    private static async Task<string> CreateAuthorizationAsync(
        IOpenIddictAuthorizationManager authorizationManager,
        int createdDaysAgo,
        string status,
        string type)
    {
        OpenIddictAuthorizationDescriptor descriptor = new()
        {
            Subject = Guid.NewGuid().ToString(),
            Type = type,
            Status = status,
            CreationDate = DateTimeOffset.UtcNow.AddDays(-createdDaysAgo)
        };

        object authorization = await authorizationManager.CreateAsync(descriptor);
        return (await authorizationManager.GetIdAsync(authorization))!;
    }
}
