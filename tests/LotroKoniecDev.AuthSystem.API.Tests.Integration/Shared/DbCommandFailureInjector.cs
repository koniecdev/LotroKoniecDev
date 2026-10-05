using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared;

/// <summary>
/// Makes the next database command that matches fail before it reaches PostgreSQL. A test uses it to
/// stop a unit of work between two of its writes, which is the moment a real outage or a killed
/// process can hit and nothing else in this suite can reach (#839). It does nothing until a test arms
/// it, and each armed failure fires once. A test may arm more than one, for a run that has to meet two
/// failures (#980). Arming one more leaves the others armed. When one command matches two of them, the
/// failure armed first takes it, and the ones armed after it never see that command.
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
        Arm(new ArmedFailure(matches, createFailure, FailAfterItRuns: false));

    /// <summary>
    /// Makes the next matching command fail after PostgreSQL has run it. EF sends a save of one row
    /// without a transaction, so PostgreSQL commits it on its own, and this is a save whose answer is
    /// lost on the way back (#962). <see cref="DbCommitFailureInjector"/> cannot reach that save,
    /// because it has no commit of its own. A command inside a transaction never matches: failing it
    /// would roll it back, which is a different case, and it is not counted in <see cref="FailuresInjected"/>.
    /// </summary>
    public void FailNextAfterItRuns(Func<DbCommand, bool> matches, Func<Exception> createFailure) =>
        Arm(new ArmedFailure(matches, createFailure, FailAfterItRuns: true));

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
            // A command an earlier arm takes is not shown to the arms after it, so a counting predicate
            // armed behind an overlapping one counts fewer commands than ran.
            int index = _armed.FindIndex(armed => armed.FailAfterItRuns == afterItRan && armed.Matches(command));
            if (index < 0)
            {
                return null;
            }

            ArmedFailure armed = _armed[index];
            _armed.RemoveAt(index);
            FailuresInjected++;
            return armed.CreateFailure();
        }
    }

    private sealed record ArmedFailure(Func<DbCommand, bool> Matches, Func<Exception> CreateFailure, bool FailAfterItRuns);
}
