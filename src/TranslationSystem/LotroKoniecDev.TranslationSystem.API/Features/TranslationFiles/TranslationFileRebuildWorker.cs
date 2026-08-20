using Microsoft.Extensions.Options;

namespace LotroKoniecDev.TranslationSystem.API.Features.TranslationFiles;

/// <summary>
/// Runs the artifact rebuilds the write handlers schedule (PERF-04, ADR-0021). After the first signal
/// it waits one <see cref="TranslationFileRebuildSettings.DebounceWindow"/>, takes everything that
/// arrived in the meantime, and runs one rebuild per language.
/// The wait is a fixed window and not one that restarts on every signal, so a steady stream of writes
/// can never keep the rebuild from happening.
/// It calls the projector with the host's own cancellation token and never a request token, so a
/// client that disconnects after a commit can no longer cancel the rebuild.
/// A failed rebuild is logged and scheduled again, so the artifact catches up, one window at a time.
/// Two things are accepted for this artifact, because it can always be rebuilt: signals still waiting
/// when the process shuts down are lost, and the next write schedules a new one; and a second instance
/// would not see this one's signals. The whole pipeline assumes a single instance by design, like the
/// projector's gate.
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
            // Ignored on purpose, because an exception leaving ExecuteAsync stops the whole host.
            // Scheduling it again lets the artifact catch up. A shutdown cancellation still passes
            // through.
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
