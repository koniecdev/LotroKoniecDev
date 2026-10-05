using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NSubstitute;
using LotroKoniecDev.AuthSystem.API.Services.Maintenance;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Bases;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Factories;
using LotroKoniecDev.AuthSystem.Persistence;
using LotroKoniecDev.AuthSystem.Persistence.DbContexts;
using LotroKoniecDev.AuthSystem.Persistence.Sessions;
using LotroKoniecDev.SharedKernel.StronglyTypedIds;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.Maintenance;

/// <summary>
/// The daily prune of expired sign-in sessions (ADR-0062) against the real table. The clock is fixed,
/// so the boundary sits exactly where a test puts it.
/// </summary>
public sealed class SignInSessionPruneServiceTests : EndpointsTestBase
{
    private static readonly DateTimeOffset FixedUtcNow = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    public SignInSessionPruneServiceTests(AuthSystemApiFactory appFactory) : base(appFactory) { }

    [Fact]
    public void TestHost_DoesNotHostTheSessionPruneJob()
    {
        // The factory keeps every clock-driven job off this host (#821). The tests below call
        // PruneOnceAsync themselves.
        Factory.Services.GetServices<IHostedService>()
            .OfType<SignInSessionPruneService>()
            .ShouldBeEmpty();
    }

    /// <summary>
    /// A session counts as expired by the cookie handler's own rule: its expiry lies in the past. The
    /// offsets are whole microseconds, the precision PostgreSQL keeps.
    /// </summary>
    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    public async Task PruneOnceAsync_ShouldDeleteOnlySessionsThatExpiredBeforeNow(int microsecondsFromNow, bool pruned)
    {
        // Arrange
        Guid userId = await RegisterUserAsync();
        Guid sessionId = await AddSessionAsync(userId, FixedUtcNow.AddMicroseconds(microsecondsFromNow));
        using SignInSessionPruneService pruneService = CreatePruneService();

        // Act
        await pruneService.PruneOnceAsync(CancellationToken.None);

        // Assert
        (await SessionExistsAsync(sessionId)).ShouldBe(!pruned);
    }

    [Fact]
    public async Task PruneOnceAsync_ShouldDeleteEveryExpiredSession_AndKeepEveryLiveOne()
    {
        // Arrange: two users, so the prune cannot be scoped to one account by mistake
        Guid firstUserId = await RegisterUserAsync();
        Guid secondUserId = await RegisterUserAsync();
        await AddSessionAsync(firstUserId, FixedUtcNow.AddDays(-1));
        await AddSessionAsync(secondUserId, FixedUtcNow.AddMinutes(-1));
        Guid firstLive = await AddSessionAsync(firstUserId, FixedUtcNow.AddMinutes(1));
        Guid secondLive = await AddSessionAsync(secondUserId, FixedUtcNow.AddDays(30));
        using SignInSessionPruneService pruneService = CreatePruneService();

        // Act
        await pruneService.PruneOnceAsync(CancellationToken.None);

        // Assert
        List<Guid> remaining = await SessionIdsAsync();
        remaining.ShouldBe([firstLive, secondLive], ignoreOrder: true);
    }

    [Fact]
    public async Task PruneOnceAsync_ShouldNotThrow_WhenTheDatabaseFails()
    {
        // Arrange: an exception leaving the background job would stop the whole host
        Guid userId = await RegisterUserAsync();
        Guid expiredSessionId = await AddSessionAsync(userId, FixedUtcNow.AddDays(-1));
        Factory.DbCommandFailures.FailNext(
            command => command.CommandText.Contains(
                $"DELETE FROM {DatabaseSchemas.Auth}.\"SignInSessions\"", StringComparison.Ordinal),
            () => new PostgresException(
                "simulated permanent failure", "ERROR", "ERROR", PostgresErrorCodes.DataCorrupted));
        using SignInSessionPruneService pruneService = CreatePruneService();

        // Act & Assert
        await Should.NotThrowAsync(() => pruneService.PruneOnceAsync(CancellationToken.None));
        Factory.DbCommandFailures.FailuresInjected.ShouldBe(1);
        (await SessionExistsAsync(expiredSessionId)).ShouldBeTrue();
    }

    [Fact]
    public async Task PruneOnceAsync_ShouldLetAShutdownThrough()
    {
        // Arrange: a shutdown is not a failure, so it must not be swallowed and logged as one
        using CancellationTokenSource shutdown = new();
        await shutdown.CancelAsync();
        using SignInSessionPruneService pruneService = CreatePruneService();

        // Act & Assert
        await Should.ThrowAsync<OperationCanceledException>(() => pruneService.PruneOnceAsync(shutdown.Token));
    }

    [Fact]
    public async Task PruneOnceAsync_ShouldSwallowACancellationThatIsNotAShutdown()
    {
        // Arrange: a cancellation the host did not ask for is a failure like any other
        Guid userId = await RegisterUserAsync();
        Guid expiredSessionId = await AddSessionAsync(userId, FixedUtcNow.AddDays(-1));
        Factory.DbCommandFailures.FailNext(
            command => command.CommandText.Contains(
                $"DELETE FROM {DatabaseSchemas.Auth}.\"SignInSessions\"", StringComparison.Ordinal),
            () => new OperationCanceledException());
        using SignInSessionPruneService pruneService = CreatePruneService();

        // Act & Assert
        await Should.NotThrowAsync(() => pruneService.PruneOnceAsync(CancellationToken.None));
        Factory.DbCommandFailures.FailuresInjected.ShouldBe(1);
        (await SessionExistsAsync(expiredSessionId)).ShouldBeTrue();
    }

    [Fact]
    public async Task StartAsync_StoppedBeforeTheStartupDelayEnds_NeverPrunes()
    {
        // Arrange: the real clock, so the one-minute startup delay cannot end within this test
        Guid userId = await RegisterUserAsync();
        Guid expiredSessionId = await AddSessionAsync(userId, DateTimeOffset.UtcNow.AddDays(-1));
        using SignInSessionPruneService pruneService = new(
            Factory.Services.GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System,
            NullLogger<SignInSessionPruneService>.Instance);

        // Act
        await pruneService.StartAsync(CancellationToken.None);
        await pruneService.StopAsync(CancellationToken.None);

        // Assert
        (await SessionExistsAsync(expiredSessionId)).ShouldBeTrue();
    }

    private SignInSessionPruneService CreatePruneService()
    {
        TimeProvider timeProvider = Substitute.For<TimeProvider>();
        timeProvider.GetUtcNow().Returns(FixedUtcNow);

        return new SignInSessionPruneService(
            Factory.Services.GetRequiredService<IServiceScopeFactory>(),
            timeProvider,
            NullLogger<SignInSessionPruneService>.Instance);
    }

    private async Task<Guid> RegisterUserAsync()
    {
        IdentityId identityId =
            await UserFactory.RegisterRandomUserAsync(ApiClient, Faker, AccountConfirmationEmailSpy);
        return identityId.Value;
    }

    private async Task<Guid> AddSessionAsync(Guid userId, DateTimeOffset expiresAt)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        SignInSession session = SignInSession.Create(userId, [1, 2, 3], expiresAt);
        db.SignInSessions.Add(session);
        await db.SaveChangesAsync();
        return session.Id;
    }

    private async Task<bool> SessionExistsAsync(Guid sessionId)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        return await db.SignInSessions.AnyAsync(session => session.Id == sessionId);
    }

    private async Task<List<Guid>> SessionIdsAsync()
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        return await db.SignInSessions.Select(session => session.Id).ToListAsync();
    }
}
