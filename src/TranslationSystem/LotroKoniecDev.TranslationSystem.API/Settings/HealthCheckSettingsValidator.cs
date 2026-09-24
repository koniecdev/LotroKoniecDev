using Microsoft.Extensions.Options;
using LotroKoniecDev.TranslationSystem.API.Extensions;

namespace LotroKoniecDev.TranslationSystem.API.Settings;

/// <summary>
/// Stops the boot when the health check key is missing in a deployed environment, or too short anywhere
/// it is set (ADR-0058, #853). Without a key the full /health is open to every caller. That is fine in
/// Development and Testing and nowhere else: an open /health lets anyone run the database check as often
/// as they like.
/// </summary>
internal sealed class HealthCheckSettingsValidator : IValidateOptions<HealthCheckSettings>
{
    public const int MinimumKeyLength = 32;

    private readonly IWebHostEnvironment _environment;

    public HealthCheckSettingsValidator(IWebHostEnvironment environment)
    {
        _environment = environment;
    }

    public ValidateOptionsResult Validate(string? name, HealthCheckSettings options)
    {
        ArgumentNullException.ThrowIfNull(options);

        string? key = options.Key;

        if (string.IsNullOrWhiteSpace(key))
        {
            if (_environment.IsDevelopment() || _environment.IsEnvironment(EnvironmentsExtensions.TestingName))
            {
                return ValidateOptionsResult.Success;
            }

            return ValidateOptionsResult.Fail(
                $"{HealthCheckSettings.ConfigurationSection}:{nameof(HealthCheckSettings.Key)} must be set in "
                + $"{_environment.EnvironmentName}. Inject it via the "
                + $"{HealthCheckSettings.ConfigurationSection}__{nameof(HealthCheckSettings.Key)} environment "
                + "variable (HEALTH_CHECK_KEY in the box .env, openssl rand -base64 32, ADR-0058).");
        }

        if (key.Length < MinimumKeyLength)
        {
            return ValidateOptionsResult.Fail(
                $"{HealthCheckSettings.ConfigurationSection}:{nameof(HealthCheckSettings.Key)} must be at least "
                + $"{MinimumKeyLength} characters (openssl rand -base64 32).");
        }

        return ValidateOptionsResult.Success;
    }
}
