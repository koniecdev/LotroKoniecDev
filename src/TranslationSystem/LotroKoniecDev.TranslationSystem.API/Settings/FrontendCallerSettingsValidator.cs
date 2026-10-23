using Microsoft.Extensions.Options;
using LotroKoniecDev.TranslationSystem.API.Extensions;

namespace LotroKoniecDev.TranslationSystem.API.Settings;

/// <summary>
/// Development and Testing run without the limiter, so they may leave the key empty. Anywhere else a
/// missing key would quietly put every translator back into the frontend container's one bucket, and
/// only real traffic would notice (ADR-0054 §6).
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
            if (_environment.IsDevelopment() || _environment.IsEnvironment(EnvironmentsExtensions.TestingName))
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

        // Kestrel trims the spaces around a header value, so a key with them could never match.
        if (key != key.Trim())
        {
            return ValidateOptionsResult.Fail(
                $"{FrontendCallerSettings.ConfigurationSection}:{nameof(FrontendCallerSettings.Key)} must not start or "
                + "end with whitespace. Check the quoting of FRONTEND_CALLER_KEY in the box .env.");
        }

        // A header cannot carry most of these, and one simple rule covers the rest (#877).
        if (!key.All(IsPrintableAscii))
        {
            return ValidateOptionsResult.Fail(
                $"{FrontendCallerSettings.ConfigurationSection}:{nameof(FrontendCallerSettings.Key)} must contain only "
                + "printable ASCII characters: no line break, tab, other control character or non-ASCII character "
                + "such as a non-breaking space. Check FRONTEND_CALLER_KEY in the box .env (openssl rand -base64 32).");
        }

        return ValidateOptionsResult.Success;
    }

    private static bool IsPrintableAscii(char character) => character is >= ' ' and <= '~';
}
