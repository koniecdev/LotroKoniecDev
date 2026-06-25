using LotroKoniecDev.Application.Abstractions.Messaging;

namespace LotroKoniecDev.Application.Features.Patching;

public sealed record ApplyPatchCommand(
    string TranslationsPath,
    string DatFilePath) : ICommand<Result<PatchSummaryResponse>>;
