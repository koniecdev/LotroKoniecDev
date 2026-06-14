namespace LotroKoniecDev.Frontend.Settings;

/// <summary>
/// Binds the OpenID Connect relying-party configuration the Frontend uses to authenticate
/// translators against the self-hosted AuthSystem (OpenIddict). <see cref="BaseUrl"/> is the
/// token endpoint host used by <c>CookieTokenRefresher</c>; <see cref="Authority"/> is the OIDC
/// metadata/discovery host stamped into tokens.
/// </summary>
internal sealed class AuthSystemSettings
{
    public const string ConfigurationSection = "AuthSystem";

    public required string BaseUrl { get; init; }

    public required string Authority { get; init; }

    public required string ClientId { get; init; }

    public required string CallbackPath { get; init; }

    public required string SignedOutCallbackPath { get; init; }

    public required IReadOnlyList<string> Scopes { get; init; }
}
