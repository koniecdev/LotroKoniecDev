using LotroKoniecDev.Domain.Core.Monads;
using Mediator;

namespace LotroKoniecDev.Application.Features.Export;

public sealed record ExportTextsQuery(
    string DatFilePath,
    string OutputPath,
    IProgress<OperationProgress>? Progress = null) : IQuery<Result<ExportSummary>>;
