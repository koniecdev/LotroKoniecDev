namespace LotroKoniecDev.TranslationSystem.API.Features.Import;

/// <summary>
/// Import safety knobs. The removed-fraction guard rejects an upload that would soft-remove more
/// than <see cref="MaxRemovedFractionWithoutOverride"/> of the active rows unless the admin passes
/// the override flag — a partially written export would otherwise masquerade as a mass removal
/// (spec 0001, Q4). Default 20%.
/// </summary>
internal sealed class ImportSettings
{
    public const string ConfigurationSection = "Import";

    public double MaxRemovedFractionWithoutOverride { get; init; } = 0.20;
}
