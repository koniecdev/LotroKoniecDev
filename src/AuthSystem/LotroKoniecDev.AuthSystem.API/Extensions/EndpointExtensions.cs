using System.Reflection;
using LotroKoniecDev.AuthSystem.API.Common;

namespace LotroKoniecDev.AuthSystem.API.Extensions;

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
        /// The group exists only so one rate limit covers every <see cref="IApiEndpoint"/> without also
        /// covering OpenIddict's <c>/connect/*</c> endpoints.
        /// </summary>
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
        /// Maps every <see cref="IEndpoint"/> that is not an <see cref="IApiEndpoint"/>: OpenIddict's
        /// <c>/connect/*</c> endpoints. The group exists only so the brute-force limit reaches them.
        /// </summary>
        public IApplicationBuilder MapRootEndpoints(RouteGroupBuilder routeGroupBuilder)
        {
            IEnumerable<IEndpoint> endpoints = app.Services.GetRequiredService<IEnumerable<IEndpoint>>();

            foreach (IEndpoint endpoint in endpoints.Where(endpoint => endpoint is not IApiEndpoint))
            {
                endpoint.MapEndpoint(routeGroupBuilder);
            }

            return app;
        }
    }
}
