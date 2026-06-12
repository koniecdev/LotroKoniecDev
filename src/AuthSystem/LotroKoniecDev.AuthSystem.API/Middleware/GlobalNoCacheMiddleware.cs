using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Net.Http.Headers;

namespace LotroKoniecDev.AuthSystem.API.Middleware;

public sealed class GlobalNoCacheMiddleware
{
    private readonly RequestDelegate _next;

    public GlobalNoCacheMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(() =>
        {
            IHeaderDictionary responseHeaders = context.Response.Headers;

            if (responseHeaders.ContainsKey(HeaderNames.CacheControl))
            {
                return Task.CompletedTask;
            }

            Endpoint? endpoint = context.GetEndpoint();

            bool hasOutputCache = endpoint?.Metadata.GetMetadata<IOutputCachePolicy>() is not null;

            if (hasOutputCache)
            {
                return Task.CompletedTask;
            }

            responseHeaders[HeaderNames.CacheControl] = "no-store, no-cache, max-age=0, must-revalidate";
            responseHeaders[HeaderNames.Pragma] = "no-cache";
            responseHeaders[HeaderNames.Expires] = "0";

            return Task.CompletedTask;
        });

        await _next(context);
    }
}
