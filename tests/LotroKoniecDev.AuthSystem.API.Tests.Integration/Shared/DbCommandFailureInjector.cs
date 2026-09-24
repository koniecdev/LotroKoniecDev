using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared;

/// <summary>
/// Makes the next database command that matches fail before it reaches PostgreSQL. A test uses it to
/// stop a unit of work between two of its writes, which is the moment a real outage or a killed
/// process can hit and nothing else in this suite can reach (#839). It does nothing until a test arms
/// it, and it disarms itself after the failure it was asked for.
/// </summary>
public sealed class DbCommandFailureInjector : DbCommandInterceptor
{
    private readonly Lock _lock = new();
    private Func<DbCommand, bool>? _matches;
    private Func<Exception>? _createFailure;

    public int FailuresInjected { get; private set; }

    public void FailNext(Func<DbCommand, bool> matches, Func<Exception> createFailure)
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
            FailuresInjected = 0;
        }
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result)
    {
        ThrowIfArmedFor(command);
        return base.ReaderExecuting(command, eventData, result);
    }

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result)
    {
        ThrowIfArmedFor(command);
        return base.NonQueryExecuting(command, eventData, result);
    }

    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<object> result)
    {
        ThrowIfArmedFor(command);
        return base.ScalarExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        ThrowIfArmedFor(command);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        ThrowIfArmedFor(command);
        return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        ThrowIfArmedFor(command);
        return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
    }

    private void ThrowIfArmedFor(DbCommand command)
    {
        Exception? failure = null;

        lock (_lock)
        {
            if (_matches is not null && _createFailure is not null && _matches(command))
            {
                failure = _createFailure();
                FailuresInjected++;
                _matches = null;
                _createFailure = null;
            }
        }

        if (failure is not null)
        {
            throw failure;
        }
    }
}
