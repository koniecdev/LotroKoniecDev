using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Integration;

/// <summary>
/// The SQL stream is the only place "this column was not read" shows (PERF-01, PERF-04), a side effect
/// the <c>.Received()</c> rule allows asserting on.
/// </summary>
public sealed class SqlCommandRecorder : DbCommandInterceptor
{
    private readonly ConcurrentQueue<string> _commands = new();

    public IReadOnlyList<string> Commands => [.. _commands];

    /// <summary>
    /// Lets a test act as a given query starts (#715). Clear it at the end: the factory is shared by the
    /// whole collection.
    /// </summary>
    public Action<string>? BeforeCommand { get; set; }

    public void Clear() => _commands.Clear();

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result)
    {
        Record(command.CommandText);
        return base.ReaderExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Record(command.CommandText);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result)
    {
        Record(command.CommandText);
        return base.NonQueryExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Record(command.CommandText);
        return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }

    private void Record(string commandText)
    {
        _commands.Enqueue(commandText);
        BeforeCommand?.Invoke(commandText);
    }
}
