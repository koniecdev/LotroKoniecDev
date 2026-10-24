using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate;

namespace LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Services;

/// <summary>
/// The diff of an upload against the stored state (spec 0001), as keys and ids only (spec 0006), so it
/// grows with the diff, not with the file or the catalog. Building it changes nothing, so the import
/// handler can run the truncation guard before applying anything; it then re-reads the row content
/// from the buffered upload.
/// </summary>
public sealed class TranslationDiffPlan
{
    private readonly Dictionary<FragmentKeyValue, SourceHash> _addedByKey;

    /// <summary>
    /// Keyed by fragment, so the apply pass can read each new source from the upload stream.
    /// </summary>
    public IReadOnlyDictionary<FragmentKeyValue, TranslationId> SourceChangedByKey { get; }

    public IReadOnlyList<TranslationId> RemovedIds { get; }
    public IReadOnlyList<TranslationId> RestoredIds { get; }
    public int UnchangedCount { get; }

    /// <summary>
    /// Rows whose incoming "source" was our own Polish coming back (spec 0012). For reporting only: they
    /// are already counted in <see cref="UnchangedCount"/>, or in <see cref="RestoredIds"/> when
    /// soft-removed.
    /// </summary>
    public int EchoedCount { get; }

    /// <summary>
    /// Source-changed rows that carried Polish work.
    /// </summary>
    public int InvalidatedCount { get; }

    /// <summary>
    /// Stored rows that are not soft-removed: the divisor of <see cref="RemovedFraction"/>.
    /// </summary>
    public int ComparableExistingCount { get; }

    public int AddedCount => _addedByKey.Count;

    /// <summary>
    /// Zero on a baseline import, so the truncation guard never fires on the first load.
    /// </summary>
    public double RemovedFraction
        => ComparableExistingCount is 0 ? 0d : (double)RemovedIds.Count / ComparableExistingCount;

    public bool IsAdded(FragmentKeyValue key) => _addedByKey.ContainsKey(key);

    /// <summary>
    /// The plan takes over <paramref name="addedByKey"/>, so the caller must not use it again.
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
