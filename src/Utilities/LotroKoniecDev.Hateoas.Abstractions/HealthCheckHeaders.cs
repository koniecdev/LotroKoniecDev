namespace LotroKoniecDev.Hateoas.Abstractions;

/// <summary>
/// The header that opens the full /health on the auth API and the TMS API (ADR-0058). Both APIs compile
/// against this one copy, like <see cref="FrontendCallerHeaders"/>. The daily health ping sends the same
/// name from bash, so a rename here has to change that workflow too.
/// </summary>
public static class HealthCheckHeaders
{
    public const string Key = "X-LOTRO-Health-Key";
}
