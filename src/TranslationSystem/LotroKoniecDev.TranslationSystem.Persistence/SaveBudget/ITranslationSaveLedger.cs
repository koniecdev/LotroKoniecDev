using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslatorAggregate;

namespace LotroKoniecDev.TranslationSystem.Persistence.SaveBudget;

/// <summary>
/// The per-account save budget's store (ADR-0072). Every call commits at once, outside any
/// transaction and the change tracker, so a parallel request always counts a committed row.
/// </summary>
public interface ITranslationSaveLedger
{
    /// <summary>
    /// Records the save first and counts after, so parallel requests can never pass more saves than
    /// the limit. Rows older than the window are deleted on the way, for every account.
    /// </summary>
    Task<TranslationSaveCounts> RecordAsync(
        Guid saveId,
        TranslatorId translatorId,
        bool replacedPolish,
        DateTimeOffset savedAt,
        TimeSpan window,
        CancellationToken cancellationToken);

    /// <summary>
    /// Deletes every account's rows older than the window. A save without a budget calls it too, so a
    /// translator who becomes a Reviewer leaves no rows behind (ADR-0072 amendment of 2026-11-21).
    /// </summary>
    Task DeleteExpiredAsync(DateTimeOffset now, TimeSpan window, CancellationToken cancellationToken);

    /// <summary>
    /// Hands the permit back after a refusal, so a refused attempt spends nothing.
    /// </summary>
    Task RemoveAsync(Guid saveId, CancellationToken cancellationToken);
}
