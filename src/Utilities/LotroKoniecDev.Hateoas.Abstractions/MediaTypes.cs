namespace LotroKoniecDev.Hateoas.Abstractions;

/// <summary>
/// Links are opt-in: a client that wants them sends <see cref="HateoasJson"/> in its Accept header, and
/// anyone else gets plain <see cref="Json"/> without links. The APIs, the frontend and the patcher CLI
/// all read this one copy, so the two ends cannot drift. It is the reason the patcher may reference
/// this assembly at all.
/// </summary>
public static class MediaTypes
{
    public const string Json = "application/json";

    public const string HateoasJson = "application/vnd.dev-lotrokoniecdev.hateoas.json";
}
