using Microsoft.AspNetCore.Http;

namespace LotroKoniecDev.Hateoas.ContentNegotiation;

/// <summary>
/// Helpers that mirror <see cref="Results"/> but negotiate the links first. The <c>attachLinks</c>
/// delegate runs only when the client asked for the vendor media type by name. Everyone else gets the
/// payload without links.
/// </summary>
public static class HateoasResults
{
    /// <summary>
    /// Returns 200 OK. When the client accepts the vendor media type,
    /// <paramref name="attachLinks"/> fills in the links before the response is written.
    /// </summary>
    public static IResult Ok<T>(T response, Func<T, ValueTask>? attachLinks = null)
        where T : class
        => new HateoasNegotiatedResult<T>(response, attachLinks, StatusCodes.Status200OK);
}
