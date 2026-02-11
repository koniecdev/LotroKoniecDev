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
    /// <returns>Result containing the export summary or an error.</returns>
    Result<ExportSummary> ExportAllTexts(
        string datFilePath,
        string outputPath);
}

/// <summary>
/// Contains summary information about an export operation.
/// </summary>
public sealed record ExportSummary(
    int TotalTextFiles,
    int TotalFragments,
    string OutputPath);
