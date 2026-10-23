using Microsoft.Extensions.Options;
using LotroKoniecDev.AuthSystem.API.Common;
using LotroKoniecDev.AuthSystem.API.Extensions;

namespace LotroKoniecDev.AuthSystem.API.Settings;

/// <summary>
/// Stops the boot when the OpenIddict server is misconfigured in a deployed environment, Staging or
/// Production, and names the key and the environment at fault (ADR-0008 §3, M6-05). It checks the
/// signing and encryption keys, the API client secret, the issuer and, since the base
/// <c>appsettings.json</c> no longer holds them (M6-06), the web client redirect URIs the seeder
/// registers.
/// Development and Testing create throwaway signing and encryption keys (see
/// <c>OpenIddictExtensions</c>) and take a fixed client secret and localhost addresses from their own
/// configuration, so those checks are skipped there, like in <see cref="CorsSettingsValidator"/> and the
/// Data Protection keyring check.
/// The access token lifetime is checked in every environment, because a lifetime that is too short
/// breaks the sign-in the same way everywhere (#1025).
/// A message about an address names the key, and for a list the entry number, but never repeats the
/// address. A refused address can hold a user name and password, and the message goes to the startup
/// log (#1131).
/// </summary>
internal sealed class OpenIddictSettingsValidator : IValidateOptions<OpenIddictSettings>
{
    private const int MinimumApiClientSecretLength = 32;

    /// <summary>
    /// Many clients renew a token a minute before it runs out, and the website does so for every token
    /// that lives two minutes or more. A token that lives a minute or less looks "about to run out" as soon
    /// as it arrives, so such a client renews it on every page (#1025). The website copes with a one-minute
    /// token, which it refreshes after 30 seconds, but the server should not depend on that (ADR-0049).
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
    /// Nothing on the sign-in server needs a user name or password in these addresses. Each one is public.
    /// The issuer is in the discovery document and in every token. A redirect URI is where the browser
    /// goes after sign-in. The first post-logout URI is printed on every account page and in every e-mail
    /// (<see cref="FrontendUrl"/>). So a user name or password here can only be a mistake, and everyone
    /// would see it (#1108, ADR-0067 decision 4).
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
