namespace LotroKoniecDev.Hateoas.Abstractions;

/// <summary>
/// The frontend adds both headers to every call it makes to the auth API and the TMS API while serving
/// a visitor (ADR-0054). Without the per-environment key, the API ignores the address and meters the
/// call on the connection's own. Both APIs and the frontend read this one copy, so the ends cannot drift.
/// </summary>
public static class FrontendCallerHeaders
{
    public const string Key = "X-LOTRO-Frontend-Key";

    public const string ClientAddress = "X-LOTRO-Client-Address";
}
