using Microsoft.Extensions.Options;
using LotroKoniecDev.AuthSystem.API.Common;
using LotroKoniecDev.AuthSystem.API.Extensions;

namespace LotroKoniecDev.AuthSystem.API.Settings;

/// <summary>
/// Stops the boot in Staging and Production when a key, the client secret, the issuer or a web client
/// address is missing or malformed (ADR-0008 §3). Development and Testing use throwaway keys and local
/// addresses, so only the access token lifetime is checked there. A message names the key but never
/// repeats the address: it can hold a user name and password, and it goes to the startup log (#1131).
/// </summary>
internal sealed class OpenIddictSettingsValidator : IValidateOptions<OpenIddictSettings>
{
    private const int MinimumApiClientSecretLength = 32;

    /// <summary>
    /// Many clients renew a token a minute before it runs out, so a token that lives a minute or less is
    /// renewed on every page (#1025). The website copes with that, but the server should not depend on it.
    /// </summary>
    private const int MinimumAccessTokenLifetimeMinutes = 2;

    private readonly IWebHostEnvironment _environment;

    public OpenIddictSettingsValidator(IWebHostEnvironment environment)
    {
        _environment = environment;
    }

    public ValidateOptionsResult Validate(string? name, OpenIddictSettings options)
    {
        ArgumentNullException.ThrowIfNull(options);

        List<string> errors = [];

        if (options.AccessTokenLifetimeMinutes < MinimumAccessTokenLifetimeMinutes)
        {
            errors.Add(
                $"{OpenIddictSettings.ConfigurationSection}:{nameof(OpenIddictSettings.AccessTokenLifetimeMinutes)} "
                + $"is {options.AccessTokenLifetimeMinutes} in {_environment.EnvironmentName}, and it must be at least "
                + $"{MinimumAccessTokenLifetimeMinutes}. A client that renews a token a minute before it runs out "
                + "would renew a shorter token on every page.");
        }

        if (_environment.IsDevelopment() || _environment.IsTesting())
        {
            return Result(errors);
        }

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
                + "variable (e.g. https://auth.lotro-translator.pl).");
        }
        else if (!BeAbsoluteHttpUrl(options.Issuer))
        {
            errors.Add(
                $"{OpenIddictSettings.ConfigurationSection}:{nameof(OpenIddictSettings.Issuer)} must be an absolute "
                + $"http(s) URL in {_environment.EnvironmentName} (e.g. https://auth.lotro-translator.pl).");
        }
        else if (CarriesUserInfo(options.Issuer))
        {
            errors.Add(UserInfoError($"{OpenIddictSettings.ConfigurationSection}:{nameof(OpenIddictSettings.Issuer)}"));
        }
        else if (options.Issuer.Contains("localhost", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add(
                $"{OpenIddictSettings.ConfigurationSection}:{nameof(OpenIddictSettings.Issuer)} cannot contain "
                + $"'localhost' in {_environment.EnvironmentName}.");
        }

        if (options.WebClient.RedirectUris.Length == 0)
        {
            errors.Add(
                $"{OpenIddictSettings.ConfigurationSection}:{nameof(OpenIddictSettings.WebClient)}:"
                + $"{nameof(WebClientSettings.RedirectUris)} must contain at least one absolute http(s) URL in "
                + $"{_environment.EnvironmentName}. Inject the web client OAuth callback URL(s) via the "
                + $"{OpenIddictSettings.ConfigurationSection}__{nameof(OpenIddictSettings.WebClient)}__"
                + $"{nameof(WebClientSettings.RedirectUris)}__0 environment variable "
                + "(e.g. https://lotro-translator.pl/callback).");
        }

        for (int index = 0; index < options.WebClient.RedirectUris.Length; index++)
        {
            string redirectUri = options.WebClient.RedirectUris[index];
            string key = $"{OpenIddictSettings.ConfigurationSection}:{nameof(OpenIddictSettings.WebClient)}:"
                         + $"{nameof(WebClientSettings.RedirectUris)}:{index}";

            if (!BeAbsoluteHttpUrl(redirectUri))
            {
                errors.Add(NotAbsoluteHttpUrlError(key));
            }
            else if (CarriesUserInfo(redirectUri))
            {
                errors.Add(UserInfoError(key));
            }
        }

        if (options.WebClient.PostLogoutRedirectUris.Length == 0)
        {
            errors.Add(
                $"{OpenIddictSettings.ConfigurationSection}:{nameof(OpenIddictSettings.WebClient)}:"
                + $"{nameof(WebClientSettings.PostLogoutRedirectUris)} must contain at least one absolute http(s) URL in "
                + $"{_environment.EnvironmentName}. Inject the web client post-logout URL(s) via the "
                + $"{OpenIddictSettings.ConfigurationSection}__{nameof(OpenIddictSettings.WebClient)}__"
                + $"{nameof(WebClientSettings.PostLogoutRedirectUris)}__0 environment variable "
                + "(e.g. https://lotro-translator.pl).");
        }

        for (int index = 0; index < options.WebClient.PostLogoutRedirectUris.Length; index++)
        {
            string postLogoutRedirectUri = options.WebClient.PostLogoutRedirectUris[index];
            string key = $"{OpenIddictSettings.ConfigurationSection}:{nameof(OpenIddictSettings.WebClient)}:"
                         + $"{nameof(WebClientSettings.PostLogoutRedirectUris)}:{index}";

            if (!BeAbsoluteHttpUrl(postLogoutRedirectUri))
            {
                errors.Add(NotAbsoluteHttpUrlError(key));
            }
            else if (CarriesUserInfo(postLogoutRedirectUri))
            {
                errors.Add(UserInfoError(key));
            }
        }

        return Result(errors);
    }

    private static ValidateOptionsResult Result(List<string> errors)
    {
        return errors.Count > 0
            ? ValidateOptionsResult.Fail(errors)
            : ValidateOptionsResult.Success;
    }

    private static bool BeAbsoluteHttpUrl(string value)
    {
        return AbsoluteHttpUri.TryParse(value, out _);
    }

    /// <summary>
    /// Every one of these addresses is public: in the discovery document, in every token, in the browser
    /// or on every page and e-mail (<see cref="FrontendUrl"/>). So a user name or password here can only be
    /// a mistake, and everyone would see it (#1108).
    /// </summary>
    private static bool CarriesUserInfo(string value)
    {
        return AbsoluteHttpUri.TryParse(value, out Uri? uri) && !string.IsNullOrEmpty(uri.UserInfo);
    }

    private string NotAbsoluteHttpUrlError(string key)
    {
        return $"{key} must be an absolute http(s) URL in {_environment.EnvironmentName}.";
    }

    private string UserInfoError(string key)
    {
        return $"{key} must not carry a user name or password in {_environment.EnvironmentName}. Visitors can "
               + "see this address, so they would see the user name and password too. Remove the "
               + "'user:password@' part.";
    }
}
