using LotroKoniecDev.TranslationSystem.Contracts.Import;

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

    /// <summary>
    /// Maximum accepted upload size in bytes, enforced on the import endpoint (request body + multipart
    /// form length). Defaults to <see cref="ImportUploadLimits.MaxUploadBytes"/>; configurable so ops
    /// can lift it as the export grows without a code change (it is read at startup, so a restart — not
    /// a redeploy — applies a new value) (spec 0003, #208).
    /// </summary>
    public long MaxUploadBytes { get; init; } = ImportUploadLimits.MaxUploadBytes;

    /// <summary>
    /// How many rows the import's apply pass mutates per chunk (load by id → mutate → save → clear
    /// tracker), bounding the transaction's working set no matter how many rows changed (spec 0006).
    /// Default sits in the spec's 2–5k band; configurable mainly so tests can force chunk
    /// boundaries cheaply.
    /// </summary>
    public int ApplyChunkSize { get; init; } = 5_000;
}
