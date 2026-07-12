using Npgsql;

namespace LotroKoniecDev.AuthSystem.API.Extensions;

/// <summary>
/// Bounded retry for pre-listen database work (#451): on the daily scale-from-zero cold start the
/// Neon compute resumes slower (~31 s measured) than Npgsql's connection-open timeout, so the
/// first physical open fails transiently. EF's <c>EnableRetryOnFailure</c> retries commands, not
/// that initial open — without this wrapper the unhandled exception kills the process before
/// <c>RunAsync</c> and the container restarts. Worst case (4 attempts × ~20 s open timeout +
/// 3 × 5 s delay ≈ 95 s) stays well under the 300 s ACA startup-probe budget.
/// </summary>
internal static partial class ColdStartRetry
{
    internal const int DefaultMaxAttempts = 4;
    internal static readonly TimeSpan DefaultDelayBetweenAttempts = TimeSpan.FromSeconds(5);

    internal static async Task ExecuteAsync(
        Func<Task> operation,
        ILogger logger,
        int maxAttempts = DefaultMaxAttempts,
        TimeSpan? delayBetweenAttempts = null)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);

        TimeSpan delay = delayBetweenAttempts ?? DefaultDelayBetweenAttempts;

        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await operation();
                return;
            }
            catch (Exception exception) when (attempt < maxAttempts && IsTransientDatabaseFailure(exception))
            {
                LogTransientStartupFailure(logger, exception, attempt, maxAttempts, delay.TotalSeconds);

                await Task.Delay(delay);
            }
        }
    }

    internal static bool IsTransientDatabaseFailure(Exception? exception)
    {
        while (exception is not null)
        {
            if (exception is NpgsqlException { IsTransient: true })
            {
                return true;
            }

            exception = exception.InnerException;
        }

        return false;
    }

    [LoggerMessage(EventId = EventIds.StartupTransientDatabaseFailure, Level = LogLevel.Warning, Message = "Transient database failure on startup attempt {Attempt}/{MaxAttempts}; retrying in {DelaySeconds}s")]
    private static partial void LogTransientStartupFailure(ILogger logger, Exception exception, int attempt, int maxAttempts, double delaySeconds);
}
