using Microsoft.Extensions.Options;
using LotroKoniecDev.AuthSystem.API.Extensions;

namespace LotroKoniecDev.AuthSystem.API.Settings;

/// <summary>
/// Fails fast at startup when the OpenIddict server is misconfigured in a deployed environment
/// (Staging/Production), naming the offending key and the environment (ADR-0008 §3, M6-05).
/// Development and Testing mint ephemeral signing/encryption keys (see <c>OpenIddictExtensions</c>),
/// so the production key material is intentionally absent there and validation is skipped — mirroring
/// <see cref="CorsSettingsValidator"/> and the Data Protection keyring guard.
/// </summary>
internal sealed class OpenIddictSettingsValidator : IValidateOptions<OpenIddictSettings>
{
    private readonly IWebHostEnvironment _environment;

    public OpenIddictSettingsValidator(IWebHostEnvironment environment)
    {
        _environment = environment;
    }

    public ValidateOptionsResult Validate(string? name, OpenIddictSettings options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (_environment.IsDevelopment() || _environment.IsTesting())
        {
            return ValidateOptionsResult.Success;
        }

        List<string> errors = [];

        if (string.IsNullOrWhiteSpace(options.EncryptionKey.Key))
        {
            errors.Add(
                $"{OpenIddictSettings.ConfigurationSection}:{nameof(OpenIddictSettings.EncryptionKey)}:"
                + $"{nameof(EncryptionKeySettings.Key)} must be set in {_environment.EnvironmentName}. Generate a "
                + "256-bit (32-byte) key and base64-encode it, then inject it via the "
                + $"{OpenIddictSettings.ConfigurationSection}__{nameof(OpenIddictSettings.EncryptionKey)}__"
                + $"{nameof(EncryptionKeySettings.Key)} environment variable.");
        }

        if (string.IsNullOrWhiteSpace(options.SigningKey.RsaPrivateKeyXml))
        {
            errors.Add(
                $"{OpenIddictSettings.ConfigurationSection}:{nameof(OpenIddictSettings.SigningKey)}:"
                + $"{nameof(SigningKeySettings.RsaPrivateKeyXml)} must be set in {_environment.EnvironmentName}. "
                + "Generate an RSA key pair and base64-encode its XML, then inject it via the "
                + $"{OpenIddictSettings.ConfigurationSection}__{nameof(OpenIddictSettings.SigningKey)}__"
                + $"{nameof(SigningKeySettings.RsaPrivateKeyXml)} environment variable.");
        }

        if (string.IsNullOrWhiteSpace(options.ApiClientSecret))
        {
            errors.Add(
                $"{OpenIddictSettings.ConfigurationSection}:{nameof(OpenIddictSettings.ApiClientSecret)} must be set "
                + $"in {_environment.EnvironmentName}. Use a strong, randomly generated secret of at least "
                + $"{MinimumApiClientSecretLength} characters.");
        }
        else if (options.ApiClientSecret.Length < MinimumApiClientSecretLength)
        {
            errors.Add(
                $"{OpenIddictSettings.ConfigurationSection}:{nameof(OpenIddictSettings.ApiClientSecret)} must be at "
                + $"least {MinimumApiClientSecretLength} characters in {_environment.EnvironmentName} for security.");
        }

        if (string.IsNullOrWhiteSpace(options.Issuer))
        {
            errors.Add(
                $"{OpenIddictSettings.ConfigurationSection}:{nameof(OpenIddictSettings.Issuer)} must be set in "
                + $"{_environment.EnvironmentName}. Inject the public token issuer URL via the "
                + $"{OpenIddictSettings.ConfigurationSection}__{nameof(OpenIddictSettings.Issuer)} environment "
                + "variable (e.g. https://auth.lotro.koniec.dev).");
        }
        else if (!BeAbsoluteHttpUrl(options.Issuer))
        {
            errors.Add(
                $"{OpenIddictSettings.ConfigurationSection}:{nameof(OpenIddictSettings.Issuer)} must be an absolute "
                + $"http(s) URL in {_environment.EnvironmentName} (e.g. https://auth.lotro.koniec.dev).");
        }
        else if (options.Issuer.Contains("localhost", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add(
                $"{OpenIddictSettings.ConfigurationSection}:{nameof(OpenIddictSettings.Issuer)} cannot contain "
                + $"'localhost' in {_environment.EnvironmentName}.");
        }

        return errors.Count > 0
            ? ValidateOptionsResult.Fail(errors)
            : ValidateOptionsResult.Success;
    }

    private const int MinimumApiClientSecretLength = 32;

    private static bool BeAbsoluteHttpUrl(string value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
               && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }
}
