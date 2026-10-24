using FluentValidation;

namespace LotroKoniecDev.TranslationSystem.Persistence.Settings;

/// <summary>
/// Fails the boot in every environment when the connection string is missing or unreadable (ADR-0008
/// §3), instead of a 500 later. The messages never carry Npgsql's own error
/// (<see cref="ConnectionStringReader"/>).
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
