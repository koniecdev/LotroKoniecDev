namespace LotroKoniecDev.AuthSystem.API.Settings;

/// <summary>
/// The origins the production CORS policy allows (ADR-0008 §3). Development and Testing use the open
/// policy and never read this list.
/// </summary>
internal sealed class CorsSettings
{
    public const string ConfigurationSection = "Cors";

    public string[] AllowedOrigins { get; init; } = [];
}
