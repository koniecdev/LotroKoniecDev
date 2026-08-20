using Npgsql;

namespace LotroKoniecDev.AuthSystem.API.Extensions;

/// <summary>
/// A limited retry for database work that runs before the app starts listening (#451). On the daily
/// cold start the Neon compute takes longer to wake up, about 31 seconds as measured, than Npgsql
/// waits to open a connection, so the first open fails for a moment.
/// EF's <c>EnableRetryOnFailure</c> retries commands but not that first open, so without this wrapper
/// the exception kills the process before <c>RunAsync</c> and the container restarts.
/// In the worst case this takes about 95 seconds (4 attempts of about 20 seconds plus 3 waits of 5),
/// well under the 300 second startup probe budget.
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
