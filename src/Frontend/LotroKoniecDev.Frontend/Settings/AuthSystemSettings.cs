namespace LotroKoniecDev.Frontend.Settings;

/// <summary>
/// The OpenID Connect settings the Frontend uses to log translators in against our own AuthSystem
/// (OpenIddict). <see cref="BaseUrl"/> is the host of the token endpoint that
/// <c>CookieTokenRefresher</c> calls. <see cref="Authority"/> is the host the OIDC metadata comes from
/// and the value written into the tokens.
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

    /// <summary>
    /// The environment's shared key, sent next to the visitor's address so the auth API meters each
    /// call on that visitor instead of on this container (ADR-0054). Compose feeds the auth API the same
    /// <c>FRONTEND_CALLER_KEY</c>.
    /// </summary>
    public string? CallerKey { get; init; }
}
