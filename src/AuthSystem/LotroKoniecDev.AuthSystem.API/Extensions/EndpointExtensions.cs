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
        /// Maps every <see cref="IApiEndpoint"/>. They sit at the root; the group exists only so rate
        /// limiting can cover them all without also covering OpenIddict's <c>/connect/*</c> endpoints.
        /// </summary>
        /// <param name="routeGroupBuilder">The rate-limited root <see cref="RouteGroupBuilder"/>.</param>
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
        /// Maps the endpoints at the application root: everything that is a plain
        /// <see cref="IEndpoint"/> but not an <see cref="IApiEndpoint"/>, which is OpenIddict's
        /// <c>/connect/*</c> endpoints. They sit at the root; the group exists only so the
        /// brute-force rate-limit policy really applies to them, because a group convention reaches
        /// only the endpoints mapped through that group.
        /// </summary>
        /// <param name="routeGroupBuilder">The rate-limited root <see cref="RouteGroupBuilder"/>.</param>
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
