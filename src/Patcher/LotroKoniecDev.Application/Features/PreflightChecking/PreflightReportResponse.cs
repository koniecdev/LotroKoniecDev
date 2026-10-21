using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Domain.Core.Errors;

namespace LotroKoniecDev.Application.Features.PreflightChecking;

public sealed record PreflightReportResponse(
    bool IsGameRunning,
    bool HasWriteAccess,
    Result<GameUpdateCheckSummary>? GameUpdateCheckResult)
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

        return HasWriteAccess
            ? Result.Success()
            : Result.Failure(DomainErrors.DatFileLocation.NoWriteAccess(
                Path.GetDirectoryName(Path.GetFullPath(datFilePath)) ?? datFilePath));
    }
}
