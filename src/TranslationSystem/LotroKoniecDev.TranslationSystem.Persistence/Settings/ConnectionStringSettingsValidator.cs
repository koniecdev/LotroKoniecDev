using FluentValidation;

namespace LotroKoniecDev.TranslationSystem.Persistence.Settings;

internal sealed class ConnectionStringSettingsValidator : AbstractValidator<ConnectionStringSettings>
{
    public ConnectionStringSettingsValidator()
    {
        RuleFor(x => x.TranslationDatabase)
            .NotEmpty()
            .WithMessage("TranslationDatabase connection string is required.");
    }
}
