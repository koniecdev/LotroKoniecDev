namespace LotroKoniecDev.TranslationSystem.Contracts.Translations;

/// <summary>
/// The answer of the copy into identical rows (#1587). <see cref="TranslationSaved"/> is <c>false</c> when
/// the row already had this text, so only the empty rows were written.
/// </summary>
/// <param name="FilledCount">The empty rows with the same English that got the text.</param>
public sealed record FillIdenticalTranslationsResponse(
    TranslationDetailResponse Translation,
    int FilledCount,
    bool TranslationSaved);
