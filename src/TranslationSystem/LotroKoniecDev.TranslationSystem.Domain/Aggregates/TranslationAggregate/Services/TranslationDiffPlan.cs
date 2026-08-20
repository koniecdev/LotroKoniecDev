using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate;

namespace LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Services;

/// <summary>
/// The result of comparing an upload with the stored state (spec 0001), held as value rows only
/// (spec 0006): added is a set of keys, source-changed maps keys to row ids, removed and restored are
/// id lists. There are no aggregates and no source strings here, so the plan grows with the diff and
/// not with the file or the catalog. Building the plan changes nothing, which lets the import handler
/// run the truncation guard on the removed fraction before it applies anything. The handler then
/// carries the plan out inside its transaction (COPY for added rows, chunked updates for the rest)
/// and re-reads the row content it needs from the buffered upload.
/// </summary>
public sealed class TranslationDiffPlan
{
    private readonly Dictionary<FragmentKeyValue, SourceHash> _addedByKey;

    /// <summary>
    /// Existing rows whose English source the upload changed, keyed by fragment identity, so the
    /// apply pass can read the new source for each key from the upload stream.
    /// </summary>
    public IReadOnlyDictionary<FragmentKeyValue, TranslationId> SourceChangedByKey { get; }

    public IReadOnlyList<TranslationId> RemovedIds { get; }
    public IReadOnlyList<TranslationId> RestoredIds { get; }
    public int UnchangedCount { get; }

    /// <summary>
    /// Rows whose incoming "source" was our own Polish coming back from a patched DAT (spec 0012).
    /// They are matched through <see cref="StoredSourceDigest.EchoHash"/> and treated as an unchanged
    /// source, so they are already counted in <see cref="UnchangedCount"/>, or in
    /// <see cref="RestoredIds"/> when they were soft-removed. This count is for reporting only.
    /// </summary>
    public int EchoedCount { get; }

    /// <summary>
    /// Source-changed rows that carried Polish work and were therefore invalidated.
    /// </summary>
    public int InvalidatedCount { get; }

    /// <summary>
    /// Stored rows that are not soft-removed. The removed-fraction guard divides by this number.
    /// </summary>
    public int ComparableExistingCount { get; }

    public int AddedCount => _addedByKey.Count;

    /// <summary>
    /// How much of the active catalog this upload would soft-remove. A baseline import has no active
    /// rows to remove, so this is zero and the guard never fires on the first load.
    /// </summary>
    public double RemovedFraction
        => ComparableExistingCount is 0 ? 0d : (double)RemovedIds.Count / ComparableExistingCount;

    public bool IsAdded(FragmentKeyValue key) => _addedByKey.ContainsKey(key);

    /// <summary>
    /// <paramref name="addedByKey"/> is what is left of the incoming map after the diff ran: exactly
    /// the keys with no stored row (see <see cref="TranslationDiffService.ComputePlanAsync"/>). The
    /// plan takes it over, so the caller must not use it again.
    /// </summary>
    public TranslationDiffPlan(
        Dictionary<FragmentKeyValue, SourceHash> addedByKey,
        IReadOnlyDictionary<FragmentKeyValue, TranslationId> sourceChangedByKey,
        IReadOnlyList<TranslationId> removedIds,
        IReadOnlyList<TranslationId> restoredIds,
        int unchangedCount,
        int echoedCount,
        int invalidatedCount,
        int comparableExistingCount)
    {
        ArgumentNullException.ThrowIfNull(addedByKey);
        ArgumentNullException.ThrowIfNull(sourceChangedByKey);
        ArgumentNullException.ThrowIfNull(removedIds);
        ArgumentNullException.ThrowIfNull(restoredIds);

        _addedByKey = addedByKey;
        SourceChangedByKey = sourceChangedByKey;
        RemovedIds = removedIds;
        RestoredIds = restoredIds;
        UnchangedCount = unchangedCount;
        EchoedCount = echoedCount;
        InvalidatedCount = invalidatedCount;
        ComparableExistingCount = comparableExistingCount;
    }
}
