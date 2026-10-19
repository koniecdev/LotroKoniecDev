using FluentValidation;

namespace LotroKoniecDev.TranslationSystem.Persistence.Settings;

/// <summary>
/// Checks the TMS database connection string at startup and stops the app when it is missing or
/// cannot be read (ADR-0008 §3, M6-05). Every environment needs it, the dev compose stack included, so
/// there is no exception. The messages name the full configuration key and its environment-variable
/// form, so a bad value fails the boot with something you can act on instead of a 500 later. They never
/// carry Npgsql's own error (<see cref="ConnectionStringReader"/>, #1157).
/// </summary>
internal sealed class ConnectionStringSettingsValidator : AbstractValidator<ConnectionStringSettings>
{
    public ConnectionStringSettingsValidator()
    {
        RuleFor(x => x.TranslationDatabase)
            .NotEmpty()
            .WithMessage(
                $"{ConnectionStringSettings.TranslationDatabaseKey} is required. Inject it via the "
                + $"{ConnectionStringSettings.ConfigurationSection}__{nameof(ConnectionStringSettings.TranslationDatabase)} "
                + "environment variable.");

        RuleFor(x => x.TranslationDatabase)
            .Must(ConnectionStringReader.IsReadable)
            .When(x => !string.IsNullOrWhiteSpace(x.TranslationDatabase))
            .WithMessage(
                $"{ConnectionStringSettings.TranslationDatabaseKey} is not a valid connection string. Check the "
                + $"{ConnectionStringSettings.ConfigurationSection}__{nameof(ConnectionStringSettings.TranslationDatabase)} "
                + "environment variable: it takes Key=Value pairs separated by ';', and a value that contains ';' "
                + "goes in double quotes.");
    }
}
