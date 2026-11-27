using System.Data.Common;
using LotroKoniecDev.SharedKernel.BuildingBlocks;
using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.TranslationSystem.API.Auth.CurrentUserAccessing;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Domain.Core.Errors;
using LotroKoniecDev.TranslationSystem.Persistence.SaveBudget;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate.Enums;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslatorAggregate;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;

namespace LotroKoniecDev.TranslationSystem.API.Features.Translations;

/// <summary>
/// The steps every translation write shares: the checks on the typed text, the permits of the daily save
/// budget (ADR-0072) and the overwrite log line. The save and the copy into identical rows both call it,
/// so the two cannot drift apart (#1587). Its log lines go to the caller's logger, so each names its
/// endpoint.
/// </summary>
internal sealed partial class TranslationSaveBudgetGate
{
    private readonly ICurrentUserAccessor _currentUserAccessor;
    private readonly ITranslationSaveLedger _saveLedger;
    private readonly IOptions<TranslationSaveBudgetSettings> _saveBudgetSettings;

    /// <summary>
    /// The row must be live and the Polish must keep the English placeholder count (#1437). The validator
    /// cannot see the English, so this runs in the handler, before any permit.
    /// </summary>
    public static Result CheckWritable(Translation translation, string translatedText)
    {
        ArgumentNullException.ThrowIfNull(translation);

        // A soft-removed row is out of translation work (spec 0001).
        if (translation.IsRemoved)
        {
            return Result.Failure(DomainErrors.TranslationEntity.CannotEditRemoved);
        }

        return translation.Source.CheckPlaceholderCountOf(translatedText);
    }

    /// <summary>
    /// One permit per row the action saves (ADR-0072 §4 and its amendment of 2026-11-27). Only the first can be
    /// an overwrite: a copy goes only into rows with no Polish. The ids exist before anything is recorded, so a
    /// caller can hand them back whatever fails.
    /// </summary>
    public static IReadOnlyList<TranslationSavePermit> NewPermits(int saves, bool replacesPolish, DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(saves, 1);

        return Enumerable.Range(0, saves)
            .Select(index => new TranslationSavePermit(Guid.CreateVersion7(now), replacesPolish && index == 0))
            .ToList();
    }

    /// <summary>
    /// Records the permits of a plain translator and judges them together; a refusal hands every one back.
    /// Any other caller only clears the expired rows (ADR-0072 amendment of 2026-11-21).
    /// </summary>
    public async Task<Result> SpendAsync(
        ILogger logger,
        TranslatorId translatorId,
        IReadOnlyList<TranslationSavePermit> permits,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(permits);

        TranslationSaveBudgetSettings settings = _saveBudgetSettings.Value;
        TimeSpan window = TimeSpan.FromHours(settings.WindowHours);

        if (!_currentUserAccessor.HasDailySaveBudget())
        {
            // Without this, rows would wait for a plain translator's save, and there may be none once
            // every regular translator is a Reviewer (#1488).
            await _saveLedger.DeleteExpiredAsync(now, window, cancellationToken);
            return Result.Success();
        }

        int saves = permits.Count;
        bool replacesPolish = permits.Any(permit => permit.ReplacedPolish);
        List<Guid> permitIds = permits.Select(permit => permit.Id).ToList();

        TranslationSaveCounts counts = await _saveLedger.RecordAsync(permits, translatorId, now, window, cancellationToken);

        TranslationSaveBudgetVerdict verdict = TranslationSaveBudget.Judge(counts, replacesPolish, settings);

        if (verdict.RefusedBy is TranslationSaveBudgetKind refusedBy)
        {
            (int count, int limit, Error error) = refusedBy switch
            {
                TranslationSaveBudgetKind.Saves when saves > 1 => (counts.Saves, settings.SaveLimit,
                    TranslationSaveBudgetErrors.FillExceedsSaveBudget(saves, settings.SaveLimit, settings.WindowHours)),
                TranslationSaveBudgetKind.Saves => (counts.Saves, settings.SaveLimit,
                    TranslationSaveBudgetErrors.SaveBudgetExhausted(settings.SaveLimit, settings.WindowHours)),
                _ => (counts.Overwrites, settings.OverwriteLimit,
                    TranslationSaveBudgetErrors.OverwriteBudgetExhausted(settings.OverwriteLimit, settings.WindowHours))
            };

            // Not the request's token: a client that leaves now must not keep a refused permit.
            await _saveLedger.RemoveAsync(permitIds, CancellationToken.None);
            LogSaveBudget(
                logger, translatorId.Value, refusedBy, TranslationSaveBudgetOutcome.Refused,
                count, limit, settings.WindowHours);
            return Result.Failure(error);
        }

        // Every save at or above a threshold, not only the crossing one: two parallel saves can both skip the
        // exact number, and the alert looks back one hour.
        if (verdict.SavesAtAlertThreshold)
        {
            LogSaveBudget(
                logger, translatorId.Value, TranslationSaveBudgetKind.Saves, TranslationSaveBudgetOutcome.AlertThreshold,
                counts.Saves, settings.SaveLimit, settings.WindowHours);
        }

        if (verdict.OverwritesAtAlertThreshold)
        {
            LogSaveBudget(
                logger, translatorId.Value, TranslationSaveBudgetKind.Overwrites, TranslationSaveBudgetOutcome.AlertThreshold,
                counts.Overwrites, settings.OverwriteLimit, settings.WindowHours);
        }

        return Result.Success();
    }

    /// <summary>
    /// Gives a failed fill's permits back (ADR-0072 amendment of 2026-11-27). Never on the request's token,
    /// and a failure here is logged, not thrown, so the write's own error reaches the caller. A caller with no
    /// budget took none.
    /// </summary>
    public async Task HandBackAsync(ILogger logger, TranslatorId translatorId, IReadOnlyList<TranslationSavePermit> permits)
    {
        ArgumentNullException.ThrowIfNull(permits);

        if (!_currentUserAccessor.HasDailySaveBudget())
        {
            return;
        }

        try
        {
            await _saveLedger.RemoveAsync(permits.Select(permit => permit.Id).ToList(), CancellationToken.None);
        }
        catch (Exception exception) when (exception is DbException or RetryLimitExceededException)
        {
            LogPermitsNotHandedBack(logger, exception, translatorId.Value, permits.Count);
        }
    }

    /// <summary>
    /// Only after the commit, so a refused or conflicting save never counts toward the owner's total alert
    /// (ADR-0072 §6).
    /// </summary>
    public void LogOverwrite(
        ILogger logger,
        TranslatorId translatorId,
        int fileId,
        long gossipId,
        TranslationStatus previousStatus)
        => LogTranslationOverwritten(
            logger, translatorId.Value, fileId, gossipId, previousStatus, _currentUserAccessor.HasDailySaveBudget());

    public TranslationSaveBudgetGate(
        ICurrentUserAccessor currentUserAccessor,
        ITranslationSaveLedger saveLedger,
        IOptions<TranslationSaveBudgetSettings> saveBudgetSettings)
    {
        _currentUserAccessor = currentUserAccessor;
        _saveLedger = saveLedger;
        _saveBudgetSettings = saveBudgetSettings;
    }

    [LoggerMessage(EventId = EventIds.TranslationOverwritten, Level = LogLevel.Information,
        Message = TranslationSaveBudgetLogText.OverwrittenTemplate)]
    private static partial void LogTranslationOverwritten(
        ILogger logger, Guid translatorId, int fileId, long gossipId, TranslationStatus previousStatus, bool budgeted);

    [LoggerMessage(EventId = EventIds.TranslationSaveBudgetWarning, Level = LogLevel.Warning,
        Message = TranslationSaveBudgetLogText.BudgetTemplate)]
    private static partial void LogSaveBudget(
        ILogger logger,
        Guid translatorId,
        TranslationSaveBudgetKind budget,
        TranslationSaveBudgetOutcome outcome,
        int count,
        int limit,
        int windowHours);

    [LoggerMessage(EventId = EventIds.TranslationSavePermitsNotHandedBack, Level = LogLevel.Warning,
        Message = "Translator {TranslatorId} keeps {PermitCount} save permits of a failed fill until they expire")]
    private static partial void LogPermitsNotHandedBack(
        ILogger logger, Exception exception, Guid translatorId, int permitCount);
}
