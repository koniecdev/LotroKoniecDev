namespace LotroKoniecDev.AuthSystem.Contracts.Common;

/// <summary>
/// The two headers the frontend adds to every auth API call it makes while serving a visitor
/// (ADR-0054): the visitor's address, and the per-environment key without which the auth API ignores
/// that address and meters the call on the connection's own.
/// </summary>
public static class FrontendCallerHeaders
{
    public const string Key = "X-LOTRO-Frontend-Key";

    public const string ClientAddress = "X-LOTRO-Client-Address";
}
