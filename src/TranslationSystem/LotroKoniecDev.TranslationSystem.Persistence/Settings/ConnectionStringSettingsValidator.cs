using FluentValidation;

namespace LotroKoniecDev.TranslationSystem.Persistence.Settings;

/// <summary>
/// Checks the TMS database connection string at startup and stops the app when it is missing
/// (ADR-0008 §3, M6-05). Every environment needs it, the dev compose stack included, so there is no
/// exception. The message names the full configuration key and its environment-variable form, so a
/// missing value fails the boot with something you can act on instead of a 500 later.
/// </summary>
internal sealed class ConnectionStringSettingsValidator : AbstractValidator<ConnectionStringSettings>
{
    public ConnectionStringSettingsValidator()
    {
        RuleFor(x => x.TranslationDatabase)
            .NotEmpty()
            .WithMessage(
                $"{ConnectionStringSettings.ConfigurationSection}:{nameof(ConnectionStringSettings.TranslationDatabase)} "
                + "is required. Inject it via the "
                + $"{ConnectionStringSettings.ConfigurationSection}__{nameof(ConnectionStringSettings.TranslationDatabase)} "
                + "environment variable.");
    }
}
