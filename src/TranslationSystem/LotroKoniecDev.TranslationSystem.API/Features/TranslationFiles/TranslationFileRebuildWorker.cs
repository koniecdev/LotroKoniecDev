using Microsoft.Extensions.Options;

namespace LotroKoniecDev.TranslationSystem.API.Features.TranslationFiles;

/// <summary>
/// After the first signal it waits one <see cref="TranslationFileRebuildSettings.DebounceWindow"/> and
/// runs one rebuild per language (PERF-04, ADR-0021). The window is fixed, not restarted per signal, so
/// a steady stream of writes can never block the rebuild. It uses the host's token, so a disconnecting
/// client cannot cancel it. The artifact can always be rebuilt, so signals lost at shutdown, and a
/// single-instance design, are accepted.
/// </summary>
internal sealed partial class TranslationFileRebuildWorker : BackgroundService
{
    private readonly TranslationFileRebuildScheduler _scheduler;
    private readonly IPrecomputedTranslationFileProjector _projector;
    private readonly TimeProvider _timeProvider;
    private readonly TranslationFileRebuildSettings _settings;
    private readonly ILogger<TranslationFileRebuildWorker> _logger;

    public TranslationFileRebuildWorker(
        TranslationFileRebuildScheduler scheduler,
        IPrecomputedTranslationFileProjector projector,
        TimeProvider timeProvider,
        IOptions<TranslationFileRebuildSettings> settings,
        ILogger<TranslationFileRebuildWorker> logger)
    {
        _scheduler = scheduler;
        _projector = projector;
        _timeProvider = timeProvider;
        _settings = settings.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (await _scheduler.Reader.WaitToReadAsync(stoppingToken))
        {
            if (_settings.DebounceWindow > TimeSpan.Zero)
            {
                await Task.Delay(_settings.DebounceWindow, _timeProvider, stoppingToken);
            }

            Dictionary<string, int> signalCountsByLanguage = new(StringComparer.OrdinalIgnoreCase);
            while (_scheduler.Reader.TryRead(out string? language))
            {
                signalCountsByLanguage[language] = signalCountsByLanguage.GetValueOrDefault(language) + 1;
            }

            foreach ((string language, int signalCount) in signalCountsByLanguage)
            {
                await RebuildAsync(language, signalCount, stoppingToken);
            }
        }
    }

    private async Task RebuildAsync(string language, int signalCount, CancellationToken stoppingToken)
    {
        try
        {
            await _projector.RebuildAsync(language, stoppingToken);
            LogRebuildCompleted(_logger, language, signalCount);
        }
        catch (Exception exception)
            when (exception is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
        {
            // An exception leaving ExecuteAsync stops the host. Scheduling again lets the artifact catch
            // up.
            LogRebuildFailed(_logger, exception, language);
            _scheduler.Schedule(language);
        }
        finally
        {
            _scheduler.MarkCompleted(signalCount);
        }
    }

    [LoggerMessage(EventId = EventIds.TranslationFileRebuildCompleted, Level = LogLevel.Information, Message = "Precomputed translation file for '{Language}' rebuilt ({SignalCount} coalesced write signal(s))")]
    private static partial void LogRebuildCompleted(ILogger logger, string language, int signalCount);

    [LoggerMessage(EventId = EventIds.TranslationFileRebuildFailed, Level = LogLevel.Error, Message = "Precomputed translation file rebuild for '{Language}' failed; re-scheduled for the next debounce window")]
    private static partial void LogRebuildFailed(ILogger logger, Exception exception, string language);
}
