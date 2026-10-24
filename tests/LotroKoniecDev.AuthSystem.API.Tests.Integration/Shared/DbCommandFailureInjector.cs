using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared;

/// <summary>
/// Makes the next matching command fail before it reaches PostgreSQL, to stop a unit of work between two
/// writes as an outage or a killed process can (#839). Each armed failure fires once, unless armed with
/// <see cref="FailEvery"/>, and a test may arm several (#980). The one armed first takes a command two arms
/// match, so a counting predicate sees fewer commands than ran.
/// </summary>
public sealed class DbCommandFailureInjector : DbCommandInterceptor
{
    private readonly Lock _lock = new();
    private readonly List<ArmedFailure> _armed = [];

    /// <summary>
    /// Counts every failure that fired since the last <see cref="Disarm"/>, whichever arm it came from.
    /// </summary>
    public int FailuresInjected { get; private set; }

    public void FailNext(Func<DbCommand, bool> matches, Func<Exception> createFailure) =>
        Arm(new ArmedFailure(matches, createFailure, FailAfterItRuns: false, KeepArmed: false));

    /// <summary>
    /// Makes every matching command fail until the test ends, like a row the database keeps refusing.
    /// A failed write that a later save sends again then fails there too (#981).
    /// </summary>
    public void FailEvery(Func<DbCommand, bool> matches, Func<Exception> createFailure) =>
        Arm(new ArmedFailure(matches, createFailure, FailAfterItRuns: false, KeepArmed: true));

    /// <summary>
    /// Fails the next matching command after PostgreSQL ran it. EF sends a one-row save without a
    /// transaction, so it commits on its own, and this is a save whose answer is lost (#962). A command inside
    /// a transaction never matches, because failing it would roll it back.
    /// </summary>
    public void FailNextAfterItRuns(Func<DbCommand, bool> matches, Func<Exception> createFailure) =>
        Arm(new ArmedFailure(matches, createFailure, FailAfterItRuns: true, KeepArmed: false));

    public void Disarm()
    {
        lock (_lock)
        {
            _armed.Clear();
            FailuresInjected = 0;
        }
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result)
    {
        ThrowIfArmedFor(command, afterItRan: false);
        return base.ReaderExecuting(command, eventData, result);
    }

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result)
    {
        ThrowIfArmedFor(command, afterItRan: false);
        return base.NonQueryExecuting(command, eventData, result);
    }

    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<object> result)
    {
        ThrowIfArmedFor(command, afterItRan: false);
        return base.ScalarExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        ThrowIfArmedFor(command, afterItRan: false);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        ThrowIfArmedFor(command, afterItRan: false);
        return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        ThrowIfArmedFor(command, afterItRan: false);
        return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override DbDataReader ReaderExecuted(
        DbCommand command,
        CommandExecutedEventData eventData,
        DbDataReader result)
    {
        if (TakeFailureFor(command, afterItRan: true) is { } failure)
        {
            result.Dispose();
            throw failure;
        }

        return base.ReaderExecuted(command, eventData, result);
    }

    public override int NonQueryExecuted(
        DbCommand command,
        CommandExecutedEventData eventData,
        int result)
    {
        ThrowIfArmedFor(command, afterItRan: true);
        return base.NonQueryExecuted(command, eventData, result);
    }

    public override async ValueTask<DbDataReader> ReaderExecutedAsync(
        DbCommand command,
        CommandExecutedEventData eventData,
        DbDataReader result,
        CancellationToken cancellationToken = default)
    {
        if (TakeFailureFor(command, afterItRan: true) is { } failure)
        {
            // Disposing the reader drains it, so PostgreSQL has finished the command.
            await result.DisposeAsync();
            throw failure;
        }

        return await base.ReaderExecutedAsync(command, eventData, result, cancellationToken);
    }

    public override ValueTask<int> NonQueryExecutedAsync(
        DbCommand command,
        CommandExecutedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        ThrowIfArmedFor(command, afterItRan: true);
        return base.NonQueryExecutedAsync(command, eventData, result, cancellationToken);
    }

    public override object? ScalarExecuted(
        DbCommand command,
        CommandExecutedEventData eventData,
        object? result)
    {
        ThrowIfArmedFor(command, afterItRan: true);
        return base.ScalarExecuted(command, eventData, result);
    }

    public override ValueTask<object?> ScalarExecutedAsync(
        DbCommand command,
        CommandExecutedEventData eventData,
        object? result,
        CancellationToken cancellationToken = default)
    {
        ThrowIfArmedFor(command, afterItRan: true);
        return base.ScalarExecutedAsync(command, eventData, result, cancellationToken);
    }

    private void Arm(ArmedFailure failure)
    {
        lock (_lock)
        {
            _armed.Add(failure);
        }
    }

    private void ThrowIfArmedFor(DbCommand command, bool afterItRan)
    {
        if (TakeFailureFor(command, afterItRan) is { } failure)
        {
            throw failure;
        }
    }

    private Exception? TakeFailureFor(DbCommand command, bool afterItRan)
    {
        if (afterItRan && command.Transaction is not null)
        {
            return null;
        }

        lock (_lock)
        {
            // A predicate may count the commands it sees, so it is asked only about commands of its phase.
            int index = _armed.FindIndex(armed => armed.FailAfterItRuns == afterItRan && armed.Matches(command));
            if (index < 0)
            {
                return null;
            }

            // A factory that throws leaves its arm in place and uncounted, so the test fails on its own
            // setup error and not on a count that is off by one.
            ArmedFailure taken = _armed[index];
            Exception failure = taken.CreateFailure();
            if (!taken.KeepArmed)
            {
                _armed.RemoveAt(index);
            }

            FailuresInjected++;
            return failure;
        }
    }

    private sealed record ArmedFailure(
        Func<DbCommand, bool> Matches,
        Func<Exception> CreateFailure,
        bool FailAfterItRuns,
        bool KeepArmed);
}
