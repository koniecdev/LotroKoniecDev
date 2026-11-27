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

    /// <summary>
    /// The save budget's code, so every client shows its usual answer; the text names the rows a copy
    /// into identical rows needed (#1587).
    /// </summary>
    public static Error FillExceedsSaveBudget(int saves, int limit, int windowHours)
        => new("Translations.SaveBudgetExhausted",
            $"Saving this text in {saves} rows does not fit in the {limit} translation saves this account may make "
            + $"in {windowHours} hours. Save the row on its own, or try again later.",
            TypeOfError.TooManyRequests);

    public static Error OverwriteBudgetExhausted(int limit, int windowHours)
        => new("Translations.OverwriteBudgetExhausted",
            $"This account has replaced existing Polish text {limit} times in the last {windowHours} hours. "
            + "Try again later. Rows with no translation can still be saved.",
            TypeOfError.TooManyRequests);
}
