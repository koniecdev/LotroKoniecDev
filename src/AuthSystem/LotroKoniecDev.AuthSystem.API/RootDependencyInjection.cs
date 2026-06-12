using LotroKoniecDev.AuthSystem.Infrastructure;
using LotroKoniecDev.AuthSystem.Persistence;

namespace LotroKoniecDev.AuthSystem.API;

internal static class RootDependencyInjection
{
    public static IServiceCollection AddAuthSystem(
        this IServiceCollection services,
        IWebHostEnvironment environment)
    {
        services.AddInfrastructure();
        services.AddAuthPersistence();
        services.AddAuthApi(environment);

        return services;
    }
}
