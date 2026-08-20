namespace LotroKoniecDev.Hateoas.Abstractions;

/// <summary>
/// The media types LotroKoniecDev APIs negotiate on. Links are opt-in: a client that wants them asks
/// for <see cref="HateoasJson"/>, and anyone else gets plain <see cref="Json"/> without links.
/// <para>
/// Every LotroKoniecDev API shares this convention, so the constant lives here and all services
/// change together.
/// </para>
/// </summary>
public static class MediaTypes
{
    /// <summary>
    /// Plain JSON. This is the default, and it carries no links.
    /// </summary>
    public const string Json = "application/json";

    /// <summary>
    /// Our own JSON type, the one that carries hypermedia links. A client opts in by sending this
    /// value in the Accept header.
    /// </summary>
    public const string HateoasJson = "application/vnd.dev-lotrokoniecdev.hateoas.json";
}
