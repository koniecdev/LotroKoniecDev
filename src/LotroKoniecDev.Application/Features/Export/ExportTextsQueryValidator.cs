using FluentValidation;

namespace LotroKoniecDev.Application.Features.Export;

public sealed class ExportTextsQueryValidator : AbstractValidator<ExportTextsQuery>
{
    public ExportTextsQueryValidator()
    {
        RuleFor(x => x.DatFilePath).NotEmpty();
        RuleFor(x => x.OutputPath).NotEmpty();
    }
}
