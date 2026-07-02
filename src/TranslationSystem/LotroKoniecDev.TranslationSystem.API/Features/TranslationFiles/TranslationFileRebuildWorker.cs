using Microsoft.Extensions.Options;

namespace LotroKoniecDev.TranslationSystem.API.Features.TranslationFiles;

/// <summary>
/// Debounced background executor of the artifact rebuilds the write handlers schedule (PERF-04,
/// ADR-0021). After the first dirty signal it waits one <see cref="TranslationFileRebuildSettings.DebounceWindow"/>,
/// drains everything queued in the meantime, and runs one rebuild per distinct language — a fixed
/// coalescing window rather than a sliding one, so a sustained write stream can never starve the
/// rebuild. It calls the projector with the host's stopping token, never a request token: a client
/// disconnect after commit cannot cancel the regeneration anymore. A failed rebuild is logged and
/// re-scheduled, so the artifact still converges (paced by the debounce window). Two edges are
/// accepted for this regenerable artifact: signals pending at process shutdown are dropped (the
/// next write reschedules), and a second replica would not see this replica's signals — the whole
/// pipeline is single-replica by design, like the projector's process-wide gate.
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
            // Swallowed deliberately: an exception escaping ExecuteAsync stops the whole host.
            // Re-scheduling keeps the artifact converging; a shutdown cancellation propagates.
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
