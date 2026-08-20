namespace LotroKoniecDev.AuthSystem.API.Settings;

internal sealed class OpenIddictSettings
{
    public const string ConfigurationSection = "OpenIddict";

    public required string Issuer { get; init; }

    /// <summary>
    /// The issuer URL to use when this service calls itself, for example to get a client-credentials
    /// token. When it is not set, <see cref="Issuer"/> is used. It helps in Docker, where the container
    /// has to reach itself over localhost.
    /// </summary>
    public string? InternalIssuer { get; init; }

    public int AccessTokenLifetimeMinutes { get; init; } = 60;
    public int RefreshTokenLifetimeDays { get; init; } = 14;
    public EncryptionKeySettings EncryptionKey { get; init; } = new();
    public SigningKeySettings SigningKey { get; init; } = new();

    /// <summary>
    /// The client secret of the API service, used for calls between services. It must be set in
    /// production, and it should be long and randomly generated. OpenIddictSettingsValidator checks it,
    /// and only in production.
    /// </summary>
    public string ApiClientSecret { get; init; } = string.Empty;

    public string EffectiveInternalIssuer => InternalIssuer ?? Issuer;

    public WebClientSettings WebClient { get; init; } = new();
}

internal sealed class WebClientSettings
{
    /// <summary>The web client's redirect URIs, used everywhere except Development.</summary>
    public string[] RedirectUris { get; init; } = [];

    /// <summary>The web client's post-logout redirect URIs, used everywhere except Development.</summary>
    public string[] PostLogoutRedirectUris { get; init; } = [];
}

internal sealed class EncryptionKeySettings
{
    /// <summary>
    /// The 256-bit symmetric encryption key, base64 encoded. Only production needs it; development and
    /// testing use throwaway keys.
    /// </summary>
    public string Key { get; init; } = string.Empty;
}

internal sealed class SigningKeySettings
{
    /// <summary>
    /// The RSA private key as XML (RSA.ToXmlString(true)), base64 encoded. It signs the access tokens,
    /// and the public half is published at the JWKS endpoint. Only production needs it; development and
    /// testing use throwaway keys.
    /// </summary>
    public string RsaPrivateKeyXml { get; init; } = string.Empty;

    /// <summary>
    /// The previous RSA private key, used while rotating keys. To rotate:
    /// 1. Move the current RsaPrivateKeyXml value here.
    /// 2. Put a new RSA key in RsaPrivateKeyXml.
    /// 3. Deploy. New tokens use the new key, and tokens signed with the old one stay valid.
    /// 4. Once every old token has expired (AccessTokenLifetimeMinutes), remove this value.
    /// </summary>
    public string? PreviousRsaPrivateKeyXml { get; init; }
}
