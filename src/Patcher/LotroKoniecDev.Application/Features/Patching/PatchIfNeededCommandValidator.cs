using FluentValidation;

namespace LotroKoniecDev.Application.Features.Patching;

public sealed class PatchIfNeededCommandValidator : AbstractValidator<PatchIfNeededCommand>
{
    public PatchIfNeededCommandValidator()
    {
        RuleFor(x => x.DatFilePath).NotEmpty();
        RuleFor(x => x.GameVersionFilePath).NotEmpty();
        RuleFor(x => x.TranslationFilePath).NotEmpty();
    }
}
