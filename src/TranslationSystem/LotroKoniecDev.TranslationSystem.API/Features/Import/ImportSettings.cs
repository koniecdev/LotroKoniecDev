using LotroKoniecDev.TranslationSystem.Contracts.Import;

namespace LotroKoniecDev.TranslationSystem.API.Features.Import;

/// <summary>
/// Safety settings for the import. The removed-fraction guard rejects an upload that would soft-remove
/// more than <see cref="MaxRemovedFractionWithoutOverride"/> of the active rows, unless the admin sets
/// the override flag. Without it, an export that was only half written would look like a mass removal
/// (spec 0001, Q4). The default is 20%.
/// </summary>
internal sealed class ImportSettings
{
    public const string ConfigurationSection = "Import";

    public double MaxRemovedFractionWithoutOverride { get; init; } = 0.20;

    /// <summary>
    /// The largest upload the import endpoint accepts, in bytes, applied to both the request body and
    /// the multipart form length. It defaults to <see cref="ImportUploadLimits.MaxUploadBytes"/> and can
    /// be configured, so ops can raise it as the export grows without a code change. It is read at
    /// startup, so a restart is enough and no redeploy is needed (spec 0003, #208).
    /// </summary>
    public long MaxUploadBytes { get; init; } = ImportUploadLimits.MaxUploadBytes;

    /// <summary>
    /// How many rows the import changes at a time: load by id, change, save, clear the tracker. That
    /// keeps the transaction's memory use the same however many rows changed (spec 0006).
    /// The default is inside the 2,000 to 5,000 range the spec gives. It is configurable mostly so tests
    /// can force a chunk boundary cheaply.
    /// </summary>
    public int ApplyChunkSize { get; init; } = 5_000;
}
