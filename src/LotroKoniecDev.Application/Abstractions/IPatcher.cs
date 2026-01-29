using LotroKoniecDev.Domain.Core.Monads;

namespace LotroKoniecDev.Application.Abstractions;

/// <summary>
/// Defines the contract for applying translations to DAT files.
/// </summary>
public interface IPatcher
{
    /// <summary>
    /// Applies translations from a translation file to a DAT file.
    /// </summary>
    /// <param name="translationsPath">Path to the translations file.</param>
    /// <param name="datFilePath">Path to the DAT file to patch.</param>
    /// <param name="progress">Optional progress callback (appliedCount, totalCount).</param>
    /// <returns>Result containing the patch summary or an error.</returns>
    Result<PatchSummary> ApplyTranslations(
        string translationsPath,
        string datFilePath,
        Action<int, int>? progress = null);
}

/// <summary>
/// Contains summary information about a patch operation.
/// </summary>
public sealed record PatchSummary(
    int TotalTranslations,
    int AppliedTranslations,
    int SkippedTranslations,
    List<string> Warnings);
