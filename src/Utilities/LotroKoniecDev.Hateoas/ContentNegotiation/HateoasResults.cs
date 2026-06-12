using Microsoft.AspNetCore.Http;

namespace LotroKoniecDev.Hateoas.ContentNegotiation;

/// <summary>
/// Factory helpers that mirror <see cref="Results"/> but perform HATEOAS
/// content negotiation. The supplied <c>attachLinks</c> delegate is invoked
/// only when the client has explicitly requested the HATEOAS vendor media
/// type; clients that accept plain JSON receive the unadorned payload.
/// </summary>
public static class HateoasResults
{
    /// <summary>
    /// Produces a 200 OK response. When the client accepts the HATEOAS
    /// vendor media type, <paramref name="attachLinks"/> is executed to
    /// populate the response's hypermedia links before serialization.
    /// </summary>
    public static IResult Ok<T>(T response, Action<T>? attachLinks = null)
        where T : class
        => new HateoasNegotiatedResult<T>(response, attachLinks, StatusCodes.Status200OK);
}
