using LotroKoniecDev.SharedKernel.BuildingBlocks;
using LotroKoniecDev.SharedKernel.Enums;

namespace LotroKoniecDev.TranslationSystem.API.Features.Translations;

/// <summary>
/// <see cref="TypeOfError.TooManyRequests"/>, so the API answers 429 (ADR-0072).
/// </summary>
internal static class TranslationSaveBudgetErrors
{
    public static Error SaveBudgetExhausted(int limit, int windowHours)
        => new("Translations.SaveBudgetExhausted",
            $"This account has made {limit} translation saves in the last {windowHours} hours. Try again later.",
            TypeOfError.TooManyRequests);

    public static Error OverwriteBudgetExhausted(int limit, int windowHours)
        => new("Translations.OverwriteBudgetExhausted",
            $"This account has replaced existing Polish text {limit} times in the last {windowHours} hours. "
            + "Try again later. Rows with no translation can still be saved.",
            TypeOfError.TooManyRequests);
}
