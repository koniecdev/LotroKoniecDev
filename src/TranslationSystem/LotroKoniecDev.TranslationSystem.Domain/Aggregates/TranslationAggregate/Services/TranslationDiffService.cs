using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate.Enums;

namespace LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Services;

/// <summary>
/// Compares an uploaded export with the stored source state over the whole file, keyed by
/// <see cref="FragmentKeyValue"/> (spec 0001) and working on value rows only (spec 0006). The upload
/// arrives already hashed as a key to hash map, the catalog streams past as
/// <see cref="StoredSourceDigest"/> rows, and sources are compared hash to hash, so nothing here
/// holds a source string or an aggregate. It builds a <see cref="TranslationDiffPlan"/> without
/// touching the database. The import handler carries the plan out inside its transaction once the
/// truncation guard passes.
/// </summary>
/// <remarks>
/// The echo guard (spec 0012): the admin exports from their own patched DAT, so a row that is still
/// patched comes back with our Polish as its "source". Without the guard, every translated row that
/// is still in the DAT would look source-changed on every update. That would invalidate them all and
/// also write Polish over the English. So an incoming row that differs from the stored source but
/// matches the row's <see cref="StoredSourceDigest.EchoHash"/> is our own text coming back: it is
/// treated like an unchanged source (or a restored one when the row was soft-removed) and counted on
/// its own for reporting.
/// The guard only knows the row's current Polish. An older Polish still sitting in the DAT after a
/// re-edit looks exactly like a real change, and it poisons the source in the way #564 repairs.
/// Catching that needs the row's Polish history (TP-15 / #50, post-MVP).
/// </remarks>
public static class TranslationDiffService
{
    /// <summary>
    /// This method empties <paramref name="incomingByKey"/>: every key that matches a stored row is
    /// removed while <paramref name="existing"/> streams past. On return the map holds exactly the
    /// added keys and is handed to the plan as its added set, so the caller must not reuse it.
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
