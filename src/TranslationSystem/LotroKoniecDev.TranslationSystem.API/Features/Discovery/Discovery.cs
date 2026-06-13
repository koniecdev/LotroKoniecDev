using LotroKoniecDev.Hateoas.ContentNegotiation;
using LotroKoniecDev.TranslationSystem.API.Common;
using LotroKoniecDev.TranslationSystem.API.Hateoas.DiscoveryFactories;
using LotroKoniecDev.TranslationSystem.Contracts.Discovery;

namespace LotroKoniecDev.TranslationSystem.API.Features.Discovery;

internal sealed class Discovery : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder endpointRouteBuilder)
    {
        // No explicit authorization metadata — the fallback policy applies, which makes this
        // the canonical authorized-by-default endpoint (anonymous requests get 401).
        endpointRouteBuilder.MapGet("", (IDiscoveryLinkFactory discoveryLinkFactory) =>
            {
                DiscoveryResponse response = new("LotroKoniecDev.TranslationSystem");

                return HateoasResults.Ok(response, r =>
                    r.Links = discoveryLinkFactory.CreateDiscoveryLinks());
            })
            .WithName(nameof(Discovery))
            .WithTags("Discovery")
            .Produces<DiscoveryResponse>(StatusCodes.Status200OK);
    }
}
