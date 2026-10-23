namespace LotroKoniecDev.AuthSystem.API.Settings;

internal sealed class OpenIddictSettings
{
    public const string ConfigurationSection = "OpenIddict";

    public required string Issuer { get; init; }

    /// <summary>
    /// Also how long a revocation takes to bite: the TMS validates JWTs on its own, with no clock skew, so
    /// a revoked token works until it expires (ADR-0049). The service refuses to start below 2 minutes.
    /// </summary>
    public int AccessTokenLifetimeMinutes { get; init; } = 5;

    /// <summary>
    /// The website's session cookie carries the refresh token and ends after 8 idle hours (Frontend
    /// <c>AuthenticationDependencyInjectionExtensions</c>), so the extra hour keeps the token alive longer
    /// than the cookie (#1014).
    /// </summary>
    public int RefreshTokenLifetimeMinutes { get; init; } = 540;
    public EncryptionKeySettings EncryptionKey { get; init; } = new();
    public SigningKeySettings SigningKey { get; init; } = new();

    /// <summary>
    /// The API service's client secret for calls between services. Required, long and random, everywhere
    /// except Development and Testing.
    /// </summary>
    public string ApiClientSecret { get; init; } = string.Empty;

    public WebClientSettings WebClient { get; init; } = new();
}

internal sealed class WebClientSettings
{
    /// <summary>
    /// At least one is required everywhere except Development and Testing.
    /// </summary>
    public string[] RedirectUris { get; init; } = [];

    /// <summary>
    /// The first one is also the website's root address, which the account pages and the e-mails link to
    /// (<see cref="Common.FrontendUrl"/>), so the order matters. At least one is required everywhere
    /// except Development and Testing.
    /// </summary>
    public string[] PostLogoutRedirectUris { get; init; } = [];
}

internal sealed class EncryptionKeySettings
{
    /// <summary>
    /// A 256-bit key, base64 encoded. Required everywhere except Development and Testing, which use
    /// throwaway keys.
    /// </summary>
    public string Key { get; init; } = string.Empty;
}

internal sealed class SigningKeySettings
{
    /// <summary>
    /// The RSA private key as XML (RSA.ToXmlString(true)), base64 encoded. Required everywhere except
    /// Development and Testing, which use throwaway keys.
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
