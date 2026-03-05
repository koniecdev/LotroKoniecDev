using FluentValidation;

namespace LotroKoniecDev.Application.Features.Patching;

public sealed class ApplyPatchCommandValidator : AbstractValidator<ApplyPatchCommand>
{
    public ApplyPatchCommandValidator()
    {
        RuleFor(x => x.TranslationsPath).NotEmpty();
        RuleFor(x => x.DatFilePath).NotEmpty();
    }
}
