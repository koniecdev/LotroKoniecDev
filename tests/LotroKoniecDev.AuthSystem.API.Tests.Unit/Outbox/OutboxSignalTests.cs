using LotroKoniecDev.AuthSystem.API.Outbox;
using LotroKoniecDev.AuthSystem.API.Tests.Unit.Shared;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Outbox;

public sealed class OutboxSignalTests
{
    private static readonly TimeSpan SafetySweepInterval = TimeSpan.FromHours(6);
    private static readonly TimeSpan OneMillisecond = TimeSpan.FromMilliseconds(1);
    private static readonly TimeSpan RealTimeLimit = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task WaitAsync_NotifiedBeforehand_ReturnsTrueImmediately()
    {
        using OutboxSignal signal = new();

        signal.Notify();

        bool woken = await signal.WaitAsync(TimeSpan.Zero, TimeProvider.System, CancellationToken.None);
        woken.ShouldBeTrue();
    }

    [Fact]
    public async Task WaitAsync_NotifiedWhileWaiting_ReturnsTrue()
    {
        using OutboxSignal signal = new();

        Task<bool> waiting = signal.WaitAsync(TimeSpan.FromSeconds(5), TimeProvider.System, CancellationToken.None);
        signal.Notify();

        bool woken = await waiting;
        woken.ShouldBeTrue();
    }

    [Fact]
    public async Task WaitAsync_NoNotification_ReturnsFalseAfterTimeout()
    {
        using OutboxSignal signal = new();

        bool woken = await signal.WaitAsync(TimeSpan.Zero, TimeProvider.System, CancellationToken.None);

        woken.ShouldBeFalse();
    }

    /// <summary>
    /// The timeout runs on the given clock, not on the real one, so a test can move six hours in a
    /// moment (#1185).
    /// </summary>
    [Fact]
    public async Task WaitAsync_NoNotification_ReturnsFalseExactlyWhenTheTimeoutHasPassedOnTheGivenClock()
    {
        using OutboxSignal signal = new();
        TimerWatchingTimeProvider clock = new();

        Task<bool> waiting = signal.WaitAsync(SafetySweepInterval, clock, CancellationToken.None);
        clock.Advance(SafetySweepInterval - OneMillisecond);
        int wakeUpsBeforeTheTimeout = clock.WakeUps;
        clock.Advance(OneMillisecond);

        bool woken = await waiting.WaitAsync(RealTimeLimit);
        wakeUpsBeforeTheTimeout.ShouldBe(0);
        woken.ShouldBeFalse();
    }

    /// <summary>
    /// A wait that timed out must not stay in line for the semaphore. If it did, it would take the next
    /// wake-up, and the relay would sleep through a commit until its next sweep.
    /// </summary>
    [Fact]
    public async Task WaitAsync_AfterAWaitTimedOut_TheNextNotificationWakesTheNextWait()
    {
        using OutboxSignal signal = new();
        TimerWatchingTimeProvider clock = new();
        Task<bool> timedOut = signal.WaitAsync(SafetySweepInterval, clock, CancellationToken.None);
        clock.Advance(SafetySweepInterval);
        await timedOut.WaitAsync(RealTimeLimit);

        signal.Notify();

        bool woken = await signal.WaitAsync(TimeSpan.Zero, clock, CancellationToken.None);
        woken.ShouldBeTrue();
    }

    [Fact]
    public async Task Notify_CalledRepeatedly_CoalescesIntoSingleWakeUp()
    {
        using OutboxSignal signal = new();

        signal.Notify();
        signal.Notify();
        signal.Notify();

        bool firstWait = await signal.WaitAsync(TimeSpan.Zero, TimeProvider.System, CancellationToken.None);
        bool secondWait = await signal.WaitAsync(TimeSpan.Zero, TimeProvider.System, CancellationToken.None);

        firstWait.ShouldBeTrue();
        secondWait.ShouldBeFalse();
    }

    [Fact]
    public async Task WaitAsync_CancelledWhileWaiting_ThrowsOperationCanceled()
    {
        using OutboxSignal signal = new();
        using CancellationTokenSource cts = new();

        Task<bool> waiting = signal.WaitAsync(TimeSpan.FromSeconds(5), TimeProvider.System, cts.Token);
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(async () => await waiting);
    }
}
