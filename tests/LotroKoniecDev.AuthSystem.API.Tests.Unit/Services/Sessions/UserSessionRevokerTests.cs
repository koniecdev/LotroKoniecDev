using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using OpenIddict.Abstractions;
using Shouldly;
using LotroKoniecDev.AuthSystem.API.Services.Sessions;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Services.Sessions;

/// <summary>
/// The revoker runs after a committed save or at sign-out, so it never throws and never listens to the
/// request. Each step has its own time limit, which is the only thing that may stop it early, and a step
/// that fails or runs out of time never skips the other one (#872).
/// </summary>
public sealed class UserSessionRevokerTests
{
    private const string UserId = "0192a3b4-c5d6-7e8f-9a0b-1c2d3e4f5a6b";
    private const string AuthorizationId = "0192a3b4-c5d6-7e8f-9a0b-6b5a4f3e2d1c";

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
    public async Task RevokeAllAsync_ShouldRevokeTheAuthorizationsBeforeTheTokens_WhenTheStoreAnswers()
    {
        // Arrange: a refresh that races the revoke gets a dead token only when its authorization went first
        UserSessionRevoker sut = CreateSut();

        // Act
        await sut.RevokeAllAsync(UserId);

        // Assert
        Received.InOrder(async () =>
        {
            await _authorizationManager.RevokeBySubjectAsync(UserId, Arg.Any<CancellationToken>());
            await _tokenManager.RevokeBySubjectAsync(UserId, Arg.Any<CancellationToken>());
        });
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
            .Returns(callInfo => StuckUntilCancelled<long>(callInfo.Arg<CancellationToken>()));
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
            .Returns(callInfo => StuckUntilCancelled<long>(callInfo.Arg<CancellationToken>()));
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
            .Returns(callInfo => StuckUntilCancelled<long>(callInfo.Arg<CancellationToken>()));
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

    [Fact]
    public async Task RevokeSessionAsync_ShouldRevokeTheAuthorizationAndItsTokens_WhenTheStoreAnswers()
    {
        // Arrange
        object authorization = StubTheAuthorization();
        UserSessionRevoker sut = CreateSut();

        // Act
        await sut.RevokeSessionAsync(AuthorizationId);

        // Assert
        await _authorizationManager.Received(1).TryRevokeAsync(authorization, Arg.Any<CancellationToken>());
        await _tokenManager.Received(1).RevokeByAuthorizationIdAsync(AuthorizationId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RevokeSessionAsync_ShouldRevokeTheAuthorizationBeforeTheTokens_WhenTheStoreAnswers()
    {
        // Arrange
        object authorization = StubTheAuthorization();
        UserSessionRevoker sut = CreateSut();

        // Act
        await sut.RevokeSessionAsync(AuthorizationId);

        // Assert
        Received.InOrder(async () =>
        {
            await _authorizationManager.TryRevokeAsync(authorization, Arg.Any<CancellationToken>());
            await _tokenManager.RevokeByAuthorizationIdAsync(AuthorizationId, Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task RevokeSessionAsync_ShouldStillRevokeTheTokens_WhenTheAuthorizationIsGone()
    {
        // Arrange: the substitute finds no authorization row
        UserSessionRevoker sut = CreateSut();

        // Act
        await sut.RevokeSessionAsync(AuthorizationId);

        // Assert
        await _tokenManager.Received(1).RevokeByAuthorizationIdAsync(AuthorizationId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RevokeSessionAsync_ShouldStillRevokeTheTokens_WhenTheAuthorizationStepFails()
    {
        // Arrange
        _authorizationManager.FindByIdAsync(AuthorizationId, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("The database is gone."));
        UserSessionRevoker sut = CreateSut();

        // Act
        await sut.RevokeSessionAsync(AuthorizationId);

        // Assert
        await _tokenManager.Received(1).RevokeByAuthorizationIdAsync(AuthorizationId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RevokeSessionAsync_ShouldStillRevokeTheAuthorization_WhenTheTokenStepFails()
    {
        // Arrange
        object authorization = StubTheAuthorization();
        _tokenManager.RevokeByAuthorizationIdAsync(AuthorizationId, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("The database is gone."));
        UserSessionRevoker sut = CreateSut();

        // Act
        await sut.RevokeSessionAsync(AuthorizationId);

        // Assert
        await _authorizationManager.Received(1).TryRevokeAsync(authorization, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RevokeSessionAsync_ShouldStillRevokeTheTokens_WhenTheAuthorizationStepRanOutOfTime()
    {
        // Arrange: the token stub refuses a cancelled token, as the real store does
        bool tokensRevoked = false;
        _authorizationManager.FindByIdAsync(AuthorizationId, Arg.Any<CancellationToken>())
            .Returns(callInfo => StuckUntilCancelled<object?>(callInfo.Arg<CancellationToken>()));
        _tokenManager.RevokeByAuthorizationIdAsync(AuthorizationId, Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                callInfo.Arg<CancellationToken>().ThrowIfCancellationRequested();
                tokensRevoked = true;
                return ValueTask.FromResult(0L);
            });
        UserSessionRevoker sut = CreateSut();

        // Act
        Task revoking = sut.RevokeSessionAsync(AuthorizationId);
        _clock.Advance(UserSessionRevoker.TimeLimit);
        await revoking.WaitAsync(CompletionTimeout);

        // Assert
        tokensRevoked.ShouldBeTrue();
    }

    private object StubTheAuthorization()
    {
        object authorization = new();
        _authorizationManager.FindByIdAsync(AuthorizationId, Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult<object?>(authorization));
        return authorization;
    }

    private UserSessionRevoker CreateSut() =>
        new(_tokenManager, _authorizationManager, _clock, NullLogger<UserSessionRevoker>.Instance);

    private static async ValueTask<T> StuckUntilCancelled<T>(CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        return default!;
    }
}
