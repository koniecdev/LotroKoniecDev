using LotroKoniecDev.Application.Features.Patching;

namespace LotroKoniecDev.Application.Abstractions;

public interface IPatchingService
{
    Result<PatchSummaryResponse> ApplyTranslations(
        string translationsPath,
        string datFilePath,
        IProgress<OperationProgress>? progress = null);
}
