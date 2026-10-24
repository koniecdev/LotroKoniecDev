using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using LotroKoniecDev.AuthSystem.API.Services.Maintenance;
using LotroKoniecDev.AuthSystem.API.Tests.Unit.Shared;
using LotroKoniecDev.AuthSystem.Persistence.DbContexts;
using LotroKoniecDev.Tests.Shared;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Services.Maintenance;

/// <summary>
/// When the sign-in session prune runs (#948): a minute after startup, then once a day, and stopping the app
/// ends the job. The integration host does not run it. With no database every pass fails, so these tests
/// also prove that a failed pass is logged and does not stop the next one.
/// </summary>
public sealed class SignInSessionPruneServiceTests
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Interval = TimeSpan.FromDays(1);
    private static readonly TimeSpan OneMillisecond = TimeSpan.FromMilliseconds(1);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);

    private readonly TimerWatchingTimeProvider _clock = new();
    private readonly RunRecorder _runs = new();

    [Fact]
    public async Task ExecuteAsync_ShouldStartPruning_OnlyOnceTheStartupDelayHasPassed()
    {
        // Arrange
        await using ServiceProvider serviceProvider = BuildServiceProvider();
        using SignInSessionPruneService sut = CreateSut(serviceProvider, NullLoggerFactory.Instance);

        // Act
        await sut.StartAsync(CancellationToken.None);
        await _clock.WaitForNextTimerAsync();
        _clock.Advance(StartupDelay - OneMillisecond);
        int runsBeforeTheDelay = _runs.Count;
        int wakeUpsBeforeTheDelay = _clock.WakeUps;
        _clock.Advance(OneMillisecond);
        await _runs.WaitForNextRunAsync();
        await _clock.WaitForNextTimerAsync();

        // Assert
        runsBeforeTheDelay.ShouldBe(0);
        wakeUpsBeforeTheDelay.ShouldBe(0);
        _runs.Count.ShouldBe(1);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldPruneAgain_OnlyOnceADayHasPassed()
    {
        // Arrange
        await using ServiceProvider serviceProvider = BuildServiceProvider();
        using SignInSessionPruneService sut = CreateSut(serviceProvider, NullLoggerFactory.Instance);

        // Act
        await StartAndWaitForTheFirstPassAsync(sut);
        _clock.Advance(Interval - OneMillisecond);
        int wakeUpsBeforeTheInterval = _clock.WakeUps;
        _clock.Advance(OneMillisecond);
        await _runs.WaitForNextRunAsync();

        // Assert: the one wake-up is the startup delay's
        wakeUpsBeforeTheInterval.ShouldBe(1);
        _runs.Count.ShouldBe(2);
    }

    /// <summary>
    /// Pass 1 fails before the daily timer exists, and pass 2 fails on it.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ShouldLogEachFailureAndPruneTheNextDay_WhenPassesFail()
    {
        // Arrange
        using CapturingLoggerFactory loggerFactory = new();
        await using ServiceProvider serviceProvider = BuildServiceProvider();
        using SignInSessionPruneService sut = CreateSut(serviceProvider, loggerFactory);

        // Act
        await StartAndWaitForTheFirstPassAsync(sut);
        _clock.Advance(Interval);
        await _runs.WaitForNextRunAsync();
        _clock.Advance(Interval);
        await _runs.WaitForNextRunAsync();
        await StopAsync(sut);

        // Assert
        _runs.Count.ShouldBe(3);
        loggerFactory.Entries
            .Where(entry => entry.Level >= LogLevel.Warning)
            .Select(entry => (entry.Level, entry.EventId.Id))
            .ShouldBe(Enumerable.Repeat((LogLevel.Error, EventIds.SignInSessionPruneFailed), 3));
    }

    [Fact]
    public async Task StopAsync_ShouldEndTheJobWithoutPruning_DuringTheStartupDelay()
    {
        // Arrange
        await using ServiceProvider serviceProvider = BuildServiceProvider();
        using SignInSessionPruneService sut = CreateSut(serviceProvider, NullLoggerFactory.Instance);

        // Act
        await sut.StartAsync(CancellationToken.None);
        await _clock.WaitForNextTimerAsync();
        await StopAsync(sut);

        // Assert
        sut.ExecuteTask.ShouldNotBeNull().IsCompleted.ShouldBeTrue();
        _runs.Count.ShouldBe(0);
    }

    [Fact]
    public async Task StopAsync_ShouldEndTheJob_WhileItWaitsForTheNextDay()
    {
        // Arrange
        await using ServiceProvider serviceProvider = BuildServiceProvider();
        using SignInSessionPruneService sut = CreateSut(serviceProvider, NullLoggerFactory.Instance);

        // Act
        await StartAndWaitForTheFirstPassAsync(sut);
        await StopAsync(sut);

        // Assert
        sut.ExecuteTask.ShouldNotBeNull().IsCompleted.ShouldBeTrue();
    }

    private async Task StartAndWaitForTheFirstPassAsync(SignInSessionPruneService sut)
    {
        await sut.StartAsync(CancellationToken.None);
        await _clock.WaitForNextTimerAsync();
        _clock.Advance(StartupDelay);
        await _runs.WaitForNextRunAsync();
        await _clock.WaitForNextTimerAsync();
    }

    private ServiceProvider BuildServiceProvider()
    {
        ServiceCollection services = new();
        services.AddScoped<AuthDbContext>(_ =>
        {
            _runs.Record();
            throw new InvalidOperationException("The database is down.");
        });
        return services.BuildServiceProvider();
    }

    private SignInSessionPruneService CreateSut(ServiceProvider serviceProvider, ILoggerFactory loggerFactory)
        => new(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            _clock,
            new Logger<SignInSessionPruneService>(loggerFactory));

    private static async Task StopAsync(BackgroundService sut)
    {
        using CancellationTokenSource stopTimeout = new(StopTimeout);
        await sut.StopAsync(stopTimeout.Token);
    }
}
