using Microsoft.Extensions.Options;
using LotroKoniecDev.TranslationSystem.API.Extensions;

namespace LotroKoniecDev.TranslationSystem.API.Settings;

/// <summary>
/// Fails fast at startup when the configured CORS origins are missing or malformed in a deployed
/// environment (Staging/Production), naming the offending key (ADR-0008 §3, M6-03). Development
/// uses the permissive AllowAnyOrigin policy and Testing runs same-origin in-memory, so neither
/// supplies origins — both skip validation, mirroring <c>OpenIddictSettingsValidator</c>.
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
    /// A CORS origin is the scheme+host(+non-default-port) only — the browser's <c>Origin</c> header
    /// carries no userinfo, path, query, or trailing slash and is always lowercase, so e.g.
    /// <c>WithOrigins("https://x/")</c> or <c>"https://u:p@x"</c> would silently never match.
    /// Requiring the value to equal its own (userinfo-free) authority part rejects those
    /// misconfigurations at boot.
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
