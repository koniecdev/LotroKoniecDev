namespace LotroKoniecDev.TranslationSystem.API.Features.Translations;

/// <summary>
/// The per-account save budget of ADR-0072, owner decision on #687. Every number is a setting, so the
/// owner can change it in the box <c>.env</c> without a release.
/// </summary>
internal sealed class TranslationSaveBudgetSettings
{
    public const string ConfigurationSection = "TranslationSaveBudget";

    /// <summary>
    /// A longer window would turn the budget table into a change history, which is #50's job.
    /// </summary>
    public const int MaxWindowHours = 24 * 31;

    public int SaveLimit { get; init; } = 1_000;
    public int SaveAlertThreshold { get; init; } = 500;

    /// <summary>
    /// Saves on a row that already has Polish. They count against both budgets.
    /// </summary>
    public int OverwriteLimit { get; init; } = 100;

    public int OverwriteAlertThreshold { get; init; } = 50;
    public int WindowHours { get; init; } = 24;
}
