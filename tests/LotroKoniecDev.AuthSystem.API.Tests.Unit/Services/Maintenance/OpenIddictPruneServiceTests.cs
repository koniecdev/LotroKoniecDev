using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OpenIddict.Abstractions;
using LotroKoniecDev.AuthSystem.API.Services.Maintenance;
using LotroKoniecDev.AuthSystem.API.Tests.Unit.Shared;
using LotroKoniecDev.Tests.Shared;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Services.Maintenance;

/// <summary>
/// When the token prune runs (#948): once a minute after startup, then once a day. A failed pass is
/// logged and never stops the next one, and stopping the app ends the job. The integration host does
/// not run this job. The integration suite tests one pass: its threshold, order and failure handling
/// in the class of the same name, and what it deletes in <c>OpenIddictPruneServiceIntegrationTests</c>.
/// </summary>
public sealed class OpenIddictPruneServiceTests
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Interval = TimeSpan.FromDays(1);
    private static readonly TimeSpan OneMillisecond = TimeSpan.FromMilliseconds(1);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);

    private readonly IOpenIddictTokenManager _tokenManager = Substitute.For<IOpenIddictTokenManager>();
    private readonly IOpenIddictAuthorizationManager _authorizationManager =
        Substitute.For<IOpenIddictAuthorizationManager>();
    private readonly TimerWatchingTimeProvider _clock = new();
    private readonly RunRecorder _runs = new();

    [Fact]
    public async Task ExecuteAsync_ShouldStartPruning_OnlyOnceTheStartupDelayHasPassed()
    {
        // Arrange
        ArrangeRuns(failingRun: null);
        await using ServiceProvider serviceProvider = BuildServiceProvider();
        using OpenIddictPruneService sut = CreateSut(serviceProvider, NullLoggerFactory.Instance);

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
        ArrangeRuns(failingRun: null);
        await using ServiceProvider serviceProvider = BuildServiceProvider();
        using OpenIddictPruneService sut = CreateSut(serviceProvider, NullLoggerFactory.Instance);

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
    /// Pass 1 is the one after the startup delay, which happens before the daily timer exists. Pass 2 is
    /// the first one on the daily timer.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ExecuteAsync_ShouldLogTheFailureAndPruneTheNextDay_WhenAPassFails(int failingRun)
    {
        // Arrange
        ArrangeRuns(failingRun);
        using CapturingLoggerFactory loggerFactory = new();
        await using ServiceProvider serviceProvider = BuildServiceProvider();
        using OpenIddictPruneService sut = CreateSut(serviceProvider, loggerFactory);

        // Act
        await StartAndWaitForTheFirstPassAsync(sut);
        _clock.Advance(Interval);
        await _runs.WaitForNextRunAsync();
        _clock.Advance(Interval);
        await _runs.WaitForNextRunAsync();
        await StopAsync(sut);

        // Assert
        _runs.Count.ShouldBe(3);
        CapturingLoggerFactory.LogEntry error = loggerFactory.Entries
            .Where(entry => entry.Level >= LogLevel.Warning)
            .ShouldHaveSingleItem();
        error.Level.ShouldBe(LogLevel.Error);
        error.EventId.Id.ShouldBe(EventIds.OpenIddictPruneFailed);
        error.Exception.ShouldBeOfType<InvalidOperationException>();
    }

    [Fact]
    public async Task StopAsync_ShouldEndTheJobWithoutPruning_DuringTheStartupDelay()
    {
        // Arrange
        ArrangeRuns(failingRun: null);
        await using ServiceProvider serviceProvider = BuildServiceProvider();
        using OpenIddictPruneService sut = CreateSut(serviceProvider, NullLoggerFactory.Instance);

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
        ArrangeRuns(failingRun: null);
        await using ServiceProvider serviceProvider = BuildServiceProvider();
        using OpenIddictPruneService sut = CreateSut(serviceProvider, NullLoggerFactory.Instance);

        // Act
        await StartAndWaitForTheFirstPassAsync(sut);
        await StopAsync(sut);

        // Assert
        sut.ExecuteTask.ShouldNotBeNull().IsCompleted.ShouldBeTrue();
    }

    private void ArrangeRuns(int? failingRun)
    {
        _tokenManager.PruneAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(_ => _runs.Record() == failingRun
                ? ValueTask.FromException<long>(new InvalidOperationException("The database is down."))
                : ValueTask.FromResult(0L));
    }

    private async Task StartAndWaitForTheFirstPassAsync(OpenIddictPruneService sut)
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
        services.AddScoped(_ => _tokenManager);
        services.AddScoped(_ => _authorizationManager);
        return services.BuildServiceProvider();
    }

    private OpenIddictPruneService CreateSut(ServiceProvider serviceProvider, ILoggerFactory loggerFactory)
        => new(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            _clock,
            new Logger<OpenIddictPruneService>(loggerFactory));

    private static async Task StopAsync(BackgroundService sut)
    {
        using CancellationTokenSource stopTimeout = new(StopTimeout);
        await sut.StopAsync(stopTimeout.Token);
    }
}
