namespace LotroKoniecDev.AuthSystem.API.Outbox;

/// <summary>
/// The wake-up line inside the process between outbox writers and the relay. A writer calls
/// <see cref="Notify"/> right after its transaction commits, and the relay's waiting
/// <see cref="WaitAsync"/> returns instead of sleeping until its safety sweep.
/// This signal, and not a fixed interval, is what drives the relay, so the database is queried only
/// moments after a write, while it is still awake (ADR-0035).
/// </summary>
/// <remarks>
/// The semaphore holds at most one pending wake-up on purpose. The relay works through the whole
/// backlog on every pass, so ten commits before it wakes still need only one pass.
/// </remarks>
internal sealed class OutboxSignal : IDisposable
{
    private readonly SemaphoreSlim _pendingWakeUp = new(initialCount: 0, maxCount: 1);

    public void Notify()
    {
        try
        {
            _pendingWakeUp.Release();
        }
        catch (SemaphoreFullException)
        {
            // A wake-up is already waiting, and it covers this commit as well.
        }
    }

    /// <param name="timeProvider">
    /// The clock that measures <paramref name="timeout"/>, so a test can move the time instead of waiting
    /// hours for the safety sweep (#1185).
    /// </param>
    /// <returns>
    /// <c>true</c> when <see cref="Notify"/> woke it, <c>false</c> when <paramref name="timeout"/>
    /// ran out first. The relay does the same thing either way. The timeout only limits how long a
    /// forgotten row can wait, one that was committed before a crash took its signal with it.
    /// </returns>
    public async Task<bool> WaitAsync(TimeSpan timeout, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        // A timeout of zero cancels its token at once, and a cancelled wait never takes a pending
        // wake-up. So the wake-up that is already there is taken first.
        if (_pendingWakeUp.Wait(TimeSpan.Zero, cancellationToken))
        {
            return true;
        }

        using CancellationTokenSource timeoutSource = new(timeout, timeProvider);
        using CancellationTokenSource waitSource =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);

        try
        {
            await _pendingWakeUp.WaitAsync(waitSource.Token);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    public void Dispose()
    {
        _pendingWakeUp.Dispose();
    }
}
