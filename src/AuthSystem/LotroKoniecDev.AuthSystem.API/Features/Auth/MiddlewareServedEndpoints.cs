using System.Diagnostics;
using LotroKoniecDev.AuthSystem.API.Common;

namespace LotroKoniecDev.AuthSystem.API.Features.Auth;

/// <summary>
/// Maps metadata-only routes for the OpenIddict endpoints that have no ASP.NET Core passthrough:
/// <c>connect/introspect</c> and <c>connect/revoke</c>. OpenIddict exposes passthrough only for the
/// token/authorize/userinfo/end-session (and verification/error) endpoints — introspection and
/// revocation are always served by the OpenIddict middleware inside the authentication stage, so a
/// routed handler here can never run (#349: the old RevokeEndpoint handler was unreachable dead
/// code). These routes exist solely to carry the brute-force rate-limit policy: the limiter runs
/// after routing and BEFORE authentication (#347) and reads the policy from the matched endpoint's
/// metadata — without a routed endpoint these URIs would take unlimited client-secret guessing.
/// Non-POST requests match no route (and thus no limiter), which is a credential-free residual:
/// OpenIddict rejects non-POST protocol requests before client authentication ever runs.
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
