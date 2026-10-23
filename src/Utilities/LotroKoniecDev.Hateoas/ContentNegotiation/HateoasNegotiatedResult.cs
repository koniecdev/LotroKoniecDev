using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;
using LotroKoniecDev.Hateoas.Abstractions;

namespace LotroKoniecDev.Hateoas.ContentNegotiation;

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

        // Append, never overwrite: other middleware, such as response compression, adds its own Vary
        // tokens.
        httpContext.Response.Headers.Append(HeaderNames.Vary, HeaderNames.Accept);
        httpContext.Response.StatusCode = _statusCode;

        string contentType = includeHateoas ? MediaTypes.HateoasJson : MediaTypes.Json;

        // null takes the registered HTTP JSON options, so the HateoasJsonTypeInfoModifiers modifier
        // still runs.
        await httpContext.Response.WriteAsJsonAsync(
            _response,
            options: null,
            contentType: contentType,
            cancellationToken: httpContext.RequestAborted);
    }
}
