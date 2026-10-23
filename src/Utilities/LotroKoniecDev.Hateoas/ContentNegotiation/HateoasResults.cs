using Microsoft.AspNetCore.Http;

namespace LotroKoniecDev.Hateoas.ContentNegotiation;

/// <summary>
/// Like <see cref="Results"/>, but <c>attachLinks</c> runs only when the client asked for the vendor
/// media type by name. Everyone else gets the payload without links.
/// </summary>
public static class HateoasResults
{
    public static IResult Ok<T>(T response, Func<T, ValueTask>? attachLinks = null)
        where T : class
        => new HateoasNegotiatedResult<T>(response, attachLinks, StatusCodes.Status200OK);
}
