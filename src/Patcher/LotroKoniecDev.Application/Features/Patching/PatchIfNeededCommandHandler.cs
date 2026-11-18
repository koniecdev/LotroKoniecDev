using FluentValidation;
using FluentValidation.Results;
using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Application.Abstractions.Messaging;
using LotroKoniecDev.Application.Extensions;

namespace LotroKoniecDev.Application.Features.Patching;

internal sealed class PatchIfNeededCommandHandler : ICommandHandler<PatchIfNeededCommand, Result<PatchIfNeededResponse>>
{
    private readonly IPatchIfNeededService _patchIfNeededService;
    private readonly IProgress<OperationProgress> _progress;
    private readonly IValidator<PatchIfNeededCommand> _validator;

    public ValueTask<Result<PatchIfNeededResponse>> Handle(PatchIfNeededCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        ValidationResult validationResult = _validator.Validate(command);
        if (!validationResult.IsValid)
        {
            return new ValueTask<Result<PatchIfNeededResponse>>(
                Result.Failure<PatchIfNeededResponse>(validationResult.ToValidationError(nameof(PatchIfNeededCommand))));
        }

        Result<PatchIfNeededResponse> result = _patchIfNeededService.PatchIfNeeded(
            command.DatFilePath, command.GameVersionFilePath, command.TranslationFilePath, _progress);

        return new ValueTask<Result<PatchIfNeededResponse>>(result);
    }

    public PatchIfNeededCommandHandler(
        IPatchIfNeededService patchIfNeededService,
        IProgress<OperationProgress> progress,
        IValidator<PatchIfNeededCommand> validator)
    {
        _patchIfNeededService = patchIfNeededService;
        _progress = progress;
        _validator = validator;
    }
}
