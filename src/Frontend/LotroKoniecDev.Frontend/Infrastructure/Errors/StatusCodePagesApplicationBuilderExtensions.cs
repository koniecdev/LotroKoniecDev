using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;

namespace LotroKoniecDev.Frontend.Infrastructure.Errors;

/// <summary>
/// Like the built-in <c>UseStatusCodePagesWithReExecute</c>, but with a page per status code, so each
/// error lands on a page that says what really happened. The error page always runs as GET (#1555). The
/// pages take only GET and POST, and a POST run would check the antiforgery token again: ASP.NET Core
/// throws when a form whose token check failed is read. That failed check stays on the request, so an
/// error page and its layout must never read the form.
/// </summary>
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
                string originalMethod = httpContext.Request.Method;

                httpContext.Features.Set<IStatusCodeReExecuteFeature>(new ReExecuteFeature
                {
                    OriginalPath = originalPath.Value ?? string.Empty,
                    OriginalPathBase = originalPathBase.Value ?? string.Empty,
                    OriginalQueryString = originalQueryString.HasValue ? originalQueryString.Value : null,
                    OriginalStatusCode = statusCode
                });

                // EndpointRoutingMiddleware stops early when an endpoint is already set, so without this
                // the new path would run the original endpoint, which no longer applies.
                httpContext.SetEndpoint(endpoint: null);
                httpContext.Features.Get<IRouteValuesFeature>()?.RouteValues?.Clear();

                httpContext.Request.Path = new PathString(newPath);
                httpContext.Request.QueryString = QueryString.Empty;
                httpContext.Request.Method = HttpMethods.Get;

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
                    // The request log line is written after this and must show the real method.
                    httpContext.Request.Method = originalMethod;
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
