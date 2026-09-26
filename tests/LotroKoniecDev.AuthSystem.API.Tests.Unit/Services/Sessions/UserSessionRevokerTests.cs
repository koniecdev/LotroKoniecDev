using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using OpenIddict.Abstractions;
using Shouldly;
using LotroKoniecDev.AuthSystem.API.Services.Sessions;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Services.Sessions;

/// <summary>
/// The revoker runs after a committed save, so it never throws and never listens to the request. Its
/// own time limit is the only thing that may stop it early (#872).
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
    public async Task RevokeAllAsync_ShouldRevokeEveryTokenAndAuthorization()
    {
        // Arrange
        object firstToken = new();
        object secondToken = new();
        object authorization = new();
        _tokenManager.FindBySubjectAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(Stored(firstToken, secondToken));
        _authorizationManager.FindBySubjectAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(Stored(authorization));
        UserSessionRevoker sut = CreateSut();

        // Act
        await sut.RevokeAllAsync(UserId);

        // Assert
        await _tokenManager.Received(1).TryRevokeAsync(firstToken, Arg.Any<CancellationToken>());
        await _tokenManager.Received(1).TryRevokeAsync(secondToken, Arg.Any<CancellationToken>());
        await _authorizationManager.Received(1).TryRevokeAsync(authorization, Arg.Any<CancellationToken>());
    }

    [Fact]
    public void RevokeAllAsync_ShouldKeepWaiting_BeforeTheTimeLimitHasPassed()
    {
        // Arrange
        _tokenManager.FindBySubjectAsync(UserId, Arg.Any<CancellationToken>())
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
        _tokenManager.FindBySubjectAsync(UserId, Arg.Any<CancellationToken>())
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
        object token = new();
        _tokenManager.FindBySubjectAsync(UserId, Arg.Any<CancellationToken>()).Returns(Stored(token));
        _tokenManager.TryRevokeAsync(token, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("The database is gone."));
        UserSessionRevoker sut = CreateSut();

        // Act & Assert
        await Should.NotThrowAsync(() => sut.RevokeAllAsync(UserId));
    }

    private UserSessionRevoker CreateSut() =>
        new(_tokenManager, _authorizationManager, _clock, NullLogger<UserSessionRevoker>.Instance);

    private static async IAsyncEnumerable<object> Stored(params object[] entries)
    {
        foreach (object entry in entries)
        {
            await Task.Yield();
            yield return entry;
        }
    }

    private static async IAsyncEnumerable<object> StuckUntilCancelled(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        yield break;
    }
}
