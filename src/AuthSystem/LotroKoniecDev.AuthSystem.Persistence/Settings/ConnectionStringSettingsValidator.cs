using FluentValidation;

namespace LotroKoniecDev.AuthSystem.Persistence.Settings;

internal sealed class ConnectionStringSettingsValidator : AbstractValidator<ConnectionStringSettings>
{
    public ConnectionStringSettingsValidator()
    {
        RuleFor(x => x.AuthDatabase)
            .NotEmpty()
            .WithMessage("AuthDatabase connection string is required.");
    }
}
