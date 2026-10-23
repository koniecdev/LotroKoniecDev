using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using LotroKoniecDev.AuthSystem.Persistence.Outbox;

namespace LotroKoniecDev.AuthSystem.API.Outbox;

/// <summary>
/// Wakes the relay after every commit that carries a new outbox row, so no writer has to remember a
/// second call after its save (ADR-0038 §6, amended by #1115). A save with no transaction open commits
/// on its own, so the wake-up follows the save. A save inside a transaction commits only with that
/// transaction, so the wake-up waits for its commit: the relay reads committed rows, and a signal sent
/// earlier could find nothing (the <c>RegisterUser</c> trap of ADR-0035).
/// </summary>
/// <remarks>
/// A failed or cancelled save with no transaction open, and a failed commit, wake the relay too, because
/// the write may have landed with only its answer lost (ADR-0065 §1). A needless wake-up finds nothing
/// to send. A rolled-back transaction wakes nothing.
/// <para>
/// EF Core reports a lost concurrency check on its own path, <see cref="ThrowingConcurrencyException"/>,
/// and not as a failed save. Identity turns that failure into a failed result, so it is the failed save
/// of every writer that saves through <c>UserManager.UpdateAsync</c>.
/// </para>
/// <para>
/// It is scoped like the context it sits on, so its state belongs to one context. It sees only commits
/// made through EF Core. Under a <c>TransactionScope</c> it would wake the relay too early, before the
/// scope commits, and a transaction handed in with <c>UseTransaction</c> and committed outside EF Core
/// would never wake it. The app uses neither. A row written with raw SQL would wait for the relay's
/// sweep, and the app writes none.
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
    /// Only one transaction is open on a context at a time. A transaction rolled back, or disposed without
    /// a commit, leaves its id here: EF Core reports no event for the second case. That transaction never
    /// commits, so its id never wakes the relay.
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
        // Each save decides on its own. A save that another interceptor refused before EF Core began it
        // reported no end and wrote nothing, so its wake-up must not fire at this save.
        _wakeUpAfterSave = false;

        if (context is null
            || !context.ChangeTracker.Entries<OutboxMessage>().Any(entry => entry.State == EntityState.Added))
        {
            return;
        }

        // A failed save inside a transaction waits for that transaction too. Only its end says whether
        // anything landed.
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
