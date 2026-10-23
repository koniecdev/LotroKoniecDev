using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using LotroKoniecDev.AuthSystem.API.BackgroundServices;
using LotroKoniecDev.AuthSystem.API.Outbox;
using LotroKoniecDev.AuthSystem.API.Tests.Unit.Shared;
using LotroKoniecDev.AuthSystem.Infrastructure.Messaging;
using LotroKoniecDev.AuthSystem.Persistence.DbContexts;
using LotroKoniecDev.Tests.Shared;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.BackgroundServices;

/// <summary>
/// How long the relay waits for a signal (#1185, ADR-0035): six hours after a clean pass, and a growing wait
/// after a failed one that stops at six hours. A signal ends any wait. Where a pass asks for the database
/// context, each test decides whether it fails or finds no pending rows.
/// </summary>
public sealed class OutboxRelayTests : IDisposable
{
    private static readonly TimeSpan SafetySweepInterval = TimeSpan.FromHours(6);
    private static readonly TimeSpan OneMillisecond = TimeSpan.FromMilliseconds(1);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);

    private static readonly DbContextOptions<AuthDbContext> EmptyDatabaseOptions =
        new DbContextOptionsBuilder<AuthDbContext>()
            .UseNpgsql("Host=outbox-relay-tests.invalid")
            .AddInterceptors(new EmptyDatabaseInterceptor())
            .Options;

    private readonly TimerWatchingTimeProvider _clock = new();
    private readonly RunRecorder _passes = new();
    private readonly OutboxSignal _signal = new();

    public void Dispose()
    {
        _signal.Dispose();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldSweepAgain_OnlyOnceSixHoursHavePassedWithoutASignal()
    {
        // Arrange
        await using ServiceProvider serviceProvider = BuildServiceProvider(failingPasses: []);
        using OutboxRelay sut = CreateSut(serviceProvider, NullLoggerFactory.Instance);

        // Act
        await StartAndWaitForTheStartupPassAsync(sut);
        _clock.Advance(SafetySweepInterval - OneMillisecond);
        int wakeUpsBeforeTheInterval = _clock.WakeUps;
        _clock.Advance(OneMillisecond);
        await _passes.WaitForNextRunAsync();
        await _clock.WaitForNextTimerAsync();

        // Assert
        wakeUpsBeforeTheInterval.ShouldBe(0);
        _passes.Count.ShouldBe(2);
    }

    /// <summary>
    /// The sixth wait proves the ceiling: a row that always fails is tried at the sweep pace and no faster.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ShouldWaitLongerAfterEachFailedPass_UpToTheSafetySweepInterval()
    {
        // Arrange
        TimeSpan[] expectedWaits =
        [
            TimeSpan.FromSeconds(30),
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(5),
            TimeSpan.FromMinutes(30),
            SafetySweepInterval,
            SafetySweepInterval
        ];
        await using ServiceProvider serviceProvider = BuildServiceProvider(failingPasses: [1, 2, 3, 4, 5, 6, 7]);
        using OutboxRelay sut = CreateSut(serviceProvider, NullLoggerFactory.Instance);

        // Act
        await StartAndWaitForTheStartupPassAsync(sut);
        IReadOnlyList<int> wakeUpsJustBeforeEachWaitEnds = await WaitThroughAsync(expectedWaits);

        // Assert: no wait ended early, and each next pass came on time
        wakeUpsJustBeforeEachWaitEnds.ShouldBe([0, 1, 2, 3, 4, 5]);
        _passes.Count.ShouldBe(expectedWaits.Length + 1);
    }

    /// <summary>
    /// The third pass is clean, so the fourth pass's failure starts the backoff over from its first step.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ShouldStartTheBackoffOver_WhenACleanPassComesBetweenFailures()
    {
        // Arrange
        TimeSpan[] expectedWaits =
        [
            TimeSpan.FromSeconds(30),
            TimeSpan.FromMinutes(1),
            SafetySweepInterval,
            TimeSpan.FromSeconds(30)
        ];
        await using ServiceProvider serviceProvider = BuildServiceProvider(failingPasses: [1, 2, 4]);
        using OutboxRelay sut = CreateSut(serviceProvider, NullLoggerFactory.Instance);

        // Act
        await StartAndWaitForTheStartupPassAsync(sut);
        IReadOnlyList<int> wakeUpsJustBeforeEachWaitEnds = await WaitThroughAsync(expectedWaits);

        // Assert
        wakeUpsJustBeforeEachWaitEnds.ShouldBe([0, 1, 2, 3]);
        _passes.Count.ShouldBe(expectedWaits.Length + 1);
    }

    /// <summary>
    /// A signal drives the relay (ADR-0035), so it ends the sweep's wait and a backoff wait alike.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_ShouldRunAPassAtOnce_WhenSignalledDuringTheWait(bool startupPassFails)
    {
        // Arrange
        await using ServiceProvider serviceProvider = BuildServiceProvider(failingPasses: startupPassFails ? [1] : []);
        using OutboxRelay sut = CreateSut(serviceProvider, NullLoggerFactory.Instance);

        // Act
        await StartAndWaitForTheStartupPassAsync(sut);
        _signal.Notify();
        await _passes.WaitForNextRunAsync();
        await _clock.WaitForNextTimerAsync();

        // Assert
        _clock.WakeUps.ShouldBe(0);
        _passes.Count.ShouldBe(2);
    }

    [Fact]
    public async Task StopAsync_ShouldEndTheRelayWithoutLoggingAFailure_WhileItWaits()
    {
        // Arrange
        using CapturingLoggerFactory loggerFactory = new();
        await using ServiceProvider serviceProvider = BuildServiceProvider(failingPasses: []);
        using OutboxRelay sut = CreateSut(serviceProvider, loggerFactory);

        // Act
        await StartAndWaitForTheStartupPassAsync(sut);
        await StopAsync(sut);

        // Assert
        sut.ExecuteTask.ShouldNotBeNull().IsCompletedSuccessfully.ShouldBeTrue();
        loggerFactory.Entries.ShouldNotContain(entry => entry.Level >= LogLevel.Warning);
    }

    private async Task StartAndWaitForTheStartupPassAsync(OutboxRelay sut)
    {
        await sut.StartAsync(CancellationToken.None);
        await _passes.WaitForNextRunAsync();
        await _clock.WaitForNextTimerAsync();
    }

    /// <summary>
    /// Returns how many waits had ended a millisecond before each was due. A wait that is too long stops the
    /// next pass, and <see cref="RunRecorder.WaitForNextRunAsync"/> fails the test.
    /// </summary>
    private async Task<IReadOnlyList<int>> WaitThroughAsync(IEnumerable<TimeSpan> waits)
    {
        List<int> wakeUpsJustBeforeEachWaitEnds = [];

        foreach (TimeSpan wait in waits)
        {
            _clock.Advance(wait - OneMillisecond);
            wakeUpsJustBeforeEachWaitEnds.Add(_clock.WakeUps);
            _clock.Advance(OneMillisecond);
            await _passes.WaitForNextRunAsync();
            await _clock.WaitForNextTimerAsync();
        }

        return wakeUpsJustBeforeEachWaitEnds;
    }

    /// <param name="failingPasses">
    /// The numbers of the passes, starting at 1 for the startup pass, that fail where they ask for the
    /// database. Every other pass finds no pending rows.
    /// </param>
    private ServiceProvider BuildServiceProvider(int[] failingPasses)
    {
        ServiceCollection services = new();
        services.AddScoped<AuthDbContext>(_ =>
        {
            int pass = _passes.Record();
            return failingPasses.Contains(pass)
                ? throw new InvalidOperationException("The database is down.")
                : new AuthDbContext(EmptyDatabaseOptions);
        });
        return services.BuildServiceProvider();
    }

    private OutboxRelay CreateSut(ServiceProvider serviceProvider, ILoggerFactory loggerFactory)
        => new(
            Substitute.For<IMessagePublisher>(),
            _signal,
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            _clock,
            new Logger<OutboxRelay>(loggerFactory));

    private static async Task StopAsync(BackgroundService sut)
    {
        using CancellationTokenSource stopTimeout = new(StopTimeout);
        await sut.StopAsync(stopTimeout.Token);
    }

    /// <summary>
    /// A database with no rows. It opens no connection and answers every query with an empty result.
    /// </summary>
    private sealed class EmptyDatabaseInterceptor : DbCommandInterceptor, IDbConnectionInterceptor
    {
        public ValueTask<InterceptionResult> ConnectionOpeningAsync(
            DbConnection connection,
            ConnectionEventData eventData,
            InterceptionResult result,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(InterceptionResult.Suppress());

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(InterceptionResult<DbDataReader>.SuppressWithResult(new DataTable().CreateDataReader()));
    }
}
