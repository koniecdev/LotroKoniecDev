namespace LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate.Enums;

/// <summary>
/// The state of a single translation row. It is one enum and not an enum plus a separate
/// "invalidated" flag, so a combination that makes no sense, such as <see cref="Approved"/> and
/// invalidated at once, cannot be written down at all (spec 0001, Q6). Invalidating a row means
/// moving it to <see cref="NeedsReview"/>.
/// </summary>
public enum TranslationStatus
{
    Unset,
    Untranslated,
    Draft,
    Approved,
    NeedsReview
}
