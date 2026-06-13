using LotroKoniecDev.TranslationSystem.Persistence;

namespace LotroKoniecDev.TranslationSystem.API;

internal static class RootDependencyInjection
{
    public static IServiceCollection AddTranslationSystem(this IServiceCollection services)
    {
        services.AddTranslationPersistence();
        services.AddApi();

        return services;
    }
}
