using System.Collections.Concurrent;
using LotroKoniecDev.TranslationSystem.API.Features.TranslationFiles;
using Microsoft.Extensions.Logging.Abstractions;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Unit.Tests.Features.TranslationFiles;

public sealed class TranslationFileRebuildWorkerTests
{
    private static readonly TimeSpan ConvergenceTimeout = TimeSpan.FromSeconds(10);

    private readonly TranslationFileRebuildScheduler _scheduler = new();

    private TranslationFileRebuildWorker CreateWorker(
        IPrecomputedTranslationFileProjector projector,
        TimeSpan? debounceWindow = null)
        => new(
            _scheduler,
            projector,
            TimeProvider.System,
            Microsoft.Extensions.Options.Options.Create(new TranslationFileRebuildSettings
            {
                DebounceWindow = debounceWindow ?? TimeSpan.FromMilliseconds(50),
            }),
            NullLogger<TranslationFileRebuildWorker>.Instance);

    private async Task WaitUntilIdleAsync()
    {
        using CancellationTokenSource timeout = new(ConvergenceTimeout);
        while (_scheduler.PendingCount > 0)
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    [Fact]
    public async Task ExecuteAsync_BurstOfSignals_ShouldCoalesceIntoOneRebuild()
    {
        // Arrange: five approves land within one debounce window.
        RecordingProjector projector = new();
        using TranslationFileRebuildWorker worker = CreateWorker(projector);
        for (int signal = 0; signal < 5; signal++)
        {
            _scheduler.Schedule("pl");
        }

        // Act
        await worker.StartAsync(CancellationToken.None);
        await projector.FirstCompletedRebuild.WaitAsync(ConvergenceTimeout);
        await WaitUntilIdleAsync();
        await worker.StopAsync(CancellationToken.None);

        // Assert: the queue was fully drained by a single O(N) rebuild, not five serialized ones.
        projector.CallCount.ShouldBe(1);
        projector.Languages.ShouldBe(["pl"]);
        _scheduler.PendingCount.ShouldBe(0);
    }

    [Fact]
    public async Task ExecuteAsync_SignalsForDistinctLanguages_ShouldRebuildEachOnce()
    {
        // Arrange: dirty signals for two languages inside one window.
        RecordingProjector projector = new();
        using TranslationFileRebuildWorker worker = CreateWorker(projector);
        _scheduler.Schedule("pl");
        _scheduler.Schedule("en");
        _scheduler.Schedule("pl");

        // Act
        await worker.StartAsync(CancellationToken.None);
        await projector.FirstCompletedRebuild.WaitAsync(ConvergenceTimeout);
        await WaitUntilIdleAsync();
        await worker.StopAsync(CancellationToken.None);

        // Assert
        projector.CallCount.ShouldBe(2);
        projector.Languages.Order().ShouldBe(["en", "pl"]);
    }

    [Fact]
    public async Task ExecuteAsync_WhenRebuildFails_ShouldRescheduleUntilTheArtifactConverges()
    {
        // Arrange: the first rebuild attempt hits a transient fault.
        RecordingProjector projector = new(failuresBeforeSuccess: 1);
        using TranslationFileRebuildWorker worker = CreateWorker(projector);
        _scheduler.Schedule("pl");

        // Act
        await worker.StartAsync(CancellationToken.None);
        await projector.FirstCompletedRebuild.WaitAsync(ConvergenceTimeout);
        await WaitUntilIdleAsync();
        await worker.StopAsync(CancellationToken.None);

        // Assert: the failed pass was retried on the next debounce window; the signal is only
        // considered done once a rebuild actually succeeded.
        projector.CallCount.ShouldBe(2);
        _scheduler.PendingCount.ShouldBe(0);
    }

    [Fact]
    public async Task ExecuteAsync_WhenRebuildThrowsCancellationWhileHostIsRunning_ShouldRescheduleInsteadOfStopping()
    {
        // Arrange: a transient fault surfacing as OCE (e.g. a cancelled DB command) while the host
        // is NOT shutting down; only a shutdown cancellation may escape the worker loop.
        RecordingProjector projector = new(
            failuresBeforeSuccess: 1,
            faultFactory: () => new OperationCanceledException("Transient command cancellation."));
        using TranslationFileRebuildWorker worker = CreateWorker(projector);
        _scheduler.Schedule("pl");

        // Act
        await worker.StartAsync(CancellationToken.None);
        await projector.FirstCompletedRebuild.WaitAsync(ConvergenceTimeout);
        await WaitUntilIdleAsync();
        await worker.StopAsync(CancellationToken.None);

        // Assert: the loop survived the rogue OCE and retried to convergence.
        projector.CallCount.ShouldBe(2);
        _scheduler.PendingCount.ShouldBe(0);
    }

    [Fact]
    public async Task ExecuteAsync_WithZeroDebounceWindow_ShouldStillRebuild()
    {
        // Arrange: zero is a valid setting that disables the coalescing wait entirely.
        RecordingProjector projector = new();
        using TranslationFileRebuildWorker worker = CreateWorker(projector, TimeSpan.Zero);
        _scheduler.Schedule("pl");

        // Act
        await worker.StartAsync(CancellationToken.None);
        await projector.FirstCompletedRebuild.WaitAsync(ConvergenceTimeout);
        await WaitUntilIdleAsync();
        await worker.StopAsync(CancellationToken.None);

        // Assert
        projector.CallCount.ShouldBe(1);
        _scheduler.PendingCount.ShouldBe(0);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldRebuildOnTheWorkerLifetimeToken()
    {
        // Arrange
        RecordingProjector projector = new();
        using TranslationFileRebuildWorker worker = CreateWorker(projector);
        _scheduler.Schedule("pl");

        // Act
        await worker.StartAsync(CancellationToken.None);
        await projector.FirstCompletedRebuild.WaitAsync(ConvergenceTimeout);
        bool cancelledWhileRunning = projector.LastToken.IsCancellationRequested;
        await worker.StopAsync(CancellationToken.None);

        // Assert: the projector ran on the host stopping token (live while the app runs, cancelled
        // only at shutdown), so an aborted HTTP request can never cancel a scheduled rebuild.
        cancelledWhileRunning.ShouldBeFalse();
        projector.LastToken.IsCancellationRequested.ShouldBeTrue();
    }

    private sealed class RecordingProjector : IPrecomputedTranslationFileProjector
    {
        private readonly int _failuresBeforeSuccess;
        private readonly Func<Exception> _faultFactory;
        private readonly ConcurrentQueue<string> _languages = new();
        private readonly TaskCompletionSource _firstCompletedRebuild =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _callCount;

        public RecordingProjector(int failuresBeforeSuccess = 0, Func<Exception>? faultFactory = null)
        {
            _failuresBeforeSuccess = failuresBeforeSuccess;
            _faultFactory = faultFactory ?? (() => new InvalidOperationException("Transient rebuild failure."));
        }

        public int CallCount => Volatile.Read(ref _callCount);

        public IReadOnlyList<string> Languages => [.. _languages];

        public CancellationToken LastToken { get; private set; }

        /// <summary>Completes on the first rebuild that succeeds (failed attempts don't count).</summary>
        public Task FirstCompletedRebuild => _firstCompletedRebuild.Task;

        public Task RebuildAsync(string language, CancellationToken cancellationToken)
        {
            int call = Interlocked.Increment(ref _callCount);
            _languages.Enqueue(language);
            LastToken = cancellationToken;

            if (call <= _failuresBeforeSuccess)
            {
                throw _faultFactory();
            }

            _firstCompletedRebuild.TrySetResult();
            return Task.CompletedTask;
        }
    }
}
