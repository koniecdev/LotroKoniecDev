namespace LotroKoniecDev.Hateoas.Abstractions;

/// <summary>
/// Media types supported by LotroKoniecDev APIs for content negotiation.
/// The HATEOAS vendor type is opt-in: clients that want hypermedia links
/// explicitly request <see cref="HateoasJson"/>, otherwise they receive a
/// plain <see cref="Json"/> representation without links.
/// <para>
/// The vendor media type is a cross-cutting "opt into HATEOAS" convention
/// shared by every LotroKoniecDev API. It is deliberately centralised here so
/// that the contract evolves atomically across services.
/// </para>
/// </summary>
public static class MediaTypes
{
    /// <summary>
    /// Standard JSON — returned without HATEOAS links by default.
    /// </summary>
    public const string Json = "application/json";

    /// <summary>
    /// Vendor-specific JSON that includes HATEOAS hypermedia links.
    /// Clients opt in by sending this value in the Accept header.
    /// </summary>
    public const string HateoasJson = "application/vnd.dev-lotrokoniecdev.hateoas.json";
}
