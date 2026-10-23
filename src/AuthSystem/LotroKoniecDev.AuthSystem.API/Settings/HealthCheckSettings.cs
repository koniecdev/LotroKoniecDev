namespace LotroKoniecDev.AuthSystem.API.Settings;

/// <summary>
/// The key that opens the full /health (ADR-0058). Compose hands the box's <c>HEALTH_CHECK_KEY</c> to
/// both APIs, and the daily health ping sends it.
/// </summary>
internal sealed class HealthCheckSettings
{
    public const string ConfigurationSection = "HealthCheck";

    public string? Key { get; init; }
}
