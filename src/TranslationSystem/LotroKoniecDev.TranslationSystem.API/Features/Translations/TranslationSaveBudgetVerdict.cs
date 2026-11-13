namespace LotroKoniecDev.TranslationSystem.API.Features.Translations;

/// <param name="RefusedBy">The budget the answer names; <c>null</c> when the save may go on.</param>
internal sealed record TranslationSaveBudgetVerdict(
    TranslationSaveBudgetKind? RefusedBy,
    bool SavesAtAlertThreshold,
    bool OverwritesAtAlertThreshold);
