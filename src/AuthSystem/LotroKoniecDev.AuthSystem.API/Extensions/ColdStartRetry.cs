using Npgsql;

namespace LotroKoniecDev.AuthSystem.API.Extensions;

/// <summary>
/// A limited retry for database work that runs before the app starts listening (#451). A sleeping Neon
/// compute can take longer to wake up than Npgsql waits to open a connection, so the first open fails
/// for a moment. On Azure that wake took about 31 seconds; on Hetzner it takes under 5 seconds (#923).
/// EF's <c>EnableRetryOnFailure</c> does not retry the open inside <c>MigrateAsync</c> (checked again on
/// #923), so without this wrapper the exception kills the process before <c>RunAsync</c> and the container
/// restarts. In the worst case this takes about 95 seconds (4 attempts of up to 20 seconds, the open limit
/// in <c>AuthDatabaseConnectionString</c>, plus 3 waits of 5). The deploy waits for <c>/health/ready</c>
/// for about 300 seconds.
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
