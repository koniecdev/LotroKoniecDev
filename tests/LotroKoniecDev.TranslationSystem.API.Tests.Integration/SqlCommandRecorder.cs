using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Integration;

/// <summary>
/// Records the SQL of every command the intercepted context runs, both reads such as queries and
/// SaveChanges batches and writes such as <c>ExecuteUpdate</c>. A test can then check things the HTTP
/// response does not show: that the 304 path for the translation file never reads the multi-MB
/// <c>Content</c> column (PERF-01, #286), or that a projection refresh updates in place without reading
/// it again (PERF-04, #289).
/// The stream of database commands is the only place "this column was not read" is visible, and the
/// repo's <c>.Received()</c> rule, which allows asserting on side effects the return value does not
/// show, permits it.
/// </summary>
public sealed class SqlCommandRecorder : DbCommandInterceptor
{
    private readonly ConcurrentQueue<string> _commands = new();

    public IReadOnlyList<string> Commands => [.. _commands];

    public void Clear() => _commands.Clear();

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result)
    {
        _commands.Enqueue(command.CommandText);
        return base.ReaderExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        _commands.Enqueue(command.CommandText);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result)
    {
        _commands.Enqueue(command.CommandText);
        return base.NonQueryExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        _commands.Enqueue(command.CommandText);
        return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }
}
