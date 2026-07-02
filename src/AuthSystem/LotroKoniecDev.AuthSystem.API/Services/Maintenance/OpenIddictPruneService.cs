using OpenIddict.Abstractions;

namespace LotroKoniecDev.AuthSystem.API.Services.Maintenance;

/// <summary>
/// Daily background prune of stale OpenIddict tokens and authorizations (PERF-02). Rolling reference
/// refresh tokens (<c>UseReferenceRefreshTokens()</c>) write a new token row on every refresh and
/// nothing else ever deletes them, so without this pass the <c>OpenIddictTokens</c> and
/// <c>OpenIddictAuthorizations</c> tables grow without bound and slow every token-endpoint lookup.
/// </summary>
internal sealed partial class OpenIddictPruneService : BackgroundService
{
    /// <summary>
    /// Rows younger than this are never pruned, mirroring the default threshold of OpenIddict's own
    /// Quartz integration; the prune only ever removes rows that are already expired or invalid.
    /// </summary>
    internal static readonly TimeSpan RetentionPeriod = TimeSpan.FromDays(14);

    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(1);

    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<OpenIddictPruneService> _logger;

    public OpenIddictPruneService(
        IServiceScopeFactory serviceScopeFactory,
        TimeProvider timeProvider,
        ILogger<OpenIddictPruneService> logger)
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
    /// Runs a single prune pass. Tokens are pruned before authorizations because OpenIddict never
    /// deletes an authorization that still has tokens attached. The managers are scoped (they ride on
    /// <c>AuthDbContext</c>), hence the explicit scope per pass. Every failure is logged and
    /// swallowed — an exception escaping <see cref="ExecuteAsync"/> stops the whole host — except a
    /// cancellation of <paramref name="cancellationToken"/> itself, which is a normal shutdown and
    /// must propagate.
    /// </summary>
    internal async Task PruneOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using AsyncServiceScope scope = _serviceScopeFactory.CreateAsyncScope();

            IOpenIddictTokenManager tokenManager =
                scope.ServiceProvider.GetRequiredService<IOpenIddictTokenManager>();
            IOpenIddictAuthorizationManager authorizationManager =
                scope.ServiceProvider.GetRequiredService<IOpenIddictAuthorizationManager>();

            DateTimeOffset threshold = _timeProvider.GetUtcNow() - RetentionPeriod;

            long prunedTokens = await tokenManager.PruneAsync(threshold, cancellationToken);
            long prunedAuthorizations = await authorizationManager.PruneAsync(threshold, cancellationToken);

            LogPruneCompleted(_logger, prunedTokens, prunedAuthorizations, threshold);
        }
        catch (Exception exception)
            when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogPruneFailed(_logger, exception);
        }
    }

    [LoggerMessage(EventId = EventIds.OpenIddictPruneCompleted, Level = LogLevel.Information, Message = "OpenIddict prune removed {TokenCount} token(s) and {AuthorizationCount} authorization(s) created before {Threshold}")]
    private static partial void LogPruneCompleted(ILogger logger, long tokenCount, long authorizationCount, DateTimeOffset threshold);

    [LoggerMessage(EventId = EventIds.OpenIddictPruneFailed, Level = LogLevel.Error, Message = "OpenIddict prune pass failed. Stale tokens and authorizations will be retried on the next daily pass.")]
    private static partial void LogPruneFailed(ILogger logger, Exception exception);
}
