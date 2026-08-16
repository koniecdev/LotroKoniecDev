using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate;

namespace LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Services;

/// <summary>
/// The outcome of diffing an upload against the stored state (spec 0001), in value rows only
/// (spec 0006): added is a key set, source-changed maps keys to row ids, removed/restored are id
/// lists — no aggregates and no source strings, so the plan's size scales with the diff, never the
/// file or the catalog. The plan is computed without mutating anything so the import handler can
/// enforce the truncation guard from the removed fraction <em>before</em> any change is applied;
/// the handler then realizes the plan in its transaction (COPY for added, chunked mutations for
/// the rest), re-reading the row content it needs from the buffered upload.
/// </summary>
public sealed class TranslationDiffPlan
{
    private readonly Dictionary<FragmentKeyValue, SourceHash> _addedByKey;

    /// <summary>
    /// Existing rows whose English source the upload changed, keyed by fragment identity so the
    /// apply pass can re-read the new source for each key from the upload stream.
    /// </summary>
    public IReadOnlyDictionary<FragmentKeyValue, TranslationId> SourceChangedByKey { get; }

    public IReadOnlyList<TranslationId> RemovedIds { get; }
    public IReadOnlyList<TranslationId> RestoredIds { get; }
    public int UnchangedCount { get; }

    /// <summary>
    /// Rows whose incoming "source" was our own Polish echoed back from a patched DAT (spec 0012)
    /// — matched through <see cref="StoredSourceDigest.EchoHash"/> and treated as an identical
    /// source, so they are already counted in <see cref="UnchangedCount"/> (or in
    /// <see cref="RestoredIds"/> when they were soft-removed). Reported for observability only.
    /// </summary>
    public int EchoedCount { get; }

    /// <summary>
    /// Source-changed rows that carried Polish work and were therefore invalidated.
    /// </summary>
    public int InvalidatedCount { get; }

    /// <summary>
    /// Active (non-removed) stored rows — the denominator of the removed-fraction guard.
    /// </summary>
    public int ComparableExistingCount { get; }

    public int AddedCount => _addedByKey.Count;

    /// <summary>
    /// The fraction of active stored rows this upload would soft-remove. Zero on a baseline import
    /// (no active rows to remove), so the guard never trips on first load.
    /// </summary>
    public double RemovedFraction
        => ComparableExistingCount is 0 ? 0d : (double)RemovedIds.Count / ComparableExistingCount;

    public bool IsAdded(FragmentKeyValue key) => _addedByKey.ContainsKey(key);

    /// <summary>
    /// <paramref name="addedByKey"/> is the incoming map after the diff consumed it — exactly the
    /// keys with no stored row (see <see cref="TranslationDiffService.ComputePlanAsync"/>); the
    /// plan takes ownership of it.
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
