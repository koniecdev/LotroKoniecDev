namespace LotroKoniecDev.TranslationSystem.API.Settings;

/// <summary>
/// Empty is valid only in Development, which uses the open policy, and in Testing, which runs on one
/// origin (ADR-0008 §3).
/// </summary>
internal sealed class CorsSettings
{
    public const string ConfigurationSection = "Cors";

    public string[] AllowedOrigins { get; init; } = [];
}
