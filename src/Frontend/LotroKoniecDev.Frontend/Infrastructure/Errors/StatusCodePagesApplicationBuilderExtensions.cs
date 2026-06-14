using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;

namespace LotroKoniecDev.Frontend.Infrastructure.Errors;

/// <summary>
/// Same contract as the built-in <c>UseStatusCodePagesWithReExecute(string, string?)</c> but lets the
/// host pick a re-execute path per status code, so 401/403/404/5xx each land on a page that honestly
/// represents what happened instead of pretending every 4xx/5xx is a 404.
/// </summary>
/// <remarks>
/// Mirrors the runtime implementation: sets <see cref="IStatusCodeReExecuteFeature"/> so middleware can
/// recognise the re-executed request, clears the routed endpoint so the rewritten path is matched
/// again, and restores request state in <c>finally</c>.
/// </remarks>
public static class StatusCodePagesApplicationBuilderExtensions
{
    extension(IApplicationBuilder app)
    {
        public IApplicationBuilder UseStatusCodePagesByStatusCode(
            Func<int, string> pathSelector,
            bool createScopeForStatusCodePages = false)
        {
            ArgumentNullException.ThrowIfNull(pathSelector);

            return app.UseStatusCodePages(async context =>
            {
                HttpContext httpContext = context.HttpContext;
                int statusCode = httpContext.Response.StatusCode;

                string newPath = pathSelector(statusCode);
                if (string.IsNullOrWhiteSpace(newPath))
                {
                    return;
                }

                PathString originalPath = httpContext.Request.Path;
                PathString originalPathBase = httpContext.Request.PathBase;
                QueryString originalQueryString = httpContext.Request.QueryString;

                httpContext.Features.Set<IStatusCodeReExecuteFeature>(new ReExecuteFeature
                {
                    OriginalPath = originalPath.Value ?? string.Empty,
                    OriginalPathBase = originalPathBase.Value ?? string.Empty,
                    OriginalQueryString = originalQueryString.HasValue ? originalQueryString.Value : null,
                    OriginalStatusCode = statusCode
                });

                // EndpointRoutingMiddleware short-circuits when an endpoint is already set, so the
                // rewritten path would otherwise hit the original (now-irrelevant) endpoint.
                httpContext.SetEndpoint(endpoint: null);
                httpContext.Features.Get<IRouteValuesFeature>()?.RouteValues?.Clear();

                httpContext.Request.Path = new PathString(newPath);
                httpContext.Request.QueryString = QueryString.Empty;

                try
                {
                    if (createScopeForStatusCodePages)
                    {
                        IServiceScopeFactory scopeFactory = httpContext.RequestServices
                            .GetRequiredService<IServiceScopeFactory>();
                        using IServiceScope scope = scopeFactory.CreateScope();
                        IServiceProvider previousServices = httpContext.RequestServices;
                        httpContext.RequestServices = scope.ServiceProvider;
                        try
                        {
                            await context.Next(httpContext);
                        }
                        finally
                        {
                            httpContext.RequestServices = previousServices;
                        }
                    }
                    else
                    {
                        await context.Next(httpContext);
                    }
                }
                finally
                {
                    httpContext.Request.QueryString = originalQueryString;
                    httpContext.Request.Path = originalPath;
                    httpContext.Features.Set<IStatusCodeReExecuteFeature?>(null);
                }
            });
        }
    }

    private sealed class ReExecuteFeature : IStatusCodeReExecuteFeature
    {
        public string OriginalPath { get; set; } = string.Empty;
        public string OriginalPathBase { get; set; } = string.Empty;
        public string? OriginalQueryString { get; set; }
        public int OriginalStatusCode { get; set; }
    }
}
