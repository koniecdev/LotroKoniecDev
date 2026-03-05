using Mediator;

namespace LotroKoniecDev.Application.Features.PreflightChecking;

public sealed record PreflightCheckQuery(
    string DatFilePath,
    string VersionFilePath) : IQuery<Result<PreflightReportResponse>>;
