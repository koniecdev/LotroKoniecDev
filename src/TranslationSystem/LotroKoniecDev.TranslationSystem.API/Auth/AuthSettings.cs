namespace LotroKoniecDev.TranslationSystem.API.Auth;

internal sealed class AuthSettings
{
    public const string ConfigurationSection = "Auth";

    public required string Issuer { get; init; }
    public required string Audience { get; init; }

    /// <summary>
    /// Where the OIDC metadata lives, when it differs from <see cref="Issuer"/>: the issuer must match the
    /// URL in the tokens, while the metadata may sit on an internal address.
    /// </summary>
    public string? Authority { get; init; }

    public string EffectiveAuthority => Authority ?? Issuer;
}
