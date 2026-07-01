using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Integration;

/// <summary>
/// Records the SQL text of every query the intercepted context executes, so a test can pin
/// properties that are invisible in the HTTP response — e.g. that the translation-file 304 path
/// never reads the multi-MB <c>Content</c> column (PERF-01/#286). The DB command stream is the
/// only observable seam for "this column was not fetched": the repo's <c>.Received()</c> policy
/// (side effects invisible in the return value) sanctions asserting on it.
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
}
