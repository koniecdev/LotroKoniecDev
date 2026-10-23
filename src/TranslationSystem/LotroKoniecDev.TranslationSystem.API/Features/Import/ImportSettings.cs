using LotroKoniecDev.TranslationSystem.Contracts.Import;

namespace LotroKoniecDev.TranslationSystem.API.Features.Import;

/// <summary>
/// An upload that would soft-remove more than <see cref="MaxRemovedFractionWithoutOverride"/> of the
/// active rows is refused unless the admin overrides it, because a half-written export looks like a
/// mass removal (spec 0001, Q4).
/// </summary>
internal sealed class ImportSettings
{
    public const string ConfigurationSection = "Import";

    public double MaxRemovedFractionWithoutOverride { get; init; } = 0.20;

    /// <summary>
    /// In bytes, for both the request body and the multipart form. Configurable so ops can raise it as
    /// the export grows with a restart, not a code change (spec 0003, #208).
    /// </summary>
    public long MaxUploadBytes { get; init; } = ImportUploadLimits.MaxUploadBytes;

    /// <summary>
    /// Rows per load-change-save-clear round, so memory stays flat however many rows changed (spec
    /// 0006 gives 2,000 to 5,000). Configurable mostly so tests can force a chunk boundary cheaply.
    /// </summary>
    public int ApplyChunkSize { get; init; } = 5_000;
}
