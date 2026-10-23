namespace LotroKoniecDev.Hateoas.Abstractions;

/// <summary>
/// Opens the full /health on the auth API and the TMS API (ADR-0058). The daily health ping
/// (<c>health-ping.yml</c>) sends the same name from bash, so a rename here has to change that workflow too.
/// </summary>
public static class HealthCheckHeaders
{
    public const string Key = "X-LOTRO-Health-Key";
}
