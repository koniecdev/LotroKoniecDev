using LotroKoniecDev.Hateoas.ContentNegotiation;
using LotroKoniecDev.TranslationSystem.API.Common;
using LotroKoniecDev.TranslationSystem.API.Hateoas.DiscoveryFactories;
using LotroKoniecDev.TranslationSystem.Contracts.Discovery;

namespace LotroKoniecDev.TranslationSystem.API.Features.Discovery;

internal sealed class Discovery : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder endpointRouteBuilder)
    {
        // The root is anonymous on purpose (#608): the endpoints it advertises to an anonymous
        // caller are themselves anonymous, so nothing leaks that the caller could not already
        // reach, and an unauthenticated client (the CLI, later the Avalonia app) has no other way
        // to bootstrap. The admin surface stays hidden through claims-aware link emission — the
        // link factory replays each target endpoint's own policy — not by walling the root off.
        // It still sits inside the rate-limited endpoint group mapped in Program.cs.
        endpointRouteBuilder.MapGet("", (IDiscoveryLinkFactory discoveryLinkFactory) =>
            {
                DiscoveryResponse response = new("LotroKoniecDev.TranslationSystem");

                return HateoasResults.Ok(response, async r =>
                    r.Links = await discoveryLinkFactory.CreateDiscoveryLinksAsync());
            })
            .AllowAnonymous()
            .WithName(nameof(Discovery))
            .WithTags("Discovery")
            .Produces<DiscoveryResponse>(StatusCodes.Status200OK);
    }
}
