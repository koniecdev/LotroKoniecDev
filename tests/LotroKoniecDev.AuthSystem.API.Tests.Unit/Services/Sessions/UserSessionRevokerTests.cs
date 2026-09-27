using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using OpenIddict.Abstractions;
using Shouldly;
using LotroKoniecDev.AuthSystem.API.Services.Sessions;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Services.Sessions;

/// <summary>
/// The revoker runs after a committed save, so it never throws and never listens to the request. Each
/// step has its own time limit, which is the only thing that may stop it early, and a step that fails or
/// runs out of time never skips the other one (#872).
/// </summary>
public sealed class UserSessionRevokerTests
{
    private const string UserId = "0192a3b4-c5d6-7e8f-9a0b-1c2d3e4f5a6b";

    private static readonly TimeSpan CompletionTimeout = TimeSpan.FromSeconds(5);

    private readonly IOpenIddictTokenManager _tokenManager = Substitute.For<IOpenIddictTokenManager>();
    private readonly IOpenIddictAuthorizationManager _authorizationManager =
        Substitute.For<IOpenIddictAuthorizationManager>();
    private readonly FakeTimeProvider _clock = new();

    [Fact]
    public async Task RevokeAllAsync_ShouldRevokeTheAuthorizationsAndTheTokens_WhenTheStoreAnswers()
    {
        // Arrange
        UserSessionRevoker sut = CreateSut();

        // Act
        await sut.RevokeAllAsync(UserId);

        // Assert
        await _authorizationManager.Received(1).RevokeBySubjectAsync(UserId, Arg.Any<CancellationToken>());
        await _tokenManager.Received(1).RevokeBySubjectAsync(UserId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RevokeAllAsync_ShouldStillRevokeTheAuthorizations_WhenTheTokenStepFails()
    {
        // Arrange: a refresh token dies with its authorization, so a failing token step must not skip that one
        _tokenManager.RevokeBySubjectAsync(UserId, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("The database is gone."));
        UserSessionRevoker sut = CreateSut();

        // Act
        await sut.RevokeAllAsync(UserId);

        // Assert
        await _authorizationManager.Received(1).RevokeBySubjectAsync(UserId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RevokeAllAsync_ShouldStillRevokeTheTokens_WhenTheAuthorizationStepFails()
    {
        // Arrange
        _authorizationManager.RevokeBySubjectAsync(UserId, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("The database is gone."));
        UserSessionRevoker sut = CreateSut();

        // Act
        await sut.RevokeAllAsync(UserId);

        // Assert
        await _tokenManager.Received(1).RevokeBySubjectAsync(UserId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RevokeAllAsync_ShouldStillRevokeTheTokens_WhenTheAuthorizationStepRanOutOfTime()
    {
        // Arrange: the token stub refuses a cancelled token, as the real store does
        bool tokensRevoked = false;
        _authorizationManager.RevokeBySubjectAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(callInfo => StuckUntilCancelled(callInfo.Arg<CancellationToken>()));
        _tokenManager.RevokeBySubjectAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                callInfo.Arg<CancellationToken>().ThrowIfCancellationRequested();
                tokensRevoked = true;
                return ValueTask.FromResult(0L);
            });
        UserSessionRevoker sut = CreateSut();

        // Act
        Task revoking = sut.RevokeAllAsync(UserId);
        _clock.Advance(UserSessionRevoker.TimeLimit);
        await revoking.WaitAsync(CompletionTimeout);

        // Assert
        tokensRevoked.ShouldBeTrue();
    }

    [Fact]
    public void RevokeAllAsync_ShouldKeepWaiting_BeforeTheTimeLimitHasPassed()
    {
        // Arrange
        _authorizationManager.RevokeBySubjectAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(callInfo => StuckUntilCancelled(callInfo.Arg<CancellationToken>()));
        UserSessionRevoker sut = CreateSut();

        // Act
        Task revoking = sut.RevokeAllAsync(UserId);
        _clock.Advance(UserSessionRevoker.TimeLimit - TimeSpan.FromMilliseconds(1));

        // Assert
        revoking.IsCompleted.ShouldBeFalse();
    }

    [Fact]
    public async Task RevokeAllAsync_ShouldGiveUpWithoutThrowing_WhenTheTimeLimitPasses()
    {
        // Arrange
        _authorizationManager.RevokeBySubjectAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(callInfo => StuckUntilCancelled(callInfo.Arg<CancellationToken>()));
        UserSessionRevoker sut = CreateSut();

        // Act
        Task revoking = sut.RevokeAllAsync(UserId);
        _clock.Advance(UserSessionRevoker.TimeLimit);

        // Assert
        await Should.NotThrowAsync(() => revoking.WaitAsync(CompletionTimeout));
    }

    [Fact]
    public async Task RevokeAllAsync_ShouldNotThrow_WhenTheStoreFails()
    {
        // Arrange
        _authorizationManager.RevokeBySubjectAsync(UserId, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("The database is gone."));
        UserSessionRevoker sut = CreateSut();

        // Act & Assert
        await Should.NotThrowAsync(() => sut.RevokeAllAsync(UserId));
    }

    private UserSessionRevoker CreateSut() =>
        new(_tokenManager, _authorizationManager, _clock, NullLogger<UserSessionRevoker>.Instance);

    private static async ValueTask<long> StuckUntilCancelled(CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        return 0;
    }
}
