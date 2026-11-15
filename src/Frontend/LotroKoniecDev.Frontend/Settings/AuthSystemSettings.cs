namespace LotroKoniecDev.Frontend.Settings;

/// <summary>
/// <see cref="BaseUrl"/> is where this app calls the auth server: the account API, and the only origin its
/// renewal and revoke may go to. <see cref="Authority"/> is where the OIDC metadata comes from, and the
/// issuer in the tokens. The token, revoke and end-session addresses come from that metadata (#1114),
/// so the validator requires one origin for both.
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
    /// Sent next to the visitor's address, so the auth API meters each call on that visitor instead of on
    /// this container (ADR-0054). Compose feeds the auth API the same <c>FRONTEND_CALLER_KEY</c>.
    /// </summary>
    public string? CallerKey { get; init; }
}
