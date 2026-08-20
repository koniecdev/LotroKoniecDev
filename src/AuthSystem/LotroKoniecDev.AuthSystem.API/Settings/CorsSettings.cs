namespace LotroKoniecDev.AuthSystem.API.Settings;

/// <summary>
/// The browser origins the production CORS policy allows. They come from the environment (ADR-0008
/// §3). An empty list is only valid in Development and Testing, which use the open AllowAnyOrigin
/// policy and never read this list. Staging and Production need at least one plain http or https
/// origin, which <see cref="CorsSettingsValidator"/> checks at startup.
/// </summary>
internal sealed class CorsSettings
{
    public const string ConfigurationSection = "Cors";

    public string[] AllowedOrigins { get; init; } = [];
}
