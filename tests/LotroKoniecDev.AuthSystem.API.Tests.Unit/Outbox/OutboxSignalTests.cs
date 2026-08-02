using LotroKoniecDev.AuthSystem.API.Outbox;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Outbox;

public sealed class OutboxSignalTests
{
    [Fact]
    public async Task WaitAsync_NotifiedBeforehand_ReturnsTrueImmediately()
    {
        using OutboxSignal signal = new();

        signal.Notify();

        bool woken = await signal.WaitAsync(TimeSpan.Zero, CancellationToken.None);
        woken.ShouldBeTrue();
    }

    [Fact]
    public async Task WaitAsync_NotifiedWhileWaiting_ReturnsTrue()
    {
        using OutboxSignal signal = new();

        Task<bool> waiting = signal.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        signal.Notify();

        bool woken = await waiting;
        woken.ShouldBeTrue();
    }

    [Fact]
    public async Task WaitAsync_NoNotification_ReturnsFalseAfterTimeout()
    {
        using OutboxSignal signal = new();

        bool woken = await signal.WaitAsync(TimeSpan.Zero, CancellationToken.None);

        woken.ShouldBeFalse();
    }

    [Fact]
    public async Task Notify_CalledRepeatedly_CoalescesIntoSingleWakeUp()
    {
        using OutboxSignal signal = new();

        signal.Notify();
        signal.Notify();
        signal.Notify();

        bool firstWait = await signal.WaitAsync(TimeSpan.Zero, CancellationToken.None);
        bool secondWait = await signal.WaitAsync(TimeSpan.Zero, CancellationToken.None);

        firstWait.ShouldBeTrue();
        secondWait.ShouldBeFalse();
    }

    [Fact]
    public async Task WaitAsync_CancelledWhileWaiting_ThrowsOperationCanceled()
    {
        using OutboxSignal signal = new();
        using CancellationTokenSource cts = new();

        Task<bool> waiting = signal.WaitAsync(TimeSpan.FromSeconds(5), cts.Token);
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(async () => await waiting);
    }
}
