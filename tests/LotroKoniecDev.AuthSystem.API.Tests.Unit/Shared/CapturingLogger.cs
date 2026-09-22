using Microsoft.Extensions.Logging;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Shared;

/// <summary>
/// Captures what the code under test logged. It is used where the log line is the behaviour: an audit
/// trail is invisible in the return value, so a null logger would let a deleted line pass unnoticed.
/// Hand-written, because NSubstitute cannot proxy an <c>ILogger</c> of an internal type.
/// </summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    private readonly List<LogEntry> _entries = [];

    public IReadOnlyList<LogEntry> Entries => _entries;

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
        _entries.Add(new LogEntry(logLevel, eventId.Id, formatter(state, exception)));
    }

    internal sealed record LogEntry(LogLevel Level, int EventId, string Message);
}
