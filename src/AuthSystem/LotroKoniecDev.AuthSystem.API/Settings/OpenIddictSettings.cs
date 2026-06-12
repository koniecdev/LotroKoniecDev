namespace LotroKoniecDev.AuthSystem.API.Settings;

internal sealed class OpenIddictSettings
{
    public const string ConfigurationSection = "OpenIddict";

    public required string Issuer { get; init; }

    /// <summary>
    /// Internal issuer URL for self-referencing calls (e.g., client credentials token acquisition).
    /// If not set, falls back to <see cref="Issuer"/>.
    /// Useful in Docker environments where the container needs to call itself via localhost.
    /// </summary>
    public string? InternalIssuer { get; init; }

    public int AccessTokenLifetimeMinutes { get; init; } = 60;
    public int RefreshTokenLifetimeDays { get; init; } = 14;
    public EncryptionKeySettings EncryptionKey { get; init; } = new();
    public SigningKeySettings SigningKey { get; init; } = new();

    /// <summary>
    /// Client secret for the API service (service-to-service communication).
    /// Must be set in production - use a strong, randomly generated secret.
    /// Validation is handled by OpenIddictSettingsValidator (only enforced in production).
    /// </summary>
    public string ApiClientSecret { get; init; } = string.Empty;

    /// <summary>
    /// Gets the issuer URL to use for internal/self-referencing calls.
    /// </summary>
    public string EffectiveInternalIssuer => InternalIssuer ?? Issuer;

    /// <summary>
    /// Configuration for the web client OAuth application.
    /// </summary>
    public WebClientSettings WebClient { get; init; } = new();
}

internal sealed class WebClientSettings
{
    /// <summary>
    /// Production redirect URIs for the web client.
    /// These are used in non-development environments.
    /// </summary>
    public string[] RedirectUris { get; init; } = [];

    /// <summary>
    /// Production post-logout redirect URIs for the web client.
    /// These are used in non-development environments.
    /// </summary>
    public string[] PostLogoutRedirectUris { get; init; } = [];
}

internal sealed class EncryptionKeySettings
{
    /// <summary>
    /// Symmetric encryption key (256-bit), base64 encoded.
    /// Only required in production - dev/testing use ephemeral keys.
    /// </summary>
    public string Key { get; init; } = string.Empty;
}

internal sealed class SigningKeySettings
{
    /// <summary>
    /// RSA private key in XML format (RSA.ToXmlString(true)), base64 encoded.
    /// Used for signing access tokens. The public key is exposed via JWKS endpoint.
    /// Only required in production - dev/testing use ephemeral keys.
    /// </summary>
    public string RsaPrivateKeyXml { get; init; } = string.Empty;

    /// <summary>
    /// Previous RSA private key for key rotation. When rotating keys:
    /// 1. Move the current RsaPrivateKeyXml value here.
    /// 2. Set a new RSA key in RsaPrivateKeyXml.
    /// 3. Deploy. New tokens are signed with the new key. Existing tokens signed with the previous key remain valid.
    /// 4. After all old tokens expire (AccessTokenLifetimeMinutes), remove this value.
    /// </summary>
    public string? PreviousRsaPrivateKeyXml { get; init; }
}
