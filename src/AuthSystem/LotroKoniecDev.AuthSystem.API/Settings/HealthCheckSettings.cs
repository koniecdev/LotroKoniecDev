namespace LotroKoniecDev.AuthSystem.API.Settings;

/// <summary>
/// The per-environment key that opens the full /health (ADR-0058, #853). The box <c>.env</c> carries it
/// as <c>HEALTH_CHECK_KEY</c>, and compose hands the same value to both APIs. The daily health ping
/// sends it.
/// </summary>
internal sealed class HealthCheckSettings
{
    public const string ConfigurationSection = "HealthCheck";

    public string? Key { get; init; }
}
