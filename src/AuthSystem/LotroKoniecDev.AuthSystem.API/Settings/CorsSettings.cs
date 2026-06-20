namespace LotroKoniecDev.AuthSystem.API.Settings;

/// <summary>
/// Browser origins the production CORS policy admits, injected per environment (ADR-0008 §3).
/// An empty list is valid only in Development/Testing — those use the permissive AllowAnyOrigin
/// policy and never read this list; Staging/Production require at least one bare http(s) origin,
/// enforced at startup by <see cref="CorsSettingsValidator"/>.
/// </summary>
internal sealed class CorsSettings
{
    public const string ConfigurationSection = "Cors";

    public string[] AllowedOrigins { get; init; } = [];
}
