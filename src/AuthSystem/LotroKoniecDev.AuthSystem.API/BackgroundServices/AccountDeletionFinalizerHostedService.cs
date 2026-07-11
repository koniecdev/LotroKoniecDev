using Microsoft.Extensions.Options;
using LotroKoniecDev.AuthSystem.API.Services.Gdpr;
using LotroKoniecDev.AuthSystem.API.Settings;

namespace LotroKoniecDev.AuthSystem.API.BackgroundServices;

/// <summary>
/// Periodically finalizes GDPR account deletions whose grace period has elapsed.
/// Runs once at startup (to catch up after downtime), then on every poll interval.
/// </summary>
internal sealed partial class AccountDeletionFinalizerHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly GdprSettings _gdprSettings;
    private readonly ILogger<AccountDeletionFinalizerHostedService> _logger;

    public AccountDeletionFinalizerHostedService(
        IServiceScopeFactory serviceScopeFactory,
        TimeProvider timeProvider,
        IOptions<GdprSettings> gdprSettings,
        ILogger<AccountDeletionFinalizerHostedService> logger)
    {
        _serviceScopeFactory = serviceScopeFactory;
        _timeProvider = timeProvider;
        _gdprSettings = gdprSettings.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RunOnceAsync(stoppingToken);

        using PeriodicTimer timer = new(_gdprSettings.DeletionFinalizationPollInterval, _timeProvider);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await RunOnceAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Graceful shutdown.
        }
    }

    private async Task RunOnceAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using AsyncServiceScope scope = _serviceScopeFactory.CreateAsyncScope();
            IAccountDeletionFinalizer finalizer =
                scope.ServiceProvider.GetRequiredService<IAccountDeletionFinalizer>();

            await finalizer.FinalizeDueAccountsAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogFinalizerRunFailed(_logger, ex);
        }
    }

    [LoggerMessage(EventId = EventIds.GdprDeletionFinalizerRunFailed, Level = LogLevel.Error, Message = "GDPR deletion finalizer run failed. Will retry on the next poll.")]
    private static partial void LogFinalizerRunFailed(ILogger logger, Exception exception);
}
