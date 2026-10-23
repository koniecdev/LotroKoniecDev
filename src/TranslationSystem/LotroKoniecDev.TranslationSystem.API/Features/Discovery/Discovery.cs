using LotroKoniecDev.Hateoas.ContentNegotiation;
using LotroKoniecDev.TranslationSystem.API.Common;
using LotroKoniecDev.TranslationSystem.API.Hateoas.DiscoveryFactories;
using LotroKoniecDev.TranslationSystem.Contracts.Discovery;

namespace LotroKoniecDev.TranslationSystem.API.Features.Discovery;

internal sealed class Discovery : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder endpointRouteBuilder)
    {
        // Open to anyone (#608, ADR-0040): it shows only what the caller can already reach, and a
        // client without a login (the CLI) has no other way to start. Still rate-limited.
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
