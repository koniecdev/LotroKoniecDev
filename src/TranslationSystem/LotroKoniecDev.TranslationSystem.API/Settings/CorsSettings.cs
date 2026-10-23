namespace LotroKoniecDev.TranslationSystem.API.Settings;

/// <summary>
/// Empty is valid only in Development and Testing, which never read the list (ADR-0008 §3).
/// </summary>
internal sealed class CorsSettings
{
    public const string ConfigurationSection = "Cors";

    public string[] AllowedOrigins { get; init; } = [];
}
