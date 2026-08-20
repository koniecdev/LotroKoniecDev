using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;
using LotroKoniecDev.Hateoas.Abstractions;

namespace LotroKoniecDev.Hateoas.ContentNegotiation;

/// <summary>
/// An <see cref="IResult"/> that chooses between plain JSON and our link-carrying JSON for one
/// payload.
/// <para>
/// It is called "negotiated" because this is where the choice is made, not because it always returns
/// links. When the client accepts the vendor media type, the link-attacher runs and the response goes
/// out as <c>application/vnd.dev-lotrokoniecdev.hateoas.json</c>. Otherwise the same payload goes out
/// as plain <c>application/json</c> with no links, and thanks to
/// <see cref="HateoasJsonTypeInfoModifiers"/> without a <c>links</c> key at all.
/// </para>
/// <para>
/// <c>Vary: Accept</c> is appended instead of overwritten, so shared caches keep the two responses
/// apart and Vary tokens set by other middleware, such as response compression, survive.
/// </para>
/// </summary>
/// <typeparam name="T">The response payload type.</typeparam>
internal sealed class HateoasNegotiatedResult<T> : IResult
    where T : class
{
    private readonly T _response;
    private readonly Func<T, ValueTask>? _linkAttacher;
    private readonly int _statusCode;

    public HateoasNegotiatedResult(T response, Func<T, ValueTask>? linkAttacher, int statusCode)
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
            await _linkAttacher!(_response);
        }

        // Append, never overwrite, so Vary tokens set by other middleware survive. Response
        // compression adds "Accept-Encoding" here.
        httpContext.Response.Headers.Append(HeaderNames.Vary, HeaderNames.Accept);
        httpContext.Response.StatusCode = _statusCode;

        string contentType = includeHateoas ? MediaTypes.HateoasJson : MediaTypes.Json;

        // Passing null for the JsonSerializerOptions lets ASP.NET Core take the registered HTTP JSON
        // options from DI itself. We do not resolve services by hand, and the
        // HateoasJsonTypeInfoModifiers contract modifier still runs.
        await httpContext.Response.WriteAsJsonAsync(
            _response,
            options: null,
            contentType: contentType,
            cancellationToken: httpContext.RequestAborted);
    }
}
