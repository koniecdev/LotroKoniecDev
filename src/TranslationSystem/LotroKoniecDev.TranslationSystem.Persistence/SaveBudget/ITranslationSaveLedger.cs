using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslatorAggregate;

namespace LotroKoniecDev.TranslationSystem.Persistence.SaveBudget;

/// <summary>
/// The per-account save budget's store (ADR-0072). Every call commits at once, outside any
/// transaction and the change tracker, so a parallel request always counts a committed row.
/// </summary>
public interface ITranslationSaveLedger
{
    /// <summary>
    /// Records the permits first, in one statement, and counts after, so parallel requests can never pass
    /// more saves than the limit. Rows older than the window are deleted on the way, for every account.
    /// </summary>
    Task<TranslationSaveCounts> RecordAsync(
        IReadOnlyList<TranslationSavePermit> permits,
        TranslatorId translatorId,
        DateTimeOffset savedAt,
        TimeSpan window,
        CancellationToken cancellationToken);

    /// <summary>
    /// Deletes every account's rows older than the window. A save without a budget calls it too, so a
    /// translator who becomes a Reviewer leaves no rows behind (ADR-0072 amendment of 2026-11-21).
    /// </summary>
    Task DeleteExpiredAsync(DateTimeOffset now, TimeSpan window, CancellationToken cancellationToken);

    /// <summary>
    /// Hands permits back after a refusal, so a refused attempt spends nothing. A fill also hands them back
    /// when its write fails (ADR-0072 amendment of 2026-11-27).
    /// </summary>
    Task RemoveAsync(IReadOnlyList<Guid> saveIds, CancellationToken cancellationToken);
}
