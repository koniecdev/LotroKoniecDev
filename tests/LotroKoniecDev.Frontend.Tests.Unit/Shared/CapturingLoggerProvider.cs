using Microsoft.Extensions.Logging;

namespace LotroKoniecDev.Frontend.Tests.Unit.Shared;

/// <summary>
/// Captures what the code under test logged. It is used where the log is the only visible behaviour:
/// the warning about an unmapped error code (ADR-0044) is deliberately not in the markup, so a null
/// logger would let that gap pass unnoticed, which is what the ADR forbids.
/// </summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly List<LogEntry> _entries = [];

    public IReadOnlyList<LogEntry> Entries => _entries;

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(_entries);

    public void Dispose()
    {
    }

    internal sealed record LogEntry(LogLevel Level, string Message);

    private sealed class CapturingLogger : ILogger
    {
        private readonly List<LogEntry> _entries;

        public CapturingLogger(List<LogEntry> entries)
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
            _entries.Add(new LogEntry(logLevel, formatter(state, exception)));
        }
    }
}
