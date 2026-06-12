using System.Reflection;
using LotroKoniecDev.AuthSystem.API.Common;

namespace LotroKoniecDev.AuthSystem.API.Extensions;

/// <summary>
/// Provides extension methods for registering and configuring endpoint-related services in a DI container.
/// </summary>
internal static class EndpointExtensions
{
    public static IServiceCollection AddEndpoints(this IServiceCollection services, Assembly assembly)
    {
        IEnumerable<ServiceDescriptor> serviceDescriptors = assembly
            .DefinedTypes
            .Where(type => type is { IsAbstract: false, IsInterface: false }
                           && type.IsAssignableTo(typeof(IEndpoint)))
            .Select(type => ServiceDescriptor.Transient(typeof(IEndpoint), type));

        foreach (ServiceDescriptor serviceDescriptor in serviceDescriptors)
        {
            services.Add(serviceDescriptor);
        }

        return services;
    }

    extension(WebApplication app)
    {
        /// <summary>
        /// Maps application API endpoints — i.e. every <see cref="IApiEndpoint"/>.
        /// Mounted directly at the root; grouped only so rate limiting can target them
        /// collectively without also covering OpenIddict's <c>/connect/*</c> surface.
        /// </summary>
        /// <param name="routeGroupBuilder">Rate-limited root <see cref="RouteGroupBuilder"/>.</param>
        /// <returns>The <see cref="IApplicationBuilder"/> for further configuration.</returns>
        public IApplicationBuilder MapApiEndpoints(RouteGroupBuilder routeGroupBuilder)
        {
            IEnumerable<IEndpoint> endpoints = app.Services.GetRequiredService<IEnumerable<IEndpoint>>();

            foreach (IEndpoint endpoint in endpoints.Where(endpoint => endpoint is IApiEndpoint))
            {
                endpoint.MapEndpoint(routeGroupBuilder);
            }

            return app;
        }

        /// <summary>
        /// Maps endpoints that live at the application root — everything that is a plain
        /// <see cref="IEndpoint"/> but not an <see cref="IApiEndpoint"/>. This covers OpenIddict's
        /// <c>/connect/*</c> surface.
        /// </summary>
        /// <returns>The <see cref="IApplicationBuilder"/> for further configuration.</returns>
        public IApplicationBuilder MapRootEndpoints()
        {
            IEnumerable<IEndpoint> endpoints = app.Services.GetRequiredService<IEnumerable<IEndpoint>>();

            foreach (IEndpoint endpoint in endpoints.Where(endpoint => endpoint is not IApiEndpoint))
            {
                endpoint.MapEndpoint(app);
            }

            return app;
        }
    }
}
