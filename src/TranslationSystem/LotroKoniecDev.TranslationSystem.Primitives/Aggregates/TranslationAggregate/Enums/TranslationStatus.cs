namespace LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate.Enums;

/// <summary>
/// One enum, not an enum plus an "invalidated" flag, so a state like approved-and-invalidated cannot
/// exist (spec 0001, Q6). Invalidating a row moves it to <see cref="NeedsReview"/>.
/// </summary>
public enum TranslationStatus
{
    Unset,
    Untranslated,
    Draft,
    Approved,
    NeedsReview
}
