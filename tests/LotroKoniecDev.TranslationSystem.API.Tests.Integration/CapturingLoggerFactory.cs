using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Integration;

/// <summary>
/// Captures what the host logged. It is used where the log is the only visible behaviour: a refused call
/// answers 401 or 403 whether or not the warning about it was written (#854), so a test that could not
/// read the warning would pass with the warning gone. Filter on the category, not on the event id alone:
/// libraries reuse the same numbers (Npgsql's 1300 and 1301 are ours too).
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

    internal sealed record LogEntry(string Category, LogLevel Level, EventId EventId, string Message);

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
            _entries.Enqueue(new LogEntry(_category, logLevel, eventId, formatter(state, exception)));
        }
    }
}
