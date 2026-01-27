using LotroKoniecDev.Application.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace LotroKoniecDev.Infrastructure;

/// <summary>
/// Extension methods for registering infrastructure layer services.
/// </summary>
public static class InfrastructureDependencyInjection
{
    /// <summary>
    /// Adds infrastructure layer services to the service collection.
    /// </summary>
    public static IServiceCollection AddInfrastructureServices(this IServiceCollection services)
    {
        services.AddScoped<IDatFileHandler, DatFileHandler>();

        return services;
    }
}
