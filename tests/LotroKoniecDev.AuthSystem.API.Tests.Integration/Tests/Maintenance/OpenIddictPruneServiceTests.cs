using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OpenIddict.Abstractions;
using LotroKoniecDev.AuthSystem.API.Services.Maintenance;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.Maintenance;

/// <summary>
/// Behavior of a single prune pass against substituted OpenIddict managers: the 14-day threshold,
/// the tokens-before-authorizations order OpenIddict requires, and the never-crash-the-host
/// failure posture. Pure tests; they start no container.
/// </summary>
public sealed class OpenIddictPruneServiceTests
{
    private static readonly DateTimeOffset FixedUtcNow = new(2026, 7, 2, 12, 0, 0, TimeSpan.Zero);

    private readonly IOpenIddictTokenManager _tokenManager =
        Substitute.For<IOpenIddictTokenManager>();

    private readonly IOpenIddictAuthorizationManager _authorizationManager =
        Substitute.For<IOpenIddictAuthorizationManager>();

    [Fact]
    public async Task PruneOnceAsync_HealthyManagers_PrunesTokensBeforeAuthorizationsWithFourteenDayThreshold()
    {
        // Arrange
        await using ServiceProvider serviceProvider = BuildServiceProvider();
        using OpenIddictPruneService service = CreateService(serviceProvider);
        DateTimeOffset expectedThreshold = FixedUtcNow - OpenIddictPruneService.RetentionPeriod;

        // Act
        await service.PruneOnceAsync(CancellationToken.None);

        // Assert: pruning is invisible in any return value, so the manager calls are the observable
        // side effect; tokens must go first because OpenIddict never deletes an authorization that
        // still has tokens attached.
        Received.InOrder(() =>
        {
            _tokenManager.PruneAsync(expectedThreshold, Arg.Any<CancellationToken>());
            _authorizationManager.PruneAsync(expectedThreshold, Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task PruneOnceAsync_TokenManagerFails_DoesNotThrow()
    {
        // Arrange
        _tokenManager.PruneAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromException<long>(new InvalidOperationException("database unavailable")));
        await using ServiceProvider serviceProvider = BuildServiceProvider();
        using OpenIddictPruneService service = CreateService(serviceProvider);

        // Act & Assert
        await Should.NotThrowAsync(() => service.PruneOnceAsync(CancellationToken.None));
    }

    [Fact]
    public async Task PruneOnceAsync_AuthorizationManagerFails_DoesNotThrow()
    {
        // Arrange
        _authorizationManager.PruneAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromException<long>(new InvalidOperationException("database unavailable")));
        await using ServiceProvider serviceProvider = BuildServiceProvider();
        using OpenIddictPruneService service = CreateService(serviceProvider);

        // Act & Assert
        await Should.NotThrowAsync(() => service.PruneOnceAsync(CancellationToken.None));
    }

    [Fact]
    public async Task PruneOnceAsync_HostShutdownCancellation_PropagatesOperationCanceledException()
    {
        // Arrange
        using CancellationTokenSource cancellationSource = new();
        await cancellationSource.CancelAsync();
        _tokenManager.PruneAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromException<long>(new OperationCanceledException(cancellationSource.Token)));
        await using ServiceProvider serviceProvider = BuildServiceProvider();
        using OpenIddictPruneService service = CreateService(serviceProvider);

        // Act & Assert: a shutdown cancellation must not be swallowed and logged as a failure
        await Should.ThrowAsync<OperationCanceledException>(
            () => service.PruneOnceAsync(cancellationSource.Token));
    }

    [Fact]
    public async Task PruneOnceAsync_OperationCanceledWithoutShutdown_IsSwallowedLikeAnyFailure()
    {
        // Arrange: a rogue OperationCanceledException not caused by the host's own stopping token
        _tokenManager.PruneAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromException<long>(new OperationCanceledException()));
        await using ServiceProvider serviceProvider = BuildServiceProvider();
        using OpenIddictPruneService service = CreateService(serviceProvider);

        // Act & Assert
        await Should.NotThrowAsync(() => service.PruneOnceAsync(CancellationToken.None));
    }

    [Fact]
    public async Task StartAsync_StoppedBeforeStartupDelayElapses_NeverPrunes()
    {
        // Arrange: real clock: the one-minute startup delay cannot elapse within this test
        await using ServiceProvider serviceProvider = BuildServiceProvider();
        using OpenIddictPruneService service = new(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System,
            NullLogger<OpenIddictPruneService>.Instance);

        // Act
        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        // Assert
        await _tokenManager.DidNotReceiveWithAnyArgs().PruneAsync(default, default);
        await _authorizationManager.DidNotReceiveWithAnyArgs().PruneAsync(default, default);
    }

    private ServiceProvider BuildServiceProvider()
    {
        ServiceCollection services = new();
        services.AddScoped(_ => _tokenManager);
        services.AddScoped(_ => _authorizationManager);
        return services.BuildServiceProvider();
    }

    private static OpenIddictPruneService CreateService(ServiceProvider serviceProvider)
    {
        TimeProvider timeProvider = Substitute.For<TimeProvider>();
        timeProvider.GetUtcNow().Returns(FixedUtcNow);

        return new OpenIddictPruneService(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            timeProvider,
            NullLogger<OpenIddictPruneService>.Instance);
    }
}
