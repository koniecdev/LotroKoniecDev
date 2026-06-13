namespace LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate.Enums;

/// <summary>
/// The lifecycle state of a single translation row. A single enum (no parallel invalidation
/// flag) so illegal combinations — e.g. <see cref="Approved"/> while invalidated — are
/// unrepresentable (spec 0001, Q6). Invalidation is the transition to <see cref="NeedsReview"/>.
/// </summary>
public enum TranslationStatus
{
    Unset,
    Untranslated,
    Draft,
    Approved,
    NeedsReview
}
