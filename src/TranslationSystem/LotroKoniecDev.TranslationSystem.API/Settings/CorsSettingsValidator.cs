using Microsoft.Extensions.Options;
using LotroKoniecDev.TranslationSystem.API.Extensions;

namespace LotroKoniecDev.TranslationSystem.API.Settings;

/// <summary>
/// Stops the boot when the configured CORS origins are missing or malformed in a deployed environment,
/// Staging or Production, and names the key at fault (ADR-0008 §3, M6-03). Development uses the open
/// AllowAnyOrigin policy and Testing runs in memory on one origin, so neither supplies origins and
/// both skip this check, like <c>OpenIddictSettingsValidator</c> does.
/// </summary>
internal sealed class CorsSettingsValidator : IValidateOptions<CorsSettings>
{
    private readonly IWebHostEnvironment _environment;

    public CorsSettingsValidator(IWebHostEnvironment environment)
    {
        _environment = environment;
    }

    public ValidateOptionsResult Validate(string? name, CorsSettings options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (_environment.IsDevelopment() || _environment.IsEnvironment(EnvironmentsExtensions.TestingName))
        {
            return ValidateOptionsResult.Success;
        }

        List<string> errors = [];

        if (options.AllowedOrigins.Length == 0)
        {
            errors.Add(
                $"{CorsSettings.ConfigurationSection}:{nameof(CorsSettings.AllowedOrigins)} must contain at least one " +
                $"origin in {_environment.EnvironmentName}. Inject it via the " +
                $"{CorsSettings.ConfigurationSection}__{nameof(CorsSettings.AllowedOrigins)}__0 environment variable " +
                "(e.g. https://lotro-translator.pl).");
        }

        foreach (string origin in options.AllowedOrigins)
        {
            if (!BeBareHttpOrigin(origin))
            {
                errors.Add(
                    $"{CorsSettings.ConfigurationSection}:{nameof(CorsSettings.AllowedOrigins)} entry '{origin}' must be " +
                    "a bare absolute http(s) origin — lowercase scheme and host, no default port, and no userinfo, " +
                    "path, query, or trailing slash (e.g. https://lotro-translator.pl).");
            }
        }

        return errors.Count > 0
            ? ValidateOptionsResult.Fail(errors)
            : ValidateOptionsResult.Success;
    }

    /// <summary>
    /// A CORS origin is only the scheme, the host and a non-default port. The browser's <c>Origin</c>
    /// header carries no user info, path, query or trailing slash, and it is always lower case. So
    /// <c>WithOrigins("https://x/")</c> or <c>"https://u:p@x"</c> would never match anything, without
    /// saying so. Requiring the value to equal its own authority part, with no user info, catches those
    /// mistakes at boot.
    /// </summary>
    private static bool BeBareHttpOrigin(string? value)
    {
        return value is not null
               && Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
               && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
               && string.IsNullOrEmpty(uri.UserInfo)
               && string.Equals(value, uri.GetLeftPart(UriPartial.Authority), StringComparison.Ordinal);
    }
}
