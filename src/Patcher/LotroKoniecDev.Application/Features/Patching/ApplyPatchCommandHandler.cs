using FluentValidation;
using FluentValidation.Results;
using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Application.Abstractions.Messaging;
using LotroKoniecDev.Application.Extensions;

namespace LotroKoniecDev.Application.Features.Patching;

internal sealed class ApplyPatchCommandHandler : ICommandHandler<ApplyPatchCommand, Result<PatchSummaryResponse>>
{
    private readonly IPatchingService _patchingService;
    private readonly IProgress<OperationProgress> _progress;
    private readonly IValidator<ApplyPatchCommand> _validator;

    public ApplyPatchCommandHandler(
        IPatchingService patchingService,
        IProgress<OperationProgress> progress,
        IValidator<ApplyPatchCommand> validator)
    {
        _patchingService = patchingService;
        _progress = progress;
        _validator = validator;
    }

    public ValueTask<Result<PatchSummaryResponse>> Handle(ApplyPatchCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        ValidationResult validationResult = _validator.Validate(command);
        if (!validationResult.IsValid)
        {
            return new ValueTask<Result<PatchSummaryResponse>>(
                Result.Failure<PatchSummaryResponse>(validationResult.ToValidationError(nameof(ApplyPatchCommand))));
        }

        Result<PatchSummaryResponse> result =
            _patchingService.ApplyTranslations(command.TranslationsPath, command.DatFilePath, _progress);

        return new ValueTask<Result<PatchSummaryResponse>>(result);
    }
}
