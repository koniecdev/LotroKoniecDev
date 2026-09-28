using System.Diagnostics;
using LotroKoniecDev.AuthSystem.API.Common;

namespace LotroKoniecDev.AuthSystem.API.Features.Auth;

/// <summary>
/// Maps routes that carry metadata only, for the two OpenIddict endpoints ASP.NET Core cannot hand
/// over to us: <c>connect/introspect</c> and <c>connect/revoke</c>.
/// OpenIddict only passes through the token, authorize, userinfo and end-session endpoints, plus
/// verification and error. Introspection and revocation are always served by the OpenIddict middleware
/// during authentication, so a handler routed here could never run. That is what #349 found: the old
/// RevokeEndpoint handler was dead code.
/// These routes exist only to carry the brute-force rate-limit policy. The limiter runs after routing
/// and before authentication (#347) and reads the policy from the matched endpoint's metadata, so
/// without a route these URIs would allow unlimited guessing of the client secret.
/// A non-POST request lands on routing's 405 endpoint, which carries no limiter. That is harmless only
/// because OpenIddict refuses it before it checks the client: revocation by default, introspection
/// because <c>OpenIddictExtensions</c> makes it read POST only (ADR-0061).
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
