using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace LotroKoniecDev.Logging.TimedOperations;

#pragma warning disable CA2254 // Message template is composed at runtime by design (timer utility)
#pragma warning disable CA1848 // LoggerMessage needs a compile-time template; this utility's template
                               // is composed per operation, and a per-instance Define would re-parse
                               // the template on every operation — slower than the plain Log call

public sealed class TimedOperation : IDisposable
{
    private readonly ILogger _logger;
    private readonly long? _slowThresholdMs;
    private readonly string _composedTemplate;
    private readonly long _startingTimestamp;

    internal TimedOperation(
        ILogger logger,
        long? slowThresholdMs,
        string messageTemplate)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(messageTemplate);
        _logger = logger;
        _slowThresholdMs = slowThresholdMs;
        _composedTemplate = $"{messageTemplate} completed in {{OperationDurationMs}}ms";
        _startingTimestamp = Stopwatch.GetTimestamp();
    }

    public void Dispose()
    {
        TimeSpan delta = Stopwatch.GetElapsedTime(_startingTimestamp);

        LogLevel logLevel = _slowThresholdMs.HasValue && delta.TotalMilliseconds > _slowThresholdMs.Value
            ? LogLevel.Warning
            : LogLevel.Information;

        if (_logger.IsEnabled(logLevel))
        {
            _logger.Log(logLevel, _composedTemplate, delta.TotalMilliseconds);
        }
    }
}

public sealed class TimedOperation<T0> : IDisposable
{
    private readonly ILogger _logger;
    private readonly long? _slowThresholdMs;
    private readonly string _composedTemplate;
    private readonly T0 _arg0;
    private readonly long _startingTimestamp;

    internal TimedOperation(
        ILogger logger,
        long? slowThresholdMs,
        string messageTemplate,
        T0 arg0)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(messageTemplate);
        _logger = logger;
        _slowThresholdMs = slowThresholdMs;
        _composedTemplate = $"{messageTemplate} completed in {{OperationDurationMs}}ms";
        _arg0 = arg0;
        _startingTimestamp = Stopwatch.GetTimestamp();
    }

    public void Dispose()
    {
        TimeSpan delta = Stopwatch.GetElapsedTime(_startingTimestamp);

        LogLevel logLevel = _slowThresholdMs.HasValue && delta.TotalMilliseconds > _slowThresholdMs.Value
            ? LogLevel.Warning
            : LogLevel.Information;

        if (_logger.IsEnabled(logLevel))
        {
            _logger.Log(logLevel, _composedTemplate, _arg0, delta.TotalMilliseconds);
        }
    }
}

public sealed class TimedOperation<T0, T1> : IDisposable
{
    private readonly ILogger _logger;
    private readonly long? _slowThresholdMs;
    private readonly string _composedTemplate;
    private readonly T0 _arg0;
    private readonly T1 _arg1;
    private readonly long _startingTimestamp;

    internal TimedOperation(
        ILogger logger,
        long? slowThresholdMs,
        string messageTemplate,
        T0 arg0,
        T1 arg1)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(messageTemplate);
        _logger = logger;
        _slowThresholdMs = slowThresholdMs;
        _composedTemplate = $"{messageTemplate} completed in {{OperationDurationMs}}ms";
        _arg0 = arg0;
        _arg1 = arg1;
        _startingTimestamp = Stopwatch.GetTimestamp();
    }

    public void Dispose()
    {
        TimeSpan delta = Stopwatch.GetElapsedTime(_startingTimestamp);

        LogLevel logLevel = _slowThresholdMs.HasValue && delta.TotalMilliseconds > _slowThresholdMs.Value
            ? LogLevel.Warning
            : LogLevel.Information;

        if (_logger.IsEnabled(logLevel))
        {
            _logger.Log(logLevel, _composedTemplate, _arg0, _arg1, delta.TotalMilliseconds);
        }
    }
}
