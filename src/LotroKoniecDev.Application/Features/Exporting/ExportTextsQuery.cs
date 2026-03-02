using Mediator;

namespace LotroKoniecDev.Application.Features.Exporting;

public sealed record ExportTextsQuery(
    string DatFilePath,
    string OutputPath) : IQuery<Result<ExportSummaryResponse>>;
