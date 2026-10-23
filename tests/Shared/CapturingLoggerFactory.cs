using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace LotroKoniecDev.Tests.Shared;

/// <summary>
/// For tests where the log is the only visible behaviour, such as the auth seeder that only warns about
/// an account at the admin address (#839), or a refused call that answers 401 or 403 either way (#854).
/// A test that captures a whole host's log also filters on the category: libraries reuse event ids
/// (Npgsql's 1300 and 1301 are the TMS API's refused-call ids).
/// </summary>
internal sealed class CapturingLoggerFactory : ILoggerFactory
{
    private readonly ConcurrentQueue<LogEntry> _entries = new();

    public IReadOnlyList<LogEntry> Entries => [.. _entries];

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _entries);

    public void AddProvider(ILoggerProvider provider)
    {
    }

    public void Dispose()
    {
    }

    internal sealed record LogEntry(string Category, LogLevel Level, EventId EventId, string Message, Exception? Exception);

    private sealed class CapturingLogger : ILogger
    {
        private readonly string _category;
        private readonly ConcurrentQueue<LogEntry> _entries;

        public CapturingLogger(string category, ConcurrentQueue<LogEntry> entries)
        {
            _category = category;
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
            _entries.Enqueue(new LogEntry(_category, logLevel, eventId, formatter(state, exception), exception));
        }
    }
}
