using FluentValidation;

namespace LotroKoniecDev.AuthSystem.Persistence.Settings;

/// <summary>
/// Stops the boot when the auth database connection string is missing or unreadable, in every
/// environment (ADR-0008 §3). The messages name the key and its environment variable, and never carry
/// Npgsql's own error (<see cref="ConnectionStringReader"/>).
/// </summary>
internal sealed class ConnectionStringSettingsValidator : AbstractValidator<ConnectionStringSettings>
{
    public ConnectionStringSettingsValidator()
    {
        RuleFor(x => x.AuthDatabase)
            .NotEmpty()
            .WithMessage(
                $"{ConnectionStringSettings.AuthDatabaseKey} is required. Inject it via the "
                + $"{ConnectionStringSettings.ConfigurationSection}__{nameof(ConnectionStringSettings.AuthDatabase)} "
                + "environment variable.");

        RuleFor(x => x.AuthDatabase)
            .Must(ConnectionStringReader.IsReadable)
            .When(x => !string.IsNullOrWhiteSpace(x.AuthDatabase))
            .WithMessage(
                $"{ConnectionStringSettings.AuthDatabaseKey} is not a valid connection string. Check the "
                + $"{ConnectionStringSettings.ConfigurationSection}__{nameof(ConnectionStringSettings.AuthDatabase)} "
                + "environment variable: it takes Key=Value pairs separated by ';', and a value that contains ';' "
                + "goes in double quotes.");
    }
}
