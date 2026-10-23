using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using LotroKoniecDev.AuthSystem.API.BackgroundServices;
using LotroKoniecDev.AuthSystem.API.Services.Gdpr;
using LotroKoniecDev.AuthSystem.API.Settings;
using LotroKoniecDev.AuthSystem.API.Tests.Unit.Shared;
using LotroKoniecDev.Tests.Shared;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.BackgroundServices;

/// <summary>
/// When the GDPR erasure runs (#948): at startup, then once per configured interval. A failed run never stops
/// the next, and stopping the app ends the job. The integration host does not run this job, so these tests
/// are the only guard on its timing.
/// </summary>
public sealed class AccountDeletionFinalizerHostedServiceTests
{
    /// <summary>
    /// Not the one-day default, so a job that ignores the setting fails these tests.
    /// </summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromHours(6);

    private static readonly TimeSpan OneMillisecond = TimeSpan.FromMilliseconds(1);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);

    private readonly IAccountDeletionFinalizer _finalizer = Substitute.For<IAccountDeletionFinalizer>();
    private readonly TimerWatchingTimeProvider _clock = new();
    private readonly RunRecorder _runs = new();

    [Fact]
    public async Task ExecuteAsync_ShouldRunOnce_AtStartupBeforeAnyTimePasses()
    {
        // Arrange
        ArrangeRuns((_, _) => Task.FromResult(0));
        await using ServiceProvider serviceProvider = BuildServiceProvider();
        using AccountDeletionFinalizerHostedService sut = CreateSut(serviceProvider, NullLoggerFactory.Instance);

        // Act
        await sut.StartAsync(CancellationToken.None);
        await _runs.WaitForNextRunAsync();
        await _clock.WaitForNextTimerAsync();

        // Assert
        _runs.Count.ShouldBe(1);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldRunAgain_OnlyOnceTheConfiguredIntervalHasPassed()
    {
        // Arrange
        ArrangeRuns((_, _) => Task.FromResult(0));
        await using ServiceProvider serviceProvider = BuildServiceProvider();
        using AccountDeletionFinalizerHostedService sut = CreateSut(serviceProvider, NullLoggerFactory.Instance);

        // Act
        await sut.StartAsync(CancellationToken.None);
        await _runs.WaitForNextRunAsync();
        await _clock.WaitForNextTimerAsync();
        _clock.Advance(PollInterval - OneMillisecond);
        int wakeUpsBeforeTheInterval = _clock.WakeUps;
        _clock.Advance(OneMillisecond);
        await _runs.WaitForNextRunAsync();

        // Assert
        wakeUpsBeforeTheInterval.ShouldBe(0);
        _runs.Count.ShouldBe(2);
    }

    /// <summary>
    /// Run 1 is the startup run, before the timer exists; run 2 is the first on the timer. A cancellation the
    /// app did not ask for is a failure too: escaping a timer run, it would end the loop like a shutdown, with
    /// no log line.
    /// </summary>
    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public async Task ExecuteAsync_ShouldLogTheFailureAndRunOnTheNextInterval_WhenARunFails(int failingRun, bool failsWithCancellation)
    {
        // Arrange
        Exception failure = failsWithCancellation
            ? new OperationCanceledException()
            : new InvalidOperationException("The database is down.");
        ArrangeRuns((run, _) => run == failingRun ? Task.FromException<int>(failure) : Task.FromResult(0));
        using CapturingLoggerFactory loggerFactory = new();
        await using ServiceProvider serviceProvider = BuildServiceProvider();
        using AccountDeletionFinalizerHostedService sut = CreateSut(serviceProvider, loggerFactory);

        // Act
        await sut.StartAsync(CancellationToken.None);
        await _runs.WaitForNextRunAsync();
        await _clock.WaitForNextTimerAsync();
        _clock.Advance(PollInterval);
        await _runs.WaitForNextRunAsync();
        _clock.Advance(PollInterval);
        await _runs.WaitForNextRunAsync();
        await StopAsync(sut);

        // Assert
        _runs.Count.ShouldBe(3);
        CapturingLoggerFactory.LogEntry error = loggerFactory.Entries
            .Where(entry => entry.Level >= LogLevel.Warning)
            .ShouldHaveSingleItem();
        error.Level.ShouldBe(LogLevel.Error);
        error.EventId.Id.ShouldBe(EventIds.GdprDeletionFinalizerRunFailed);
        error.Exception.ShouldBeSameAs(failure);
    }

    [Fact]
    public async Task StopAsync_ShouldEndTheJob_WhileItWaitsForTheNextInterval()
    {
        // Arrange
        ArrangeRuns((_, _) => Task.FromResult(0));
        await using ServiceProvider serviceProvider = BuildServiceProvider();
        using AccountDeletionFinalizerHostedService sut = CreateSut(serviceProvider, NullLoggerFactory.Instance);

        // Act
        await sut.StartAsync(CancellationToken.None);
        await _runs.WaitForNextRunAsync();
        await _clock.WaitForNextTimerAsync();
        await StopAsync(sut);

        // Assert
        sut.ExecuteTask.ShouldNotBeNull().IsCompleted.ShouldBeTrue();
    }

    /// <summary>
    /// A deploy can stop the app mid-run, and that cancellation is not a failure.
    /// </summary>
    [Fact]
    public async Task StopAsync_ShouldEndTheJobWithoutLoggingAFailure_WhenTheAppStopsDuringTheStartupRun()
    {
        // Arrange
        ArrangeRuns((_, cancellationToken) => WaitUntilCancelledAsync(cancellationToken));
        using CapturingLoggerFactory loggerFactory = new();
        await using ServiceProvider serviceProvider = BuildServiceProvider();
        using AccountDeletionFinalizerHostedService sut = CreateSut(serviceProvider, loggerFactory);

        // Act
        await sut.StartAsync(CancellationToken.None);
        await _runs.WaitForNextRunAsync();
        await StopAsync(sut);

        // Assert
        sut.ExecuteTask.ShouldNotBeNull().IsCompleted.ShouldBeTrue();
        loggerFactory.Entries.ShouldNotContain(entry => entry.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task StopAsync_ShouldEndTheJobWithoutLoggingAFailure_WhenTheAppStopsDuringARunOnTheTimer()
    {
        // Arrange
        ArrangeRuns((run, cancellationToken) => run == 2
            ? WaitUntilCancelledAsync(cancellationToken)
            : Task.FromResult(0));
        using CapturingLoggerFactory loggerFactory = new();
        await using ServiceProvider serviceProvider = BuildServiceProvider();
        using AccountDeletionFinalizerHostedService sut = CreateSut(serviceProvider, loggerFactory);

        // Act
        await sut.StartAsync(CancellationToken.None);
        await _runs.WaitForNextRunAsync();
        await _clock.WaitForNextTimerAsync();
        _clock.Advance(PollInterval);
        await _runs.WaitForNextRunAsync();
        await StopAsync(sut);

        // Assert
        sut.ExecuteTask.ShouldNotBeNull().IsCompleted.ShouldBeTrue();
        loggerFactory.Entries.ShouldNotContain(entry => entry.Level >= LogLevel.Warning);
    }

    private void ArrangeRuns(Func<int, CancellationToken, Task<int>> run)
    {
        _finalizer.FinalizeDueAccountsAsync(Arg.Any<CancellationToken>())
            .Returns(callInfo => run(_runs.Record(), callInfo.Arg<CancellationToken>()));
    }

    private ServiceProvider BuildServiceProvider()
    {
        ServiceCollection services = new();
        services.AddScoped(_ => _finalizer);
        return services.BuildServiceProvider();
    }

    private AccountDeletionFinalizerHostedService CreateSut(ServiceProvider serviceProvider, ILoggerFactory loggerFactory)
        => new(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            _clock,
            Microsoft.Extensions.Options.Options.Create(new GdprSettings { DeletionFinalizationPollInterval = PollInterval }),
            new Logger<AccountDeletionFinalizerHostedService>(loggerFactory));

    private static async Task StopAsync(BackgroundService sut)
    {
        using CancellationTokenSource stopTimeout = new(StopTimeout);
        await sut.StopAsync(stopTimeout.Token);
    }

    private static async Task<int> WaitUntilCancelledAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        return 0;
    }
}
