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
        services.AddSingleton<IDatFileLocator, DatFileLocator>();
        services.AddSingleton<IGameProcessDetector, GameProcessDetector>();
        services.AddSingleton<IWriteAccessChecker, WriteAccessChecker>();

        return services;
    }
}
