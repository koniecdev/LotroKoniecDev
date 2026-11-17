using FluentValidation;

namespace LotroKoniecDev.Application.Features.TranslationFileSyncing;

public sealed class SyncTranslationFileCommandValidator : AbstractValidator<SyncTranslationFileCommand>
{
    public SyncTranslationFileCommandValidator()
    {
        RuleFor(command => command.TmsBaseUrl)
            .NotEmpty()
            .Must(TranslationServerAddress.IsAllowed)
            .WithMessage("The TMS base URL must be a valid absolute https URL (plain http is allowed only for localhost).");

        RuleFor(command => command.TranslationFilePath)
            .NotEmpty();
    }
}
