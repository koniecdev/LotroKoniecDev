using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using LotroKoniecDev.AuthSystem.Persistence.Outbox;

namespace LotroKoniecDev.AuthSystem.API.Outbox;

/// <summary>
/// Wakes the relay after every commit that carries a new outbox row, so no writer has to remember a call
/// after its save (ADR-0038 §6, #1115). Inside a transaction the wake-up waits for the commit, because the
/// relay reads committed rows (the <c>RegisterUser</c> trap of ADR-0035).
/// </summary>
/// <remarks>
/// A failed or cancelled save outside a transaction, and a failed commit, wake the relay too: the write
/// may have landed with only its answer lost (ADR-0065 §1). A rolled-back transaction wakes nothing.
/// <para>
/// EF Core reports a lost concurrency check through <see cref="ThrowingConcurrencyException"/>, not as a
/// failed save, and every <c>UserManager.UpdateAsync</c> writer meets it that way.
/// </para>
/// <para>
/// It sees only commits made through EF Core: a <c>TransactionScope</c> would wake the relay too early,
/// a <c>UseTransaction</c> committed outside EF Core never, and a raw SQL row waits for the sweep. The app
/// uses none of these.
/// </para>
/// </remarks>
internal sealed class OutboxWakeUpInterceptor : ISaveChangesInterceptor, IDbTransactionInterceptor
{
    /// <summary>
    /// The action EF Core names in <see cref="TransactionErrorEventData.Action"/> when a commit fails.
    /// </summary>
    private const string CommitAction = "Commit";

    private readonly OutboxSignal _outboxSignal;

    private bool _wakeUpAfterSave;

    /// <summary>
    /// A transaction rolled back or disposed without a commit leaves its id here (EF Core reports no event
    /// for the second case), but it never commits, so it never wakes the relay.
    /// </summary>
    private Guid? _transactionToWakeUpAfter;

    public InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        NoteNewOutboxRows(eventData.Context);
        return result;
    }

    public ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        NoteNewOutboxRows(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        WakeUpAfterSave();
        return result;
    }

    public ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        WakeUpAfterSave();
        return ValueTask.FromResult(result);
    }

    public void SaveChangesFailed(DbContextErrorEventData eventData)
    {
        WakeUpAfterSave();
    }

    public Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        WakeUpAfterSave();
        return Task.CompletedTask;
    }

    public InterceptionResult ThrowingConcurrencyException(
        ConcurrencyExceptionEventData eventData,
        InterceptionResult result)
    {
        WakeUpAfterSave();
        return result;
    }

    public ValueTask<InterceptionResult> ThrowingConcurrencyExceptionAsync(
        ConcurrencyExceptionEventData eventData,
        InterceptionResult result,
        CancellationToken cancellationToken = default)
    {
        WakeUpAfterSave();
        return ValueTask.FromResult(result);
    }

    public void SaveChangesCanceled(DbContextEventData eventData)
    {
        WakeUpAfterSave();
    }

    public Task SaveChangesCanceledAsync(DbContextEventData eventData, CancellationToken cancellationToken = default)
    {
        WakeUpAfterSave();
        return Task.CompletedTask;
    }

    public void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData)
    {
        WakeUpAfterCommit(eventData.TransactionId);
    }

    public Task TransactionCommittedAsync(
        DbTransaction transaction,
        TransactionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        WakeUpAfterCommit(eventData.TransactionId);
        return Task.CompletedTask;
    }

    public void TransactionFailed(DbTransaction transaction, TransactionErrorEventData eventData)
    {
        WakeUpAfterFailedCommit(eventData);
    }

    public Task TransactionFailedAsync(
        DbTransaction transaction,
        TransactionErrorEventData eventData,
        CancellationToken cancellationToken = default)
    {
        WakeUpAfterFailedCommit(eventData);
        return Task.CompletedTask;
    }

    public OutboxWakeUpInterceptor(OutboxSignal outboxSignal)
    {
        _outboxSignal = outboxSignal;
    }

    private void NoteNewOutboxRows(DbContext? context)
    {
        // Each save decides on its own: a save another interceptor refused reported no end, so its wake-up
        // must not fire at this one.
        _wakeUpAfterSave = false;

        if (context is null
            || !context.ChangeTracker.Entries<OutboxMessage>().Any(entry => entry.State == EntityState.Added))
        {
            return;
        }

        // Even a failed save inside a transaction waits for its end, which says whether anything landed.
        if (context.Database.CurrentTransaction is { } transaction)
        {
            _transactionToWakeUpAfter = transaction.TransactionId;
        }
        else
        {
            _wakeUpAfterSave = true;
        }
    }

    private void WakeUpAfterSave()
    {
        if (!_wakeUpAfterSave)
        {
            return;
        }

        _wakeUpAfterSave = false;
        _outboxSignal.Notify();
    }

    private void WakeUpAfterCommit(Guid transactionId)
    {
        if (_transactionToWakeUpAfter != transactionId)
        {
            return;
        }

        _transactionToWakeUpAfter = null;
        _outboxSignal.Notify();
    }

    /// <summary>
    /// A failed savepoint leaves the transaction open, and a failed rollback commits nothing, so only a
    /// failed commit counts.
    /// </summary>
    private void WakeUpAfterFailedCommit(TransactionErrorEventData eventData)
    {
        if (eventData.Action is CommitAction)
        {
            WakeUpAfterCommit(eventData.TransactionId);
        }
    }
}
