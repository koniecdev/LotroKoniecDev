namespace LotroKoniecDev.AuthSystem.API.Outbox;

/// <summary>
/// In-process wake-up line between outbox writers and the relay: a writer calls
/// <see cref="Notify"/> right after its transaction commits, and the relay's pending
/// <see cref="WaitAsync"/> returns instead of sleeping out its safety-sweep timeout.
/// This nudge — not a fixed poll interval — is what drives the relay, so the database is
/// only queried moments after a write, while its compute is already awake (ADR-0035).
/// </summary>
/// <remarks>
/// The semaphore is capped at one pending wake-up on purpose: the relay drains the whole
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
            // A wake-up is already pending; it covers this commit too.
        }
    }

    /// <returns>
    /// <c>true</c> when woken by <see cref="Notify"/>; <c>false</c> when <paramref name="timeout"/>
    /// elapsed first — the relay treats both the same and sweeps, the timeout merely bounds how
    /// long an orphaned row (committed, but crashed before its nudge) can wait.
    /// </returns>
    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        return _pendingWakeUp.WaitAsync(timeout, cancellationToken);
    }

    public void Dispose()
    {
        _pendingWakeUp.Dispose();
    }
}
