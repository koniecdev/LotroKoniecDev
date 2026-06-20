using FluentValidation;

namespace LotroKoniecDev.TranslationSystem.Persistence.Settings;

/// <summary>
/// Fail-fast startup validation of the TMS database connection string (ADR-0008 §3, M6-05). It is
/// required in every environment — the dev compose stack needs it too — so the rule is unconditional;
/// the message names the full configuration key and its environment-variable form so a missing value
/// aborts boot with an actionable error rather than a request-time 500.
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
