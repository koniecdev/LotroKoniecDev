using FluentValidation;

namespace LotroKoniecDev.Application.Features.Patch;

public sealed class ApplyPatchCommandValidator : AbstractValidator<ApplyPatchCommand>
{
    public ApplyPatchCommandValidator()
    {
        RuleFor(x => x.TranslationsPath).NotEmpty();
        RuleFor(x => x.DatFilePath).NotEmpty();
    }
}
