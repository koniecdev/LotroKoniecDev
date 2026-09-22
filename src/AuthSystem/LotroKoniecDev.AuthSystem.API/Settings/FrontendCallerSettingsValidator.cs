using Microsoft.Extensions.Options;
using LotroKoniecDev.AuthSystem.API.Extensions;

namespace LotroKoniecDev.AuthSystem.API.Settings;

/// <summary>
/// Stops the boot when the frontend caller key is missing in a deployed environment, or too short
/// anywhere it is set (ADR-0054 §6). Development and Testing run without the limiter, so there is
/// nothing for the key to decide and they may leave it empty, the way <see cref="CorsSettingsValidator"/>
/// skips its check there. Anywhere else a missing key would quietly put every visitor's frontend
/// calls back into the frontend container's one bucket, and nothing but real traffic would notice.
/// </summary>
internal sealed class FrontendCallerSettingsValidator : IValidateOptions<FrontendCallerSettings>
{
    public const int MinimumKeyLength = 32;

    private readonly IWebHostEnvironment _environment;

    public FrontendCallerSettingsValidator(IWebHostEnvironment environment)
    {
        _environment = environment;
    }

    public ValidateOptionsResult Validate(string? name, FrontendCallerSettings options)
    {
        ArgumentNullException.ThrowIfNull(options);

        string? key = options.Key;

        if (string.IsNullOrWhiteSpace(key))
        {
            if (_environment.IsDevelopment() || _environment.IsTesting())
            {
                return ValidateOptionsResult.Success;
            }

            return ValidateOptionsResult.Fail(
                $"{FrontendCallerSettings.ConfigurationSection}:{nameof(FrontendCallerSettings.Key)} must be set in "
                + $"{_environment.EnvironmentName}. Inject it via the "
                + $"{FrontendCallerSettings.ConfigurationSection}__{nameof(FrontendCallerSettings.Key)} environment "
                + "variable (FRONTEND_CALLER_KEY in the box .env, openssl rand -base64 32, ADR-0054).");
        }

        if (key.Length < MinimumKeyLength)
        {
            return ValidateOptionsResult.Fail(
                $"{FrontendCallerSettings.ConfigurationSection}:{nameof(FrontendCallerSettings.Key)} must be at least "
                + $"{MinimumKeyLength} characters (openssl rand -base64 32).");
        }

        return ValidateOptionsResult.Success;
    }
}
