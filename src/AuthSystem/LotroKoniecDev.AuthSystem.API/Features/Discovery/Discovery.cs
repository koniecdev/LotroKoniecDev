using System.Security.Claims;
using LotroKoniecDev.AuthSystem.API.Common;
using LotroKoniecDev.AuthSystem.API.Hateoas.DiscoveryFactories;
using LotroKoniecDev.AuthSystem.Contracts.Discovery;
using LotroKoniecDev.Hateoas.ContentNegotiation;

namespace LotroKoniecDev.AuthSystem.API.Features.Discovery;

internal sealed class Discovery : IApiEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder endpointRouteBuilder)
    {
        endpointRouteBuilder.MapGet("", (
                ClaimsPrincipal user,
                IDiscoveryLinkFactory discoveryLinkFactory) =>
            {
                DiscoveryResponse response = new("LotroKoniecDev.AuthSystem");

                return HateoasResults.Ok(response, r =>
                    r.Links = discoveryLinkFactory.CreateDiscoveryLinks(
                        user.Identity?.IsAuthenticated is true));
            })
            .AllowAnonymous()
            .WithName(nameof(Discovery))
            .WithTags("Discovery")
            .Produces<DiscoveryResponse>();
    }
}
