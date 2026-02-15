using LotroKoniecDev.Application.Features.Export;
using LotroKoniecDev.Domain.Core.Monads;

namespace LotroKoniecDev.Application.Abstractions;

/// <summary>
/// Defines the contract for exporting texts from DAT files.
/// </summary>
public interface IExporter
{
    /// <summary>
    /// Exports all text fragments from a DAT file to an output file.
    /// </summary>
    /// <param name="datFilePath">Path to the source DAT file.</param>
    /// <param name="outputPath">Path where the exported texts will be saved.</param>
    /// <param name="progress">Optional progress callback (processedFiles, totalFiles).</param>
    /// <returns>Result containing the export summary or an error.</returns>
    Result<ExportSummaryResponse> ExportAllTexts(
        string datFilePath,
        string outputPath,
        Action<int, int>? progress = null);
}
