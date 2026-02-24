using LotroKoniecDev.Domain.Core.Monads;
using Mediator;

namespace LotroKoniecDev.Application.Features.PreflightCheck;

public sealed record PreflightCheckQuery(
    string DatFilePath,
    string VersionFilePath) : IQuery<Result<PreflightReportResponse>>;
