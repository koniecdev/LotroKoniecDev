using LotroKoniecDev.Application.Abstractions;

namespace LotroKoniecDev.Application.Features.PreflightChecking;

public sealed record PreflightReportResponse(
    bool IsGameRunning,
    bool HasWriteAccess,
    Result<GameUpdateCheckSummary>? GameUpdateCheckResult);
