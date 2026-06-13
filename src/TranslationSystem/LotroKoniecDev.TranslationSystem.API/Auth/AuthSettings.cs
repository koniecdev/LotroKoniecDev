namespace LotroKoniecDev.TranslationSystem.API.Auth;

internal sealed class AuthSettings
{
    public const string ConfigurationSection = "Auth";

    public required string Issuer { get; init; }
    public required string Audience { get; init; }

    /// <summary>
    /// Authority URL for OIDC metadata discovery (signing keys, endpoints).
    /// If not set, falls back to <see cref="Issuer"/>.
    /// Useful in Docker environments where the metadata endpoint is on an internal network address
    /// while the Issuer must match the browser-facing URL in tokens.
    /// </summary>
    public string? Authority { get; init; }

    public string EffectiveAuthority => Authority ?? Issuer;
}
