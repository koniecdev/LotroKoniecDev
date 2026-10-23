using Npgsql;

namespace LotroKoniecDev.AuthSystem.API.Extensions;

/// <summary>
/// A sleeping Neon compute can take longer to wake than Npgsql waits to open a connection (#451), and
/// EF's <c>EnableRetryOnFailure</c> does not retry the open inside <c>MigrateAsync</c> (#923). Without
/// this the process dies before <c>RunAsync</c>. A failing open costs about 95 seconds (4 attempts of up
/// to 20 seconds plus 3 waits of 5), well inside the deploy's 300-second wait for <c>/health/ready</c>.
/// A stall inside the seed's own queries can take longer, because EF retries those (#1121).
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
