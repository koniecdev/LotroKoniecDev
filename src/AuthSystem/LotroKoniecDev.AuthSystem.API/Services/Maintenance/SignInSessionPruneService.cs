using Microsoft.EntityFrameworkCore;
using LotroKoniecDev.AuthSystem.Persistence.DbContexts;

namespace LotroKoniecDev.AuthSystem.API.Services.Maintenance;

/// <summary>
/// Deletes expired sign-in sessions once a day (ADR-0062). The cookie handler deletes an expired session
/// only when its cookie comes back, so a cookie that never comes back, from a closed browser or a cleared
/// cookie jar, would otherwise leave its row forever.
/// </summary>
internal sealed partial class SignInSessionPruneService : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(1);

    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SignInSessionPruneService> _logger;

    public SignInSessionPruneService(
        IServiceScopeFactory serviceScopeFactory,
        TimeProvider timeProvider,
        ILogger<SignInSessionPruneService> logger)
    {
        _serviceScopeFactory = serviceScopeFactory;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(StartupDelay, _timeProvider, stoppingToken);
        await PruneOnceAsync(stoppingToken);

        using PeriodicTimer timer = new(Interval, _timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await PruneOnceAsync(stoppingToken);
        }
    }

    /// <summary>
    /// Runs one prune pass. A session is expired by the rule the cookie handler uses: its expiry lies in
    /// the past. Every failure is logged and then ignored, because an exception leaving
    /// <see cref="ExecuteAsync"/> stops the whole host. The one exception is a cancellation of
    /// <paramref name="cancellationToken"/>, which means a normal shutdown and has to pass through.
    /// </summary>
    internal async Task PruneOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using AsyncServiceScope scope = _serviceScopeFactory.CreateAsyncScope();
            AuthDbContext dbContext = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

            DateTimeOffset now = _timeProvider.GetUtcNow();

            int prunedSessions = await dbContext.SignInSessions
                .Where(session => session.ExpiresAt < now)
                .ExecuteDeleteAsync(cancellationToken);

            LogPruneCompleted(_logger, prunedSessions, now);
        }
        catch (Exception exception)
            when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogPruneFailed(_logger, exception);
        }
    }

    [LoggerMessage(EventId = EventIds.SignInSessionPruneCompleted, Level = LogLevel.Information, Message = "Sign-in session prune removed {SessionCount} session(s) that expired before {Threshold}")]
    private static partial void LogPruneCompleted(ILogger logger, int sessionCount, DateTimeOffset threshold);

    [LoggerMessage(EventId = EventIds.SignInSessionPruneFailed, Level = LogLevel.Error, Message = "Sign-in session prune pass failed. Expired sessions will be retried on the next daily pass.")]
    private static partial void LogPruneFailed(ILogger logger, Exception exception);
}
