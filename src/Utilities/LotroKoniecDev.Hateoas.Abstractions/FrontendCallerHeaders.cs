namespace LotroKoniecDev.Hateoas.Abstractions;

/// <summary>
/// The two headers the frontend adds to every call it makes to the auth API and the TMS API while
/// serving a visitor (ADR-0054): the visitor's address, and the per-environment key without which the
/// API ignores that address and meters the call on the connection's own. Both APIs and the frontend
/// compile against this one copy, like <see cref="MediaTypes"/>, so the ends cannot drift.
/// </summary>
public static class FrontendCallerHeaders
{
    public const string Key = "X-LOTRO-Frontend-Key";

    public const string ClientAddress = "X-LOTRO-Client-Address";
}
