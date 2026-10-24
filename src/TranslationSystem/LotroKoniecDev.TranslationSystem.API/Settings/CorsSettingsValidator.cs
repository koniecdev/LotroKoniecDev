using Microsoft.Extensions.Options;
using LotroKoniecDev.TranslationSystem.API.Extensions;

namespace LotroKoniecDev.TranslationSystem.API.Settings;

/// <summary>
/// Development uses the open policy and Testing runs on one origin, so both skip this check (ADR-0008
/// §3). A message names the entry number but never the origin: a refused origin can hold a user name and
/// password, and the message goes to the startup log (#1131).
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

        for (int index = 0; index < options.AllowedOrigins.Length; index++)
        {
            if (!BeBareHttpOrigin(options.AllowedOrigins[index]))
            {
                errors.Add(
                    $"{CorsSettings.ConfigurationSection}:{nameof(CorsSettings.AllowedOrigins)}:{index} must be " +
                    "a bare absolute http(s) origin — lowercase scheme and host, no default port, and no userinfo, " +
                    "path, query, or trailing slash (e.g. https://lotro-translator.pl).");
            }
        }

        return errors.Count > 0
            ? ValidateOptionsResult.Fail(errors)
            : ValidateOptionsResult.Success;
    }

    /// <summary>
    /// The browser's <c>Origin</c> header has no user info, path or trailing slash and is lower case, so
    /// <c>"https://x/"</c> or <c>"https://u:p@x"</c> would silently never match. Requiring the value to
    /// equal its own authority part catches that at boot.
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
