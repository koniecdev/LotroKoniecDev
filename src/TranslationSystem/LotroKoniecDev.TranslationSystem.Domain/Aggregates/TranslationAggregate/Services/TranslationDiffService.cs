using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate.Enums;

namespace LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Services;

/// <summary>
/// Diffs an upload against the stored sources hash to hash (spec 0001, spec 0006), so nothing here
/// holds a source string or an aggregate, and nothing touches the database.
/// </summary>
/// <remarks>
/// The echo guard (spec 0012): the admin exports from their own patched DAT, so a still-patched row
/// comes back with our Polish as its "source". Without the guard every translated row would look
/// source-changed on every update, be invalidated, and get Polish written over its English. A row
/// that matches <see cref="StoredSourceDigest.EchoHash"/> is treated as unchanged (or restored).
/// The guard knows only the current Polish: an older Polish still in the DAT after a re-edit looks
/// like a real change and poisons the source (the case #564 repairs; TP-15 / #50 would catch it).
/// </remarks>
public static class TranslationDiffService
{
    /// <summary>
    /// Empties <paramref name="incomingByKey"/> of every key that matches a stored row. What is left
    /// becomes the plan's added set, so the caller must not reuse the map.
    /// </summary>
    public static async Task<TranslationDiffPlan> ComputePlanAsync(
        IAsyncEnumerable<StoredSourceDigest> existing,
        Dictionary<FragmentKeyValue, SourceHash> incomingByKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(incomingByKey);

        Dictionary<FragmentKeyValue, TranslationId> sourceChangedByKey = [];
        List<TranslationId> removedIds = [];
        List<TranslationId> restoredIds = [];
        int unchangedCount = 0;
        int echoedCount = 0;
        int invalidatedCount = 0;
        int comparableExistingCount = 0;

        await foreach (StoredSourceDigest stored in existing.WithCancellation(cancellationToken))
        {
            if (!stored.IsRemoved)
            {
                comparableExistingCount++;
            }

            if (!incomingByKey.Remove(stored.Key, out SourceHash incomingHash))
            {
                if (!stored.IsRemoved)
                {
                    removedIds.Add(stored.Id);
                }

                continue;
            }

            // The source check runs first, so a row whose stored source is already its own Polish (a
            // poisoned source from an import before the guard) counts as unchanged, not as an echo.
            bool isEcho = incomingHash != stored.SourceHash && incomingHash == stored.EchoHash;
            if (incomingHash == stored.SourceHash || isEcho)
            {
                if (isEcho)
                {
                    echoedCount++;
                }

                if (stored.IsRemoved)
                {
                    restoredIds.Add(stored.Id);
                }
                else
                {
                    unchangedCount++;
                }

                continue;
            }

            sourceChangedByKey.Add(stored.Key, stored.Id);
            if (HasPolish(stored.Status))
            {
                invalidatedCount++;
            }
        }

        return new TranslationDiffPlan(
            incomingByKey,
            sourceChangedByKey,
            removedIds,
            restoredIds,
            unchangedCount,
            echoedCount,
            invalidatedCount,
            comparableExistingCount);
    }

    private static bool HasPolish(TranslationStatus status)
        => status is TranslationStatus.Draft or TranslationStatus.Approved or TranslationStatus.NeedsReview;
}
