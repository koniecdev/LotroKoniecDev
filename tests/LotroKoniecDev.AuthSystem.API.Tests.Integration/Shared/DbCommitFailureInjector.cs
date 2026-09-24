using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared;

/// <summary>
/// Makes the next matching commit throw after PostgreSQL has already saved it. That is what a
/// connection lost during a commit looks like to the app: it cannot tell whether the data landed, so it
/// tries again (#839). <see cref="DbCommandFailureInjector"/> cannot reach this moment, because a commit
/// is not a command. It does nothing until a test arms it, and it disarms itself after one failure.
/// </summary>
public sealed class DbCommitFailureInjector : DbTransactionInterceptor
{
    private readonly Lock _lock = new();
    private Func<TransactionEndEventData, bool>? _matches;
    private Func<Exception>? _createFailure;

    public int FailuresInjected { get; private set; }

    public void FailNextCommitAfterItLands(
        Func<TransactionEndEventData, bool> matches,
        Func<Exception> createFailure)
    {
        lock (_lock)
        {
            _matches = matches;
            _createFailure = createFailure;
            FailuresInjected = 0;
        }
    }

    public void Disarm()
    {
        lock (_lock)
        {
            _matches = null;
            _createFailure = null;
        }
    }

    public override Task TransactionCommittedAsync(
        DbTransaction transaction,
        TransactionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        Exception? failure = null;

        lock (_lock)
        {
            if (_matches is not null && _createFailure is not null && _matches(eventData))
            {
                failure = _createFailure();
                FailuresInjected++;
                _matches = null;
                _createFailure = null;
            }
        }

        return failure is null
            ? base.TransactionCommittedAsync(transaction, eventData, cancellationToken)
            : Task.FromException(failure);
    }
}
