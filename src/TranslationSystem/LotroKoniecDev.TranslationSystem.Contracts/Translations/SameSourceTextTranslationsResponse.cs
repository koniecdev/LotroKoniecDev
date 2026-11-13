namespace LotroKoniecDev.TranslationSystem.Contracts.Translations;

/// <summary>
/// The other live rows whose English text is exactly the same as one row's (#1347). <see cref="Items"/>
/// holds at most a page of them, rows with Polish first; <see cref="TotalCount"/> counts them all.
/// <see cref="HasConflictingTranslations"/> covers the whole live group, the asked row included.
/// </summary>
public sealed record SameSourceTextTranslationsResponse(
    IReadOnlyCollection<SameSourceTextItemResponse> Items,
    int TotalCount,
    bool HasConflictingTranslations);
