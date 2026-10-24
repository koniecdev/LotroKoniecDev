namespace LotroKoniecDev.AuthSystem.API.Outbox;

/// <summary>
/// <see cref="OutboxWakeUpInterceptor"/> calls <see cref="Notify"/> after a commit with an outbox row, so
/// the relay queries the database while it is still awake (ADR-0035).
/// </summary>
/// <remarks>
/// At most one pending wake-up: the relay works through the whole backlog on every pass, so ten commits
/// still need only one pass.
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
            // A wake-up is already waiting, and it covers this commit too.
        }
    }

    /// <param name="timeProvider">
    /// Measures <paramref name="timeout"/>, so a test can move the time instead of waiting hours (#1185).
    /// </param>
    /// <returns>
    /// <c>true</c> when <see cref="Notify"/> woke it, <c>false</c> on timeout. The relay does the same
    /// either way; the timeout only limits how long a row whose signal was lost can wait.
    /// </returns>
    public async Task<bool> WaitAsync(TimeSpan timeout, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        // A zero timeout cancels at once, and a cancelled wait never takes a pending wake-up, so take it first.
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
