using FluentValidation;

namespace LotroKoniecDev.Application.Features.PreflightCheck;

public sealed class PreflightCheckQueryValidator : AbstractValidator<PreflightCheckQuery>
{
    public PreflightCheckQueryValidator()
    {
        RuleFor(x => x.DatFilePath).NotEmpty();
        RuleFor(x => x.VersionFilePath).NotEmpty();
    }
}
