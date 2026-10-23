using System.Diagnostics;
using LotroKoniecDev.AuthSystem.API.Common;

namespace LotroKoniecDev.AuthSystem.API.Features.Auth;

/// <summary>
/// OpenIddict always serves <c>connect/introspect</c> and <c>connect/revoke</c> itself during
/// authentication, so a handler here never runs (#349). These routes exist only to carry the brute-force
/// rate limit, which the limiter reads from the matched endpoint; without them the client secret could
/// be guessed without limit. A non-POST request gets routing's 405 with no limiter, which is harmless
/// only because OpenIddict refuses it before checking the client (ADR-0061).
/// </summary>
internal sealed class MiddlewareServedEndpoints : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder endpointRouteBuilder)
    {
        MapRateLimitMetadataRoute(endpointRouteBuilder, "connect/introspect");
        MapRateLimitMetadataRoute(endpointRouteBuilder, "connect/revoke");
    }

    private static void MapRateLimitMetadataRoute(IEndpointRouteBuilder endpointRouteBuilder, string pattern)
    {
        endpointRouteBuilder.MapPost(pattern, Handle)
            .AllowAnonymous()
            .ExcludeFromDescription();
    }

    private static IResult Handle(HttpContext httpContext)
        => throw new UnreachableException(
            $"'{httpContext.Request.Path}' must be served by the OpenIddict middleware during the "
            + "authentication stage. Reaching this delegate means the OpenIddict endpoint URIs "
            + "(SetIntrospectionEndpointUris/SetRevocationEndpointUris) and the routes mapped here "
            + "have drifted apart — realign them.");
}
