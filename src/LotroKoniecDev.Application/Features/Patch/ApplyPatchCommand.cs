using LotroKoniecDev.Domain.Core.Monads;
using Mediator;

namespace LotroKoniecDev.Application.Features.Patch;

public sealed record ApplyPatchCommand(
    string TranslationsPath,
    string DatFilePath,
    string VersionFilePath,
    IProgress<OperationProgress>? Progress = null) : ICommand<Result<PatchSummaryResponse>>;
