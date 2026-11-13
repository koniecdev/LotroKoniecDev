using LotroKoniecDev.TranslationSystem.Persistence.SaveBudget;

namespace LotroKoniecDev.TranslationSystem.API.Features.Translations;

/// <summary>
/// Judges one budgeted save from the account's counts, which already include this save (ADR-0072).
/// </summary>
internal static class TranslationSaveBudget
{
    public static TranslationSaveBudgetVerdict Judge(
        TranslationSaveCounts counts,
        bool replacesPolish,
        TranslationSaveBudgetSettings settings)
    {
        ArgumentNullException.ThrowIfNull(counts);
        ArgumentNullException.ThrowIfNull(settings);

        // Saves first: the overwrite answer tells the translator that untranslated rows still go
        // through, which is false once the save budget is spent too.
        if (counts.Saves > settings.SaveLimit)
        {
            return new TranslationSaveBudgetVerdict(TranslationSaveBudgetKind.Saves, false, false);
        }

        if (replacesPolish && counts.Overwrites > settings.OverwriteLimit)
        {
            return new TranslationSaveBudgetVerdict(TranslationSaveBudgetKind.Overwrites, false, false);
        }

        return new TranslationSaveBudgetVerdict(
            RefusedBy: null,
            SavesAtAlertThreshold: counts.Saves >= settings.SaveAlertThreshold,
            OverwritesAtAlertThreshold: replacesPolish && counts.Overwrites >= settings.OverwriteAlertThreshold);
    }
}
