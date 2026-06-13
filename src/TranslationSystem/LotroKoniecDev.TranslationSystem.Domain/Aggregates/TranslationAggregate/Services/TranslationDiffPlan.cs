using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.ValueObjects;

namespace LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Services;

/// <summary>An existing row whose English source the upload changed.</summary>
public sealed record TranslationSourceChange(Translation Existing, TranslationSource NewSource);

/// <summary>
/// The outcome of diffing an upload against the stored state (spec 0001). The plan is computed
/// without mutating anything so the import handler can enforce the truncation guard from the
/// removed fraction <em>before</em> any change is applied; the handler then realizes the plan.
/// </summary>
public sealed class TranslationDiffPlan
{
    public IReadOnlyList<Translation> Added { get; }
    public IReadOnlyList<TranslationSourceChange> SourceChanges { get; }
    public IReadOnlyList<Translation> Removed { get; }
    public IReadOnlyList<Translation> Restored { get; }
    public int UnchangedCount { get; }

    /// <summary>Source-changed rows that carried Polish work and were therefore invalidated.</summary>
    public int InvalidatedCount { get; }

    /// <summary>Active (non-removed) stored rows — the denominator of the removed-fraction guard.</summary>
    public int ComparableExistingCount { get; }

    public TranslationDiffPlan(
        IReadOnlyList<Translation> added,
        IReadOnlyList<TranslationSourceChange> sourceChanges,
        IReadOnlyList<Translation> removed,
        IReadOnlyList<Translation> restored,
        int unchangedCount,
        int invalidatedCount,
        int comparableExistingCount)
    {
        Added = added;
        SourceChanges = sourceChanges;
        Removed = removed;
        Restored = restored;
        UnchangedCount = unchangedCount;
        InvalidatedCount = invalidatedCount;
        ComparableExistingCount = comparableExistingCount;
    }

    /// <summary>
    /// The fraction of active stored rows this upload would soft-remove. Zero on a baseline import
    /// (no active rows to remove), so the guard never trips on first load.
    /// </summary>
    public double RemovedFraction
        => ComparableExistingCount is 0 ? 0d : (double)Removed.Count / ComparableExistingCount;
}
