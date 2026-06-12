using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;
using LotroKoniecDev.Hateoas.Abstractions;

namespace LotroKoniecDev.Hateoas.ContentNegotiation;

/// <summary>
/// An <see cref="IResult"/> that performs content negotiation between the
/// plain JSON and HATEOAS vendor representations for a single payload.
/// <para>
/// The class is named "negotiated" because it is the *decision point*, not a
/// HATEOAS-only response: when the client accepts the HATEOAS vendor media
/// type, the supplied link-attacher is invoked and the response is served as
/// <c>application/vnd.dev-lotrokoniecdev.hateoas.json</c>; otherwise the same
/// payload is serialized as plain <c>application/json</c> with no links
/// attached (and, thanks to <see cref="HateoasJsonTypeInfoModifiers"/>, no
/// <c>links</c> key at all).
/// </para>
/// <para>
/// The <c>Vary: Accept</c> header is appended (not overwritten) so shared
/// caches treat the two representations as distinct entities without losing
/// Vary tokens set by upstream middleware such as response compression.
/// </para>
/// </summary>
/// <typeparam name="T">The response payload type.</typeparam>
internal sealed class HateoasNegotiatedResult<T> : IResult
    where T : class
{
    private readonly T _response;
    private readonly Action<T>? _linkAttacher;
    private readonly int _statusCode;

    public HateoasNegotiatedResult(T response, Action<T>? linkAttacher, int statusCode)
    {
        ArgumentNullException.ThrowIfNull(response);
        _response = response;
        _linkAttacher = linkAttacher;
        _statusCode = statusCode;
    }

    public async Task ExecuteAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        bool includeHateoas = _linkAttacher is not null
                              && HateoasContentNegotiator.ShouldIncludeHateoas(httpContext.Request);

        if (includeHateoas)
        {
            _linkAttacher!(_response);
        }

        // Append (never overwrite) so Vary tokens set by upstream middleware
        // — notably response compression's "Accept-Encoding" — are preserved.
        httpContext.Response.Headers.Append(HeaderNames.Vary, HeaderNames.Accept);
        httpContext.Response.StatusCode = _statusCode;

        string contentType = includeHateoas ? MediaTypes.HateoasJson : MediaTypes.Json;

        // Passing null for the JsonSerializerOptions parameter lets ASP.NET
        // Core resolve the registered Http JSON options from DI internally,
        // so our code does no service location and the HateoasJsonTypeInfo-
        // Modifiers contract modifier is still applied.
        await httpContext.Response.WriteAsJsonAsync(
            _response,
            options: null,
            contentType: contentType,
            cancellationToken: httpContext.RequestAborted);
    }
}
