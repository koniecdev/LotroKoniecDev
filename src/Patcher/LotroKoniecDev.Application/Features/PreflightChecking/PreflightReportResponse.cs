using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Domain.Core.Errors;

namespace LotroKoniecDev.Application.Features.PreflightChecking;

/// <param name="DatFileWriteCheck">
/// Whether the DAT itself can be written. It keeps its default ("writable") when the game runs or
/// the folder refuses writing, because then the file is never checked.
/// </param>
public sealed record PreflightReportResponse(
    bool IsGameRunning,
    bool HasWriteAccess,
    Result<GameUpdateCheckSummary>? GameUpdateCheckResult,
    DatFileWriteCheck DatFileWriteCheck = default)
{
    /// <summary>
    /// The update check never blocks a patch: a failed or missing check only means no version is
    /// known (ADR-0045 §7).
    /// </summary>
    public Result EnsureCanPatch(string datFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(datFilePath);

        if (IsGameRunning)
        {
            return Result.Failure(DomainErrors.DatFileLocation.GameRunning);
        }

        // The folder must stay writable as well: the .backup is created next to the DAT.
        if (!HasWriteAccess)
        {
            return Result.Failure(DomainErrors.DatFileLocation.NoWriteAccess(
                Path.GetDirectoryName(Path.GetFullPath(datFilePath)) ?? datFilePath));
        }

        return DatFileWriteCheck.ToResult(datFilePath);
    }
}
