using LotroKoniecDev.Hateoas.ContentNegotiation;
using LotroKoniecDev.TranslationSystem.API.Common;
using LotroKoniecDev.TranslationSystem.API.Hateoas.DiscoveryFactories;
using LotroKoniecDev.TranslationSystem.Contracts.Discovery;

namespace LotroKoniecDev.TranslationSystem.API.Features.Discovery;

internal sealed class Discovery : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder endpointRouteBuilder)
    {
        // The root is open to anyone on purpose (#608). What it shows an anonymous caller is itself
        // anonymous, so nothing appears that the caller could not already reach, and a client without a
        // login, the CLI today and the Avalonia app later, has no other way to start.
        // The admin endpoints stay hidden because the link factory checks each target endpoint's own
        // policy before it emits a link, not because the root is closed.
        // It is still inside the rate-limited endpoint group mapped in Program.cs.
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
