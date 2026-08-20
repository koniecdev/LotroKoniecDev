using Microsoft.Extensions.Options;
using LotroKoniecDev.AuthSystem.API.Services.Gdpr;
using LotroKoniecDev.AuthSystem.API.Settings;

namespace LotroKoniecDev.AuthSystem.API.BackgroundServices;

/// <summary>
/// Finishes GDPR account deletions once their grace period is over. It runs once at startup, to catch
/// up on anything missed while the app was down, and then on every interval.
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
            // The app is shutting down, which is not an error.
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
