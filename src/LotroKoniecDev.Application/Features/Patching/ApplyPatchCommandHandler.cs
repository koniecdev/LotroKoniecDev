using LotroKoniecDev.Application.Abstractions;
using Mediator;

namespace LotroKoniecDev.Application.Features.Patching;

internal sealed class ApplyPatchCommandHandler : ICommandHandler<ApplyPatchCommand, Result<PatchSummaryResponse>>
{
    private readonly IPatchingService _patchingService;
    private readonly IProgress<OperationProgress> _progress;

    public ApplyPatchCommandHandler(
        IPatchingService patchingService,
        IProgress<OperationProgress> progress)
    {
        _patchingService = patchingService;
        _progress = progress;
    }

    public ValueTask<Result<PatchSummaryResponse>> Handle(ApplyPatchCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        Result<PatchSummaryResponse> result =
            _patchingService.ApplyTranslations(command.TranslationsPath, command.DatFilePath, _progress);

        return new ValueTask<Result<PatchSummaryResponse>>(result);
    }
}
