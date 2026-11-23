using Microsoft.Extensions.Logging;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Shared;

/// <summary>
/// For tests where the log line is the behaviour: an audit trail is invisible in the return value.
/// Hand-written, because NSubstitute cannot proxy an <c>ILogger</c> of an internal type.
/// </summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    private const string OriginalFormatKey = "{OriginalFormat}";

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
        _entries.Add(new LogEntry(logLevel, eventId.Id, formatter(state, exception), TemplateOf(state)));
    }

    /// <summary>
    /// The template is what Serilog writes as <c>"@mt"</c>, the text a Grafana log rule anchors on.
    /// </summary>
    private static string? TemplateOf<TState>(TState state) =>
        state is IReadOnlyList<KeyValuePair<string, object?>> properties
            ? properties.FirstOrDefault(property => property.Key == OriginalFormatKey).Value as string
            : null;

    internal sealed record LogEntry(LogLevel Level, int EventId, string Message, string? Template);
}
