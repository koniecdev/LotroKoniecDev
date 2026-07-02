using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate.Enums;

namespace LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Services;

/// <summary>
/// Pure diff of an uploaded export against the stored source state, keyed by
/// <see cref="FragmentKeyValue"/> over the full file (spec 0001), computed entirely over value
/// rows (spec 0006): the upload arrives pre-hashed as a key→hash map, the catalog streams by as
/// <see cref="StoredSourceDigest"/>s, and sources compare hash-to-hash — nothing here holds a
/// source string or an aggregate. Produces a <see cref="TranslationDiffPlan"/> without touching
/// the database; the import handler realizes the plan inside its transaction after the truncation
/// guard passes.
/// </summary>
public static class TranslationDiffService
{
    /// <summary>
    /// Consumes <paramref name="incomingByKey"/>: every key matched to a stored row is removed
    /// while <paramref name="existing"/> streams by, so on return the map holds exactly the added
    /// keys and is handed to the plan as its added set — the caller must not reuse it.
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

            if (incomingHash == stored.SourceHash)
            {
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
            invalidatedCount,
            comparableExistingCount);
    }

    private static bool HasPolish(TranslationStatus status)
        => status is TranslationStatus.Draft or TranslationStatus.Approved or TranslationStatus.NeedsReview;
}
