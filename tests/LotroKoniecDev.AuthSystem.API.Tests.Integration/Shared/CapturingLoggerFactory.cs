using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared;

/// <summary>
/// Captures what the code under test logged. It is used where the log is the only visible behaviour:
/// the seeder leaves an account at the admin address as it is and only warns (#839), so a test that
/// could not read the warning would pass with the warning gone.
/// </summary>
internal sealed class CapturingLoggerFactory : ILoggerFactory
{
    private readonly ConcurrentQueue<LogEntry> _entries = new();

    public IReadOnlyList<LogEntry> Entries => [.. _entries];

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(_entries);

    public void AddProvider(ILoggerProvider provider)
    {
    }

    public void Dispose()
    {
    }

    internal sealed record LogEntry(LogLevel Level, EventId EventId, string Message);

    private sealed class CapturingLogger : ILogger
    {
        private readonly ConcurrentQueue<LogEntry> _entries;

        public CapturingLogger(ConcurrentQueue<LogEntry> entries)
        {
            _entries = entries;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            _entries.Enqueue(new LogEntry(logLevel, eventId, formatter(state, exception)));
        }
    }
}
