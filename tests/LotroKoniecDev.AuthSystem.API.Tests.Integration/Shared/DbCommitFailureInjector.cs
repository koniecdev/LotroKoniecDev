using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared;

/// <summary>
/// Makes the next matching commit throw after PostgreSQL has already saved it. That is what a
/// connection lost during a commit looks like to the app: it cannot tell whether the data landed, so it
/// tries again (#839). <see cref="DbCommandFailureInjector"/> cannot reach this moment, because a commit
/// is not a command. It does nothing until a test arms it, and each armed failure fires once. Arming
/// one more leaves the others armed, like <see cref="DbCommandFailureInjector"/>, and the failure armed
/// first takes a commit that two of them match.
/// </summary>
public sealed class DbCommitFailureInjector : DbTransactionInterceptor
{
    private readonly Lock _lock = new();
    private readonly List<ArmedFailure> _armed = [];

    /// <summary>
    /// Counts every failure that fired since the last <see cref="Disarm"/>, whichever arm it came from.
    /// </summary>
    public int FailuresInjected { get; private set; }

    public void FailNextCommitAfterItLands(
        Func<TransactionEndEventData, bool> matches,
        Func<Exception> createFailure)
    {
        lock (_lock)
        {
            _armed.Add(new ArmedFailure(matches, createFailure));
        }
    }

    public void Disarm()
    {
        lock (_lock)
        {
            _armed.Clear();
            FailuresInjected = 0;
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
            int index = _armed.FindIndex(armed => armed.Matches(eventData));
            if (index >= 0)
            {
                failure = _armed[index].CreateFailure();
                _armed.RemoveAt(index);
                FailuresInjected++;
            }
        }

        return failure is null
            ? base.TransactionCommittedAsync(transaction, eventData, cancellationToken)
            : Task.FromException(failure);
    }

    private sealed record ArmedFailure(Func<TransactionEndEventData, bool> Matches, Func<Exception> CreateFailure);
}
