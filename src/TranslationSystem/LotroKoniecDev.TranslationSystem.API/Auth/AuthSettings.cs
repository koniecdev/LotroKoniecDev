namespace LotroKoniecDev.TranslationSystem.API.Auth;

internal sealed class AuthSettings
{
    public const string ConfigurationSection = "Auth";

    public required string Issuer { get; init; }
    public required string Audience { get; init; }

    /// <summary>
    /// The URL where the OIDC metadata lives, such as the signing keys and the endpoints. When it is
    /// not set, <see cref="Issuer"/> is used. It helps in Docker, where the metadata endpoint sits on
    /// an internal address while the issuer has to match the URL the browser sees in the tokens.
    /// </summary>
    public string? Authority { get; init; }

    public string EffectiveAuthority => Authority ?? Issuer;
}
