using Microsoft.Extensions.Options;
using LotroKoniecDev.TranslationSystem.API.Extensions;

namespace LotroKoniecDev.TranslationSystem.API.Settings;

/// <summary>
/// Without a key the full /health is open, which is fine only in Development and Testing: elsewhere
/// anyone could run the database check as often as they like (ADR-0058).
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

        // Kestrel trims the spaces around a header value, so a key with them could never match.
        if (key != key.Trim())
        {
            return ValidateOptionsResult.Fail(
                $"{HealthCheckSettings.ConfigurationSection}:{nameof(HealthCheckSettings.Key)} must not start or "
                + "end with whitespace. Check the quoting of HEALTH_CHECK_KEY in the box .env.");
        }

        // A header cannot carry most of these; the frontend caller key has the same rule (#877).
        if (!key.All(IsPrintableAscii))
        {
            return ValidateOptionsResult.Fail(
                $"{HealthCheckSettings.ConfigurationSection}:{nameof(HealthCheckSettings.Key)} must contain only "
                + "printable ASCII characters: no line break, tab, other control character or non-ASCII character "
                + "such as a non-breaking space. Check HEALTH_CHECK_KEY in the box .env (openssl rand -base64 32).");
        }

        return ValidateOptionsResult.Success;
    }

    private static bool IsPrintableAscii(char character) => character is >= ' ' and <= '~';
}
