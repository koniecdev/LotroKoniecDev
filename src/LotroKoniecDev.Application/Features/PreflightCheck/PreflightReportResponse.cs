using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Domain.Core.Monads;

namespace LotroKoniecDev.Application.Features.PreflightCheck;

public sealed record PreflightReportResponse(
    bool IsGameRunning,
    bool HasWriteAccess,
    Result<GameUpdateCheckSummary>? GameUpdateCheckResult);
